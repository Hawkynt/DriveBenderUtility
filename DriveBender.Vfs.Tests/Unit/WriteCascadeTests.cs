using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>The M3 tiered write cascade: write policies, RAM buffer, deferral, landing-zone drain (§6.7/§6.8).</summary>
[TestFixture]
[Category("Unit")]
public class WriteCascadeTests {

  private static readonly Guid _pool = Guid.Parse("abababab-0000-0000-0000-000000000007");

  private FakeVolumeIO _ssd = null!;
  private FakeVolumeIO _hdd1 = null!;
  private FakeVolumeIO _hdd2 = null!;
  private DateTime _now;

  [SetUp]
  public void SetUp() {
    this._ssd = new(Guid.NewGuid(), "ssd", "PHYS-SSD", capacity: 1L << 20);
    this._hdd1 = new(Guid.NewGuid(), "hdd1", "PHYS-HDD1", capacity: 1L << 20);
    this._hdd2 = new(Guid.NewGuid(), "hdd2", "PHYS-HDD2", capacity: 1L << 20);
    this._now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
  }

  private PoolFileSystem _CreateFs(string configJson, bool ssdIsLanding = false, string? cacheSize = null) {
    var cache = new CacheInstance("c" + Guid.NewGuid().ToString("N"), new() {
      Size = cacheSize ?? "262144",
      BlockSize = "16",
      MetadataEntries = 1000,
      MetadataTtl = "1m",
      Split = cacheSize == null ? null : new() { Mode = CacheSplitMode.Separate, ReadCacheMax = "1024", WriteBufferMax = "8" },
    });
    var members = new EngineMember[] {
      new(this._ssd, ssdIsLanding ? MemberRole.Landing : MemberRole.Capacity),
      new(this._hdd1),
      new(this._hdd2),
    };
    var fs = new PoolFileSystem(_pool, members, cache, ConfigResolver.ResolveEffective(null, configJson), clock: () => this._now);
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private FakeVolumeIO[] _All => [this._ssd, this._hdd1, this._hdd2];

  private static string _Staged(string path) => path + "." + DivisonM.DriveBender.DriveBenderConstants.TEMP_EXTENSION;

  /// <summary>Durable copies under the final OR the in-progress staged name (FR-STAGED-WRITE) — durability is what counts.</summary>
  private int _DurableCopyCount(string path, byte[] expected)
    => this._All.SelectMany(v => new[] { v.GetContent(path, false), v.GetContent(path, true), v.GetContent(_Staged(path), false), v.GetContent(_Staged(path), true) })
      .Count(content => content != null && content.SequenceEqual(expected));

  [Test]
  [Category("HappyPath")]
  public void WriteBack_GivenTripleDuplication_WhenWritten_ThenAckAfterTwoCopiesAndThirdOwedToBackground() {
    var fs = this._CreateFs("""{ "duplication": 3, "write": { "policy": "write-back", "minCopiesBeforeAck": 2 } }""");
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);

    fs.Write(handle, [1, 2, 3], 0, WriteMode.Normal); // ack ⇒ 2 durable copies exist

    this._DurableCopyCount("f.bin", [1, 2, 3]).Should().Be(2, "write-back acks at minCopiesBeforeAck (FR-WB)");
    fs.WriteBuffer.IsDirty("f.bin").Should().BeTrue("the third copy is owed");
    fs.Journal.ReadIncomplete().Should().NotBeEmpty("the intent stays open until every copy is applied");

    fs.CreateScheduler().Quiesce();

    this._DurableCopyCount("f.bin", [1, 2, 3]).Should().Be(3, "background sync converges to the duplication level (SAFE-DUP)");
    fs.WriteBuffer.IsDirty("f.bin").Should().BeFalse();

    fs.Close(handle); // publishes the staged temp — the Create intent completes with the rename
    fs.Journal.ReadIncomplete().Should().BeEmpty();
  }

  [Test]
  [Category("HappyPath")]
  public void WriteBack_GivenCrashBeforeClose_WhenRecovered_ThenHalfWrittenFileNeverAppears() {
    var fs = this._CreateFs("""{ "duplication": 3, "write": { "policy": "write-back", "minCopiesBeforeAck": 2 } }""");
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [9, 9], 0, WriteMode.Normal);

    // power loss before the file was ever closed or fsynced: it was never fully written
    foreach (var volume in this._All)
      volume.SimulateCrash();

    var fs2 = this._CreateFs("""{ "duplication": 3, "write": { "policy": "write-back", "minCopiesBeforeAck": 2 } }""");

    this._DurableCopyCount("f.bin", [9, 9]).Should().Be(0, "a file that never finished writing must not appear after a crash (FR-STAGED-WRITE)");
    var probe = () => fs2.GetAttributes("f.bin");
    probe.Should().Throw<PoolFsException>("no half-written file surfaces in the namespace");
    fs2.Journal.ReadIncomplete().Should().BeEmpty("recovery swept the orphaned temp and its intent");
  }

  [Test]
  [Category("HappyPath")]
  public void Deferred_GivenCoalescingWindow_WhenClockAdvances_ThenOwedCopiesApplyOnlyAfterWindow() {
    var fs = this._CreateFs("""{ "duplication": 3, "write": { "policy": "deferred", "minCopiesBeforeAck": 2, "deferWindow": "5s", "maxDeferSeconds": 30 } }""");
    var scheduler = fs.CreateScheduler();
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1], 0, WriteMode.Normal);

    scheduler.Pump().Should().Be(0, "inside the defer window nothing is applied (FR-DEF)");
    this._DurableCopyCount("f.bin", [1]).Should().Be(2);

    this._now += TimeSpan.FromSeconds(6);
    scheduler.Quiesce();

    this._DurableCopyCount("f.bin", [1]).Should().Be(3, "the coalesced batch applies after the window");
    fs.Close(handle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Deferred_GivenContinuousWrites_WhenMaxDeferExceeded_ThenHardBoundForcesApplication() {
    var fs = this._CreateFs("""{ "duplication": 3, "write": { "policy": "deferred", "minCopiesBeforeAck": 2, "deferWindow": "5s", "maxDeferSeconds": 30 } }""");
    var scheduler = fs.CreateScheduler();
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);

    // keep writing every 2 s — the window never expires, but the hard bound must
    for (var i = 0; i < 16; ++i) {
      fs.Write(handle, [(byte)i], i, WriteMode.Normal);
      this._now += TimeSpan.FromSeconds(2);
    }

    scheduler.Quiesce();
    fs.WriteBuffer.IsDirty("f.bin").Should().BeFalse("maxDeferSeconds bounds the risk window (FR-DEF)");
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Performance_GivenVolatileAckOptIn_WhenWritten_ThenAckFromRamAndReadsAreCoherent() {
    var fs = this._CreateFs("""{ "duplication": 1, "write": { "policy": "performance", "acceptVolatileAck": true, "minCopiesBeforeAck": 1 } }""");
    var handle = fs.Create("scratch.bin", NodeKind.File, CreateFlags.None);

    fs.Write(handle, [7, 7, 7], 0, WriteMode.Normal);

    this._DurableCopyCount("scratch.bin", [7, 7, 7]).Should().Be(0, "the ack came from RAM alone — the explicit volatility trade (SAFE-RAM)");
    fs.WriteBuffer.IsVolatileOnly("scratch.bin").Should().BeTrue();
    fs.GetAttributes("scratch.bin").Length.Should().Be(3, "metadata reflects the buffered image");

    var buffer = new byte[3];
    fs.Read(handle, buffer, 0).Should().Be(3);
    buffer.Should().Equal(new byte[] { 7, 7, 7 }, "the write buffer is authoritative until flushed (SAFE-COHERE)");
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Performance_GivenFsync_WhenFlushed_ThenDataForcedOutOfRamAndSurvivesCrash() {
    var fs = this._CreateFs("""{ "duplication": 1, "write": { "policy": "performance", "acceptVolatileAck": true, "minCopiesBeforeAck": 1 } }""");
    var handle = fs.Create("scratch.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [5, 5], 0, WriteMode.Normal);

    fs.Flush(handle); // fsync overrides volatile ack (SAFE-FSYNC)

    foreach (var volume in this._All)
      volume.SimulateCrash();

    this._DurableCopyCount("scratch.bin", [5, 5]).Should().Be(1, "a successful fsync is an absolute durability promise regardless of policy");
    fs.Close(handle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Performance_GivenWriteBufferAtHardCap_WhenWritten_ThenDegradesToDurableWriteInsteadOfGrowing() {
    // writeBufferMax is 8 bytes; a 16-byte volatile write cannot stage
    var fs = this._CreateFs("""{ "duplication": 1, "write": { "policy": "performance", "acceptVolatileAck": true, "minCopiesBeforeAck": 1 } }""", cacheSize: "tiny");
    var handle = fs.Create("big.bin", NodeKind.File, CreateFlags.None);

    fs.Write(handle, new byte[16], 0, WriteMode.Normal);

    this._DurableCopyCount("big.bin", new byte[16]).Should().Be(1, "backpressure degrades to a durable write, never unbounded RAM (FR-BACKP)");
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Drain_GivenFileLandedOnFastTier_WhenDrainerRuns_ThenFileMovesToCapacityWithDuplication() {
    var fs = this._CreateFs("""{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 2 } }""", ssdIsLanding: true);
    var handle = fs.Create("ingest.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [4, 4, 4], 0, WriteMode.Normal);
    fs.Close(handle);

    this._ssd.FileExists("ingest.bin", false).Should().BeTrue("ingest lands on the fast tier first (FR-TIER)");

    fs.CreateScheduler().Quiesce();

    this._ssd.FileExists("ingest.bin", false).Should().BeFalse("the drainer frees the fast tier (FR-LZ-DRAIN)");
    var capacityPrimaries = new[] { this._hdd1, this._hdd2 }.Count(v => v.FileExists("ingest.bin", false));
    capacityPrimaries.Should().Be(1);
    this._DurableCopyCount("ingest.bin", [4, 4, 4]).Should().BeGreaterThanOrEqualTo(2, "duplication is re-established after the drain (SAFE-DUP)");

    var readHandle = fs.Open("ingest.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[3];
    fs.Read(readHandle, buffer, 0).Should().Be(3);
    buffer.Should().Equal(new byte[] { 4, 4, 4 }, "the file survives the tier hop bit-exact (UC2)");
    fs.Close(readHandle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Drain_GivenOpenOrDirtyFile_WhenDrainerRuns_ThenFileStaysPut() {
    var fs = this._CreateFs("""{ "duplication": 1 }""", ssdIsLanding: true);
    var handle = fs.Create("open.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1], 0, WriteMode.Normal);

    fs.DrainOneLandingFile().Should().BeFalse("open files never move under the writer");
    (this._ssd.FileExists("open.bin", false) || this._ssd.FileExists(_Staged("open.bin"), false)).Should().BeTrue();
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Unmount_GivenOwedCopies_WhenUnmounted_ThenEverythingFlushedFirst() {
    var fs = this._CreateFs("""{ "duplication": 3, "write": { "policy": "write-back", "minCopiesBeforeAck": 2 } }""");
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [8], 0, WriteMode.Normal);
    fs.Close(handle);

    fs.Unmount();

    this._DurableCopyCount("f.bin", [8]).Should().Be(3, "clean unmount flushes all dirty state (FR-CLEAN-UNMOUNT)");
    fs.Journal.ReadIncomplete().Should().BeEmpty();
  }

  [Test]
  [Category("HappyPath")]
  public void StateMachine_GivenWritePolicyStages_WhenObserved_ThenStatesProgress() {
    var fs = this._CreateFs("""{ "duplication": 3, "write": { "policy": "write-back", "minCopiesBeforeAck": 2 } }""");
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);

    fs.WriteBuffer.StateOf("f.bin").Should().Be(DirtyState.Clean);
    fs.Write(handle, [1], 0, WriteMode.Normal);
    fs.WriteBuffer.StateOf("f.bin").Should().Be(DirtyState.Landed, "durable copies exist, remainder owed (§6.8)");

    fs.FlushPath("f.bin");
    fs.WriteBuffer.StateOf("f.bin").Should().Be(DirtyState.Clean);
    fs.Close(handle);
  }

}
