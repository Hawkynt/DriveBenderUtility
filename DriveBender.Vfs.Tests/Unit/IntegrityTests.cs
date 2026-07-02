using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Checksum DB, bit-rot repair and out-of-band reconciliation (§6.15, SAFE-OOB).</summary>
[TestFixture]
[Category("Unit")]
public class IntegrityTests {

  private static readonly Guid _pool = Guid.Parse("efefefef-0000-0000-0000-000000000009");

  private FakeVolumeIO _volume1 = null!;
  private FakeVolumeIO _volume2 = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    var cache = new CacheInstance("i" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    this._fs = new(_pool, [new(this._volume1), new(this._volume2)], cache, ConfigResolver.ResolveEffective(null, """{ "duplication": 2 }"""));
    this._fs.Mount(new(@"X:\"));
  }

  private void _CreateWithContent(string path, byte[] content) {
    var handle = this._fs.Create(path, NodeKind.File, CreateFlags.None);
    this._fs.Write(handle, content, 0, WriteMode.Normal);
    this._fs.Close(handle);
    this._fs.RunScrub(); // baseline the checksum DB
  }

  private (FakeVolumeIO holder, bool shadow) _PrimaryHolder(string path) {
    var holder = new[] { this._volume1, this._volume2 }.Single(v => v.FileExists(path, false));
    return (holder, false);
  }

  [Test]
  [Category("HappyPath")]
  public void Scrub_GivenCleanPool_WhenScrubbed_ThenNoIssues() {
    this._CreateWithContent("clean.bin", [1, 2, 3]);
    this._fs.RunScrub().Should().BeEmpty("driver writes must never read as corruption or edits");
  }

  [Test]
  [Category("HappyPath")]
  public void Scrub_GivenSilentBitFlip_WhenScrubbed_ThenRepairedFromGoodCopyAndQuarantined() {
    this._CreateWithContent("f.bin", [1, 2, 3]);
    var (holder, shadow) = this._PrimaryHolder("f.bin");

    // content changes, (size, mtime) do not — the definition of silent corruption (SAFE-OOB case 1)
    holder.CorruptSilently("f.bin", shadow, content => content[1] = 99);

    var issues = this._fs.RunScrub();

    issues.Should().ContainSingle().Which.Kind.Should().Be(IntegrityIssueKind.BitRotRepaired);
    holder.GetContent("f.bin", shadow).Should().Equal(new byte[] { 1, 2, 3 }, "the corrupt copy is repaired from a checksum-verified copy");
    holder.FilePaths.Should().Contain(path => path.Contains("conflicts/f.bin.bitrot"), "the corrupt content is quarantined, never destroyed");

    var handle = this._fs.Open("f.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[3];
    this._fs.Read(handle, buffer, 0);
    buffer.Should().Equal(new byte[] { 1, 2, 3 });
    this._fs.Close(handle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Scrub_GivenAllCopiesRotten_WhenScrubbed_ThenUnrecoverableAndNothingOverwritten() {
    this._CreateWithContent("f.bin", [1, 2, 3]);
    this._volume1.CorruptSilently("f.bin", this._volume1.FileExists("f.bin", false) == false, c => c[0] = 77);
    this._volume2.CorruptSilently("f.bin", this._volume2.FileExists("f.bin", false) == false, c => c[0] = 88);

    var issues = this._fs.RunScrub();

    issues.Should().ContainSingle().Which.Kind.Should().Be(IntegrityIssueKind.BitRotUnrecoverable);
    this._volume1.FilePaths.Concat(this._volume2.FilePaths).Should().NotContain(p => p.Contains("conflicts/"),
      "when no good copy exists the last data is never overwritten or moved (SAFE-OOB)");
  }

  [Test]
  [Category("HappyPath")]
  public void Scrub_GivenExternalEdit_WhenScrubbed_ThenAcceptedAndRePropagated() {
    this._CreateWithContent("doc.txt", [1, 1, 1]);
    var (holder, shadow) = this._PrimaryHolder("doc.txt");

    // edited on another machine: content AND mtime advanced (SAFE-OOB case 2)
    holder.CorruptSilently("doc.txt", shadow, content => content[0] = 42);
    holder.SetTimestamps("doc.txt", shadow, null, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    var issues = this._fs.RunScrub();

    issues.Should().ContainSingle().Which.Kind.Should().Be(IntegrityIssueKind.ExternalEditAccepted);
    var other = new[] { this._volume1, this._volume2 }.Single(v => v != holder);
    other.GetContent("doc.txt", other.FileExists("doc.txt", true)).Should().Equal(new byte[] { 42, 1, 1 },
      "the external edit is authoritative and re-propagated (never mistaken for corruption)");

    this._fs.RunScrub().Should().BeEmpty("after acceptance the DB is refreshed — a second scrub is clean");
  }

  [Test]
  [Category("EdgeCase")]
  public void Scrub_GivenDivergentEditsOnBothCopies_WhenScrubbed_ThenConflictKeepsAllVersions() {
    this._CreateWithContent("both.txt", [5, 5]);
    var holder1 = new[] { this._volume1, this._volume2 }.Single(v => v.FileExists("both.txt", false));
    var holder2 = new[] { this._volume1, this._volume2 }.Single(v => v.FileExists("both.txt", true));

    holder1.CorruptSilently("both.txt", false, c => c[0] = 11);
    holder1.SetTimestamps("both.txt", false, null, new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc));
    holder2.CorruptSilently("both.txt", true, c => c[0] = 22);
    holder2.SetTimestamps("both.txt", true, null, new DateTime(2027, 1, 3, 0, 0, 0, DateTimeKind.Utc));

    var issues = this._fs.RunScrub();

    issues.Should().ContainSingle().Which.Kind.Should().Be(IntegrityIssueKind.Conflict);
    holder1.FilePaths.Concat(holder2.FilePaths).Should().Contain(p => p.Contains("conflicts/both.txt.conflict"),
      "divergent versions are preserved for user resolution, never auto-destroyed (SAFE-OOB case 3)");
  }

  [Test]
  [Category("EdgeCase")]
  public void Scrub_GivenAmbiguousEqualMtimeDivergence_WhenScrubbed_ThenConservativelyAConflict() {
    this._CreateWithContent("amb.txt", [7]);
    var sameTime = new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    var holder1 = new[] { this._volume1, this._volume2 }.Single(v => v.FileExists("amb.txt", false));
    var holder2 = new[] { this._volume1, this._volume2 }.Single(v => v.FileExists("amb.txt", true));
    holder1.CorruptSilently("amb.txt", false, c => c[0] = 1);
    holder1.SetTimestamps("amb.txt", false, null, sameTime);
    holder2.CorruptSilently("amb.txt", true, c => c[0] = 2);
    holder2.SetTimestamps("amb.txt", true, null, sameTime);

    var issues = this._fs.RunScrub();

    issues.Should().ContainSingle().Which.Kind.Should().Be(IntegrityIssueKind.Conflict,
      "mtime can be spoofed or skewed — when unsure the scrubber keeps everything (SAFE-OOB)");
  }

  [Test]
  [Category("HappyPath")]
  public void QuickScan_GivenUnchangedFiles_WhenScanned_ThenNothingHashedOrReported() {
    this._CreateWithContent("static.bin", [3, 3]);

    this._fs.Integrity.QuickScan().Should().BeEmpty("(size, mtime) match the DB — the quick pass skips hashing (FR-OOB-MOUNT)");
  }

  [Test]
  [Category("HappyPath")]
  public void MountTimeScan_GivenMemberEditedWhileUnmounted_WhenRemounted_ThenReconciledBeforeServing() {
    this._CreateWithContent("roam.txt", [9, 9]);
    this._fs.Integrity.SaveAll();
    this._fs.Unmount();

    // the member was used elsewhere while unmounted
    var (holder, shadow) = this._PrimaryHolder("roam.txt");
    holder.CorruptSilently("roam.txt", shadow, c => c[0] = 1);
    holder.SetTimestamps("roam.txt", shadow, null, new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc));

    var cache = new CacheInstance("i2" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    var remounted = new PoolFileSystem(_pool, [new(this._volume1), new(this._volume2)], cache, ConfigResolver.ResolveEffective(null, """{ "duplication": 2 }"""));
    remounted.Mount(new(@"X:\"));

    var other = new[] { this._volume1, this._volume2 }.Single(v => v != holder);
    other.GetContent("roam.txt", other.FileExists("roam.txt", true)).Should().Equal(new byte[] { 1, 9 },
      "the mount-time delta scan reconciles external edits before serving stale data (FR-OOB-MOUNT)");
  }

  [Test]
  [Category("HappyPath")]
  public void ChecksumDb_GivenPersistedDatabase_WhenReloaded_ThenSurvivesRestart() {
    this._CreateWithContent("persist.bin", [4]);
    this._fs.Integrity.SaveAll();

    this._volume1.FilePaths.Concat(this._volume2.FilePaths)
      .Should().Contain(p => p.Equals(ChecksumDatabase.DbPath, StringComparison.OrdinalIgnoreCase),
        "the checksum DB is a self-contained sidecar (SAFE-COMPAT)");

    var reloaded = new IntegrityService([this._volume1, this._volume2]);
    reloaded.ScrubAll().Should().BeEmpty("a fresh service sees the persisted baselines");
  }

}
