using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// How a checksum stops being trustworthy, and what that must cost (FR-CHECKSUM, FR-SCRUB).
///
/// The bargain the engine strikes is: reads are trusted and never verified, because hashing every
/// block served would trade throughput away for a check that nearly always passes. Instead a write
/// records what it can and MARKS what it cannot, and a scheduled sweep re-baselines the marks.
///
/// Two properties make that bargain hold, and neither is obvious:
/// <list type="bullet">
/// <item>a stale entry must never be believed — trusting a hash that predates a known write would
/// report a perfectly good file as rotted, and the "repair" would then overwrite the newer content
/// with the older, turning an integrity check into data loss;</item>
/// <item>marking must be idempotent — a database file or a VM image is rewritten continuously, and
/// re-marking on every write would dirty the checksum database on every write, which is the O(1)
/// version of the O(file) cost the marking exists to avoid.</item>
/// </list>
/// </summary>
[TestFixture]
[Category("Unit")]
public class ChecksumStalenessTests {

  [Test]
  [Category("HappyPath")]
  public void Cadence_GivenTheScheduleFormsTheConfigurationUses_ThenEachResolvesToItsPeriod() {
    PoolFileSystem.ScrubCadence("idle-weekly", TimeSpan.Zero).Should().Be(TimeSpan.FromDays(7),
      "'idle-weekly' is the shipped default and must mean a week, not nothing");
    PoolFileSystem.ScrubCadence("daily", TimeSpan.Zero).Should().Be(TimeSpan.FromDays(1));
    PoolFileSystem.ScrubCadence("idle-monthly", TimeSpan.Zero).Should().Be(TimeSpan.FromDays(30));
    PoolFileSystem.ScrubCadence("36h", TimeSpan.Zero).Should().Be(TimeSpan.FromHours(36),
      "a plain duration must work too — a cadence is not a fixed menu");
  }

  [Test]
  [Category("EdgeCase")]
  public void Cadence_GivenItIsTurnedOffOrUnreadable_ThenNothingIsScheduledOrTheFallbackApplies() {
    foreach (var off in new[] { "off", "never", "manual", "" })
      PoolFileSystem.ScrubCadence(off, TimeSpan.FromDays(1)).Should().Be(TimeSpan.Zero,
        $"'{off}' must disable the sweep rather than quietly running it on some default");

    PoolFileSystem.ScrubCadence(null, TimeSpan.FromDays(3)).Should().Be(TimeSpan.FromDays(3),
      "an unset schedule takes the fallback the caller chose");

    // an unreadable value must not take the pool down, and must not silently mean "never" either:
    // a typo in a config file should degrade to sweeping, not to no protection at all
    PoolFileSystem.ScrubCadence("every other tuesday", TimeSpan.FromDays(1)).Should().Be(TimeSpan.FromDays(1));
  }

  [Test]
  [Category("HappyPath")]
  public void Entry_GivenItIsFresh_ThenItIsNotStale() {
    new ChecksumEntry(10, 20, "AB").Stale.Should().BeFalse(
      "a recorded checksum is trustworthy until something says otherwise — the flag has to default off");
  }

  [Test]
  [Category("EdgeCase")]
  public void Entry_WhenMarkedStale_ThenTheHashIsKeptButFlagged() {
    var entry = new ChecksumEntry(10, 20, "AB") with { Stale = true };

    entry.Stale.Should().BeTrue();
    entry.Hash.Should().Be("AB",
      "the old hash is kept rather than blanked: a scan can then tell 'known and now dirty' from "
      + "'never baselined', which are different situations and deserve different reporting");
  }

  [Test]
  [Category("Performance")]
  public void Marking_GivenAFileIsRewrittenRepeatedly_ThenTheDatabaseIsDirtiedOnlyOnce() {
    var member = new InMemoryVolume();
    var database = new ChecksumDatabase(member);
    database.Set("hot.bin", new(1024, 99, "AB"));

    database.MarkStale("hot.bin").Should().BeTrue("the first write is what makes the entry untrustworthy");

    // the shape that matters: a database file or VM image rewritten thousands of times
    for (var write = 0; write < 1000; ++write)
      database.MarkStale("hot.bin").Should().BeFalse(
        "an entry already marked must report no change, or a hot file dirties the checksum database "
        + "on every single write — the per-write cost that marking exists to avoid");
  }

  [Test]
  [Category("EdgeCase")]
  public void Marking_GivenNothingWasEverRecorded_ThenThereIsNothingToMark() {
    var database = new ChecksumDatabase(new InMemoryVolume());

    database.MarkStale("never-seen.bin").Should().BeFalse(
      "a file with no baseline is already untrusted; inventing an entry to mark would be recording "
      + "a hash that was never computed");
  }

  /// <summary>The smallest member that satisfies the checksum database: it only reads and writes one sidecar.</summary>
  private sealed class InMemoryVolume : IVolumeIO {
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public Guid MemberId { get; } = Guid.NewGuid();
    public string DisplayName => "in-memory";
    public string PhysicalVolumeId => "PHYS-MEM";
    public bool IsOnline => true;
    public long BytesFree => 1 << 30;
    public long BytesTotal => 1 << 30;
    public BackendCaps Caps => BackendCaps.AtomicRename | BackendCaps.DurableFlush;

    private string _Key(string path, bool shadow) => (shadow ? "s|" : "p|") + path;

    public Stream OpenRead(string relativePath, bool shadow)
      => this._files.TryGetValue(this._Key(relativePath, shadow), out var content)
        ? new MemoryStream(content, false)
        : throw new PoolFsException(PoolFsError.NotFound, relativePath);

    public Stream OpenWrite(string relativePath, bool shadow, bool create) {
      var key = this._Key(relativePath, shadow);
      var stream = new CapturingStream(content => this._files[key] = content);
      return stream;
    }

    public void Truncate(string relativePath, bool shadow, long length) { }
    public void Delete(string relativePath, bool shadow) => this._files.Remove(this._Key(relativePath, shadow));
    public void EnsureFolder(string relativeFolder, bool shadow) { }
    public void DeleteFolder(string relativeFolder, bool shadow) { }
    public void RenameFolder(string fromRelativeFolder, string toRelativeFolder) { }

    public void AtomicReplace(string tempRelative, string finalRelative, bool shadow) {
      var from = this._Key(tempRelative, shadow);
      if (!this._files.Remove(from, out var content))
        return;

      this._files[this._Key(finalRelative, shadow)] = content;
    }

    public FileMeta? Stat(string relativePath, bool shadow)
      => this._files.TryGetValue(this._Key(relativePath, shadow), out var content)
        ? new FileMeta(content.Length, DateTime.UnixEpoch, DateTime.UnixEpoch, FileAttributes.Normal)
        : null;

    public bool FileExists(string relativePath, bool shadow) => this._files.ContainsKey(this._Key(relativePath, shadow));
    public bool FolderExists(string relativeFolder, bool shadow) => true;
    public IEnumerable<VolumeEntry> List(string relativeFolder, bool shadow) => [];
    public void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc) { }

    private sealed class CapturingStream(Action<byte[]> onDispose) : MemoryStream {
      protected override void Dispose(bool disposing) {
        if (disposing)
          onDispose(this.ToArray());

        base.Dispose(disposing);
      }
    }
  }

}
