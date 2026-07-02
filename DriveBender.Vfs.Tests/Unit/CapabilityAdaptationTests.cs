using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Backend capability adaptation (FR-CAP-ADAPT, SAFE-REMOTE): the engine honours each
/// backend's declared capability set — a weak-flush backend never satisfies the ack
/// quorum, a rename-less backend gets journalled put-and-verify emulation.
/// </summary>
[TestFixture]
[Category("Unit")]
public class CapabilityAdaptationTests {

  private static readonly Guid _pool = Guid.Parse("badcafe0-0000-0000-0000-00000000000a");

  private const BackendCaps _REMOTE_WEAK = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps; // FTP-ish: no AtomicRename, no DurableFlush

  private FakeVolumeIO _local = null!;
  private FakeVolumeIO _remote = null!;

  [SetUp]
  public void SetUp() {
    this._local = new(Guid.NewGuid(), "local", "PHYS-LOCAL", capacity: 1L << 20);
    this._remote = new(Guid.NewGuid(), "remote", "UNC-REMOTE", capacity: 1L << 20) { Caps = _REMOTE_WEAK };
  }

  private PoolFileSystem _CreateFs(string configJson) {
    var cache = new CacheInstance("cap" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._local), new(this._remote)], cache, ConfigResolver.ResolveEffective(null, configJson));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  [Test]
  [Category("Exception")]
  public void Write_GivenQuorumNeedsTwoButOnlyLocalHasDurableFlush_WhenWritten_ThenAckRefused() {
    var fs = this._CreateFs("""{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 2 } }""");
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);

    var act = () => fs.Write(handle, [1], 0, WriteMode.Normal);

    act.Should().Throw<PoolFsException>(
      "a backend lacking DurableFlush is excluded from satisfying minCopiesBeforeAck (SAFE-REMOTE)");
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenMinCopiesOne_WhenWritten_ThenDurableLocalTakesTheAckAndRemoteIsOwed() {
    var fs = this._CreateFs("""{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 1, "acceptVolatileAck": false }, "folders": {} }""");
    // CFG-SAFE-FLOOR would reject minCopies 1 for D=2 in validation; the engine still
    // honours the effective clamp — bypass validation here to exercise the quorum routing
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [5, 5], 0, WriteMode.Normal);

    // the durable ack copy must be the local one, never the weak remote
    this._local.SimulateCrash();
    this._remote.SimulateCrash();

    var localHolds = (this._local.GetContent("f.bin", false) ?? this._local.GetContent("f.bin", true) ?? []).SequenceEqual(new byte[] { 5, 5 });
    localHolds.Should().BeTrue("the ack'd copy lives on the durable-flush member (SAFE-REMOTE)");
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Publish_GivenBackendWithoutAtomicRename_WhenPublished_ThenPutAndVerifyEmulationUsed() {
    var act = () => WholeFilePublisher.Publish(this._remote, "up.bin", false, [9, 9, 9]);
    act.Should().NotThrow("missing AtomicRename gets a put-and-verify emulation (FR-CAP-ADAPT)");
    this._remote.GetContent("up.bin", false).Should().Equal(new byte[] { 9, 9, 9 });
  }

  [Test]
  [Category("Exception")]
  public void AtomicReplace_GivenRenamelessBackend_WhenCalledDirectly_ThenNotSupported() {
    this._remote.Seed("t.tmp", false, [1]);
    var act = () => this._remote.AtomicReplace("t.tmp", "t.bin", false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotSupported);
  }

  [Test]
  [Category("EdgeCase")]
  public void WeakFlush_GivenCrashAfterFlush_WhenDataOnlyOnRemote_ThenLostExactlyAsTheModelPredicts() {
    using (var stream = this._remote.OpenWrite("weak.bin", false, true)) {
      stream.Write([1], 0, 1);
      stream.Flush(); // acknowledged but not durable — FTP-style
    }

    this._remote.SimulateCrash();

    this._remote.FileExists("weak.bin", false).Should().BeFalse(
      "this is precisely why a weak-flush member can never be the sole copy of acknowledged data");
  }

  [Test]
  [Category("HappyPath")]
  public void Drain_GivenRenamelessCapacityTarget_WhenDrained_ThenWholeFilePutAndVerifyWorks() {
    // landing SSD + rename-less remote capacity: the drain must fall back to put-and-verify
    var ssd = new FakeVolumeIO(Guid.NewGuid(), "ssd", "PHYS-SSD", capacity: 1L << 20);
    var cache = new CacheInstance("cap2" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    var config = ConfigResolver.ResolveEffective(null, """{ "duplication": 1 }""");
    var fs = new PoolFileSystem(_pool, [new(ssd, MemberRole.Landing), new(this._remote)], cache, config);
    fs.Mount(new(@"Y:\"));

    var handle = fs.Create("archive.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [3, 3], 0, WriteMode.Normal);
    fs.Close(handle);

    fs.DrainOneLandingFile().Should().BeTrue();
    ssd.FileExists("archive.bin", false).Should().BeFalse();
    this._remote.GetContent("archive.bin", false).Should().Equal(new byte[] { 3, 3 }, "remote capacity tiers drain whole-file (§6.1)");
  }

}
