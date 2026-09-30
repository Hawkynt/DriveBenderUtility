using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Copy-on-write for hard-linked files (docs/SpaceSavings.md). Two different files with identical
/// content may share their data on a disk. Whatever changes one of them through the pool — a write,
/// a truncate, new times — must leave the other exactly as it was, even across a power cut.
/// </summary>
[TestFixture]
[Category("Unit")]
public class LinkCopyOnWriteTests {

  private static readonly Guid _pool = Guid.Parse("c0c0c0c0-0000-0000-0000-000000000001");
  private const BackendCaps _LINKING = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.AtomicRename | BackendCaps.DurableFlush
                                       | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps | BackendCaps.HardLinks;

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private static readonly byte[] _TWIN = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 3))];

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24) { Caps = _LINKING };
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24) { Caps = _LINKING };
  }

  private PoolFileSystem _Engine(string config = """{ "duplication": 1 }""") {
    var cache = new CacheInstance("cow" + Guid.NewGuid().ToString("N"), new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache, ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  /// <summary>a.bin and b.bin: two files, one set of data on <paramref name="volume"/> (primary, or shadow).</summary>
  private static void _Twins(FakeVolumeIO volume, bool shadow = false) {
    volume.Seed("a.bin", shadow, _TWIN);
    volume.TryHardLink("a.bin", shadow, "b.bin", shadow).Should().BeTrue();
    volume.LinkCount("a.bin", shadow).Should().Be(2, "the premise: the two files share their data");
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenTheFileSharesItsDataWithAnother_ThenOnlyTheWrittenFileChanges() {
    _Twins(this._v1);
    using var fs = this._Engine();

    var handle = fs.Open("b.bin", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(handle, [0xEE, 0xEE], 10, WriteMode.Normal);
    fs.Close(handle);

    this._v1.GetContent("a.bin", false).Should().Equal(_TWIN, "the other file is untouched");
    this._v1.GetContent("b.bin", false)![10].Should().Be(0xEE);
    this._v1.LinkCount("a.bin", false).Should().Be(1, "the written file has data of its own now");
  }

  [Test]
  [Category("HappyPath")]
  public void SetLength_GivenTheFileSharesItsData_ThenTheOtherKeepsItsLength() {
    _Twins(this._v1);
    using var fs = this._Engine();

    var handle = fs.Open("b.bin", AccessMode.ReadWrite, ShareMode.Read);
    fs.SetLength(handle, 100);
    fs.Close(handle);

    this._v1.GetContent("a.bin", false).Should().Equal(_TWIN);
    this._v1.GetContent("b.bin", false).Should().HaveCount(100);
  }

  [Test]
  [Category("EdgeCase")]
  public void SetAttributes_GivenTheFileSharesItsData_ThenTheOtherKeepsItsTimes() {
    // a hard link shares metadata as well as content: stamping one name stamped both
    _Twins(this._v1);
    var original = this._v1.Stat("a.bin", false)!.Value.LastWriteTimeUtc;
    using var fs = this._Engine();

    fs.SetAttributes("b.bin", new(LastWriteTimeUtc: new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

    this._v1.Stat("a.bin", false)!.Value.LastWriteTimeUtc.Should().Be(original, "the other file keeps its own time");
    this._v1.Stat("b.bin", false)!.Value.LastWriteTimeUtc.Year.Should().Be(2001);
  }

  [Test]
  [Category("EdgeCase")]
  public void OwedCopy_GivenBothDisksHoldTheTwinsLinked_ThenTheAppliedCopyDoesNotReachTheOtherFile() {
    // duplication 2 acknowledged on one copy: the second copy is written later, from the write
    // buffer — the path that writes in place without going through the ordinary write
    _Twins(this._v1);
    _Twins(this._v2, shadow: true);
    using var fs = this._Engine("""{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 1 } }""");

    var handle = fs.Open("b.bin", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(handle, [0xAB], 0, WriteMode.Normal);
    fs.Close(handle);
    fs.CreateScheduler().Quiesce();

    this._v1.GetContent("a.bin", false).Should().Equal(_TWIN);
    this._v2.GetContent("a.bin", true).Should().Equal(_TWIN, "the owed copy went to b's own data, not to a's");
    this._v1.GetContent("b.bin", false)![0].Should().Be(0xAB);
    this._v2.GetContent("b.bin", true)![0].Should().Be(0xAB);
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenTheFileSharesItsData_WhenThePowerIsCutAtEveryStep_ThenTheOtherFileIsNeverChanged() {
    for (var step = 1; ; ++step) {
      this.SetUp();
      _Twins(this._v1); // seeded content is already durable

      var fired = 0;
      var remaining = step;
      void Cut(VolumeOp op, string path) {
        if (Volatile.Read(ref fired) == 1 || Interlocked.Decrement(ref remaining) == 0) {
          Interlocked.Exchange(ref fired, 1);
          throw new InvalidOperationException("power loss");
        }
      }

      var fs = this._Engine();
      this._v1.BeforeOperation = Cut;
      this._v2.BeforeOperation = Cut;
      try {
        var handle = fs.Open("b.bin", AccessMode.ReadWrite, ShareMode.Read);
        fs.Write(handle, [0xEE, 0xEE], 10, WriteMode.Normal);
        fs.Close(handle);
      } catch (Exception) {
        // the machine went down part-way
      }

      try {
        fs.Dispose();
      } catch (Exception) {
      }

      this._v1.BeforeOperation = null;
      this._v2.BeforeOperation = null;
      if (fired == 0) {
        step.Should().BeGreaterThan(3, "the write and its separation are several steps, each one cut");
        break;
      }

      this._v1.SimulateCrash();
      this._v2.SimulateCrash();
      using var recovered = this._Engine();
      this._v1.GetContent("a.bin", false).Should().Equal(_TWIN, $"power cut at step {step}: the other file must never change");
      var b = this._v1.GetContent("b.bin", false);
      b.Should().NotBeNull($"power cut at step {step}: the written file is still there");
      b!.Length.Should().Be(_TWIN.Length);
    }
  }

}
