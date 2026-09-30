using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The space optimizer inside a mounted pool (docs/SpaceSavings.md): it must never make an
/// application wait. It holds no file while it hashes and compares, hears every change the pool
/// makes meanwhile, and at its commit leaves any file alone that changed or is open.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SpaceOptimizerEngineTests {

  private const BackendCaps _LINKING = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.AtomicRename | BackendCaps.DurableFlush
                                       | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps | BackendCaps.HardLinks;

  private static readonly byte[] _CONTENT = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i * 5 + 3))];
  private static readonly TimeSpan _NEVER_WAITS = TimeSpan.FromSeconds(10);

  private FakeVolumeIO _disk = null!;

  [SetUp]
  public void SetUp() {
    this._disk = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 26) { Caps = _LINKING };
    this._disk.Seed("a.bin", false, _CONTENT);
    this._disk.Seed("b.bin", false, _CONTENT); // same content, same times: a hard link may be made
  }

  private PoolFileSystem _Engine() {
    var cache = new CacheInstance("soe" + Guid.NewGuid().ToString("N"), new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(Guid.NewGuid(), [new(this._disk)], cache, ConfigResolver.ResolveEffective(null, """{ "duplication": 1 }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  /// <summary>Runs <paramref name="act"/> on another thread the 2nd time the pass opens b.bin (its byte compare), and waits for it.</summary>
  private void _DuringCompare(Action act) {
    var seen = 0;
    this._disk.BeforeOperation = (op, path) => {
      if (op != VolumeOp.OpenRead || path != "b.bin" || Interlocked.Increment(ref seen) != 2)
        return;

      // an application acting while the pass is mid-compare: with the file held, this would wait on
      // the very thread it blocks, and the pass would never finish
      Task.Run(act).Wait(_NEVER_WAITS).Should().BeTrue("the pass must not make an application wait");
    };
  }

  [Test]
  [Category("HappyPath")]
  public void Optimize_GivenTwoIdleIdenticalFiles_ThenTheyShareTheirData() {
    using var fs = this._Engine();

    var report = fs.OptimizeSpace();

    report.FilesDeduplicated.Should().Be(1);
    this._disk.LinkCount("a.bin", false).Should().Be(2);
  }

  [Test]
  [Category("EdgeCase")]
  public void Optimize_WhenAnApplicationWritesTheFileMidCompare_ThenTheWriteNeverWaitsAndIsKept() {
    using var fs = this._Engine();
    this._DuringCompare(() => {
      var handle = fs.Open("b.bin", AccessMode.ReadWrite, ShareMode.Read);
      fs.Write(handle, [0xEE], 7, WriteMode.Normal);
      fs.Close(handle);
    });

    var report = fs.OptimizeSpace();

    report.FilesDeduplicated.Should().Be(0, "the file changed while it was compared");
    report.SkippedChanged.Should().Be(1);
    this._disk.GetContent("b.bin", false)![7].Should().Be(0xEE);
    this._disk.GetContent("a.bin", false).Should().Equal(_CONTENT);
    this._disk.LinkCount("a.bin", false).Should().Be(1);
  }

  [Test]
  [Category("EdgeCase")]
  public void Optimize_WhenAnApplicationStampsNewTimesMidCompare_ThenNoLinkIsMade() {
    // new times are a change too: a link would give the other file these times
    using var fs = this._Engine();
    this._DuringCompare(() => fs.SetAttributes("b.bin", new(LastWriteTimeUtc: new DateTime(2012, 3, 4, 0, 0, 0, DateTimeKind.Utc))));

    fs.OptimizeSpace().FilesDeduplicated.Should().Be(0);
    this._disk.LinkCount("a.bin", false).Should().Be(1);
    this._disk.Stat("a.bin", false)!.Value.LastWriteTimeUtc.Year.Should().NotBe(2012);
  }

  [Test]
  [Category("EdgeCase")]
  public void Optimize_WhenAnApplicationDeletesTheFileMidCompare_ThenNothingIsBroughtBack() {
    using var fs = this._Engine();
    this._DuringCompare(() => fs.Unlink("b.bin"));

    fs.OptimizeSpace().FilesDeduplicated.Should().Be(0);
    this._disk.FileExists("b.bin", false).Should().BeFalse("a deleted file stays deleted");
    this._disk.GetContent("a.bin", false).Should().Equal(_CONTENT);
  }

  [Test]
  [Category("EdgeCase")]
  public void Optimize_GivenTheFileIsOpen_ThenItIsReadFreelyAndSkippedAtTheCommit() {
    using var fs = this._Engine();
    var handle = fs.Open("b.bin", AccessMode.Read, ShareMode.Read | ShareMode.Write);
    try {
      var report = fs.OptimizeSpace();

      report.SkippedBusy.Should().Be(1, "an open file is left for the next pass");
      this._disk.LinkCount("a.bin", false).Should().Be(1);
    } finally {
      fs.Close(handle);
    }

    fs.OptimizeSpace().FilesDeduplicated.Should().Be(1, "closed, it is shared on the next pass");
  }

}
