using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// FR-STAGED-WRITE and FR-STRIPE: a file being written lives under a temp physical name and
/// only the atomic temp→final rename — the last action before its Create intent completes —
/// makes it appear fully written; striped acks rotate blocks across storages while the
/// owed-sync job converges every copy in the background.
/// </summary>
[TestFixture]
[Category("Unit")]
public class StagedWriteTests {

  private static readonly Guid _pool = Guid.Parse("57a6ed00-0000-0000-0000-000000000011");

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
  }

  private PoolFileSystem _CreateFs(string configJson) {
    var cache = new CacheInstance("s" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache, ConfigResolver.ResolveEffective(null, configJson));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static string _Staged(string path) => path + "." + DivisonM.DriveBender.DriveBenderConstants.TEMP_EXTENSION;

  [Test]
  [Category("HappyPath")]
  public void Write_GivenOpenFile_WhenInspectedOnDisk_ThenOnlyTempNamesExistUntilClose() {
    var fs = this._CreateFs("""{ "duplication": 2 }""");
    var handle = fs.Create("movie.mkv", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1, 2, 3], 0, WriteMode.Normal);

    foreach (var volume in new[] { this._v1, this._v2 }) {
      volume.FileExists("movie.mkv", false).Should().BeFalse("the final name must not appear before the file is done");
      volume.FileExists("movie.mkv", true).Should().BeFalse();
    }

    new[] { this._v1, this._v2 }.Count(v => v.FileExists(_Staged("movie.mkv"), false) || v.FileExists(_Staged("movie.mkv"), true))
      .Should().Be(2, "the in-progress file lives under its temp physical name on every copy");
    fs.Journal.ReadIncomplete().Should().NotBeEmpty("the Create intent stays open until the publish rename");

    // the LOGICAL view is complete the whole time
    fs.GetAttributes("movie.mkv").Length.Should().Be(3);
    fs.ReadDirectory("").Should().Contain(e => e.Name == "movie.mkv", "listings show the logical file while it is written");

    fs.Close(handle); // temp → final is the last action, then the intent completes

    new[] { this._v1, this._v2 }.Count(v => v.FileExists("movie.mkv", false)) .Should().Be(1);
    new[] { this._v1, this._v2 }.Count(v => v.FileExists("movie.mkv", true)).Should().Be(1);
    new[] { this._v1, this._v2 }.Any(v => v.FileExists(_Staged("movie.mkv"), false) || v.FileExists(_Staged("movie.mkv"), true))
      .Should().BeFalse("no temp lingers after publish");
    fs.Journal.ReadIncomplete().Should().BeEmpty("the rename released the journal entry");
  }

  [Test]
  [Category("HappyPath")]
  public void Flush_GivenStagedFile_WhenFsynced_ThenPublishedImmediately() {
    var fs = this._CreateFs("""{ "duplication": 2 }""");
    var handle = fs.Create("db.sqlite", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [7], 0, WriteMode.Normal);

    fs.Flush(handle); // fsync = durability promise — the file must survive a crash from here on

    new[] { this._v1, this._v2 }.Count(v => v.FileExists("db.sqlite", false) || v.FileExists("db.sqlite", true))
      .Should().Be(2, "an fsynced file publishes to its final name (SAFE-FSYNC over staging)");
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenStripedAck_WhenSequentialBlocksWritten_ThenStoragesAlternateAndConverge() {
    // minCopiesBeforeAck 1 with duplication 2: each block acks after ONE storage took it,
    // consecutive blocks rotate the target, the owed-sync job converges the rest
    var fs = this._CreateFs("""{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 1 } }""");
    var handle = fs.Create("stripe.bin", NodeKind.File, CreateFlags.None);

    fs.Write(handle, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], 0, WriteMode.Normal);  // block 0
    fs.Write(handle, [2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2], 16, WriteMode.Normal); // block 1

    var staged = _Staged("stripe.bin");
    byte[]? Durable(FakeVolumeIO v, bool shadow, int offset) {
      var content = v.GetContent(staged, shadow);
      return content != null && content.Length >= offset + 16 ? content[offset..(offset + 16)] : null;
    }

    // each block is durable on at least one storage right now (that's what the ack promised)…
    var block0Holders = new[] { (this._v1, false), (this._v2, true) }.Count(t => Durable(t.Item1, t.Item2, 0)?.All(b => b == 1) == true);
    var block1Holders = new[] { (this._v1, false), (this._v2, true) }.Count(t => Durable(t.Item1, t.Item2, 16)?.All(b => b == 2) == true);
    block0Holders.Should().BeGreaterThanOrEqualTo(1);
    block1Holders.Should().BeGreaterThanOrEqualTo(1);
    (block0Holders + block1Holders).Should().BeLessThan(4, "with ack quorum 1 the blocks are striped, not fully mirrored yet");

    // …and the background sync converges both storages to the full file
    fs.CreateScheduler().Quiesce();
    fs.Close(handle);

    var expected = new byte[32];
    Array.Fill(expected, (byte)1, 0, 16);
    Array.Fill(expected, (byte)2, 16, 16);
    this._v1.GetContent("stripe.bin", false).Should().Equal(expected, "every storage ends with the complete file");
    this._v2.GetContent("stripe.bin", true).Should().Equal(expected);
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenFastAndSlowStorage_WhenStriping_ThenReadyFastStorageTakesTheSyncBlocks() {
    // FR-STRIPE-READY: the selector routes to the storage that is ready — the measured-fast one —
    // instead of blindly alternating onto a slow/busy disk
    var fast = new MeasuredVolumeIO(this._v1);
    var slow = new MeasuredVolumeIO(this._v2);
    fast.RecordLatency(0.5);
    slow.RecordLatency(80);

    var cache = new CacheInstance("r" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(fast), new(slow)], cache,
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 1 } }"""));
    fs.Mount(new(@"X:\"));

    var handle = fs.Create("vid.bin", NodeKind.File, CreateFlags.None);
    for (var block = 0; block < 4; ++block)
      fs.Write(handle, Enumerable.Repeat((byte)(block + 1), 16).ToArray(), block * 16, WriteMode.Normal);

    // every sync block landed on the measured-fast member; the slow one only owes background copies
    var staged = _Staged("vid.bin");
    var fastBytes = (this._v1.GetContent(staged, false) ?? this._v1.GetContent(staged, true))!;
    var slowBytes = this._v2.GetContent(staged, false) ?? this._v2.GetContent(staged, true);
    fastBytes.Length.Should().Be(64, "the ready (fast) storage took every synchronous block");
    (slowBytes == null || slowBytes.All(b => b == 0)).Should().BeTrue("the slow storage holds no synced data yet — it converges in the background");

    fs.CreateScheduler().Quiesce();
    fs.Close(handle);
    var final1 = this._v1.GetContent("vid.bin", false) ?? this._v1.GetContent("vid.bin", true);
    var final2 = this._v2.GetContent("vid.bin", false) ?? this._v2.GetContent("vid.bin", true);
    final1.Should().Equal(final2, "both storages converge to the identical full file");
    final1!.Length.Should().Be(64);
  }

  [Test]
  [Category("EdgeCase")]
  public void Unlink_GivenStagedFile_WhenDeletedBeforeClose_ThenTempsVanishAndIntentCompletes() {
    var fs = this._CreateFs("""{ "duplication": 2 }""");
    var handle = fs.Create("abort.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1], 0, WriteMode.Normal);

    fs.Unlink("abort.bin");

    new[] { this._v1, this._v2 }.Any(v => v.FileExists(_Staged("abort.bin"), false) || v.FileExists(_Staged("abort.bin"), true))
      .Should().BeFalse("aborting an in-progress file leaves nothing behind");
    fs.Journal.ReadIncomplete().Should().BeEmpty();
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Rename_GivenStagedFile_WhenRenamedBeforeClose_ThenPublishedFirstAndRenamed() {
    var fs = this._CreateFs("""{ "duplication": 2 }""");
    var handle = fs.Create("download.partial", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [4, 2], 0, WriteMode.Normal);

    fs.Rename("download.partial", "download.done", RenameFlags.None);

    this._v1.FileExists(_Staged("download.partial"), false).Should().BeFalse("no temp survives the rename");
    fs.GetAttributes("download.done").Length.Should().Be(2);
    fs.Close(handle);
  }

}
