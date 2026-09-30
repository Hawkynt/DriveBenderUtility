using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The landing zone takes a new file only while the file fits (docs/IncomingFiles.md): a file whose
/// announced size does not fit goes straight to the storage group, and a file that outgrows the
/// landing zone while it is written moves to the storage group and continues there. Either way the
/// file ends up whole, under its name, in storage — and nothing of it is left on the landing zone.
/// </summary>
[TestFixture]
[Category("Unit")]
public class LandingOverflowTests {

  private static readonly Guid _pool = Guid.Parse("1a0d1a0d-0000-0000-0000-000000000001");

  /// <summary>64 KiB, and the fast tier's default low watermark (75 %) — so about 48 KiB of room.</summary>
  private const long _SSD = 64 * 1024;

  private FakeVolumeIO _ssd = null!;
  private FakeVolumeIO _hdd1 = null!;
  private FakeVolumeIO _hdd2 = null!;

  [SetUp]
  public void SetUp() {
    this._ssd = new(Guid.NewGuid(), "ssd", "PHYS-SSD", capacity: _SSD);
    this._hdd1 = new(Guid.NewGuid(), "hdd1", "PHYS-HDD1", capacity: 1L << 24);
    this._hdd2 = new(Guid.NewGuid(), "hdd2", "PHYS-HDD2", capacity: 1L << 24);
  }

  private FakeVolumeIO[] _All => [this._ssd, this._hdd1, this._hdd2];

  private PoolFileSystem _Engine(string extra = "") {
    var cache = new CacheInstance("lo" + Guid.NewGuid().ToString("N"), new() { Size = "4194304", BlockSize = "4096", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._ssd, MemberRole.Landing), new(this._hdd1), new(this._hdd2)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 1, "readAhead": { "enabled": false }{{extra}} }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static byte[] _Content(int length, int seed = 5) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 13 + seed))];

  private static byte[] _Read(PoolFileSystem fs, string path) {
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var buffer = new byte[fs.GetAttributes(path).Length];
      var read = 0;
      while (read < buffer.Length && fs.Read(handle, buffer.AsSpan(read), read) is var got and > 0)
        read += got;

      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  private static void _WriteInChunks(PoolFileSystem fs, NodeHandle handle, byte[] content, int chunk = 8 * 1024) {
    for (var offset = 0; offset < content.Length; offset += chunk)
      fs.Write(handle, content.AsSpan(offset, Math.Min(chunk, content.Length - offset)), offset, WriteMode.Normal);
  }

  /// <summary>The file lives in storage, whole, and the landing zone holds nothing of it.</summary>
  private void _AssertInStorageOnly(PoolFileSystem fs, string path, byte[] content) {
    _Read(fs, path).Should().Equal(content);
    new[] { this._hdd1, this._hdd2 }.Count(v => v.GetContent(path, false) is { } c && c.SequenceEqual(content))
      .Should().Be(1, "the file is published on a storage disk");
    this._ssd.FilePaths.Where(p => !p.Contains(".drivebenderutility")).Should().BeEmpty("nothing of it is left on the landing zone");
  }

  [Test]
  [Category("HappyPath")]
  public void Landing_GivenAFileThatFits_ThenItLandsOnTheLandingZone() {
    using var fs = this._Engine();
    var content = _Content(16 * 1024);
    var handle = fs.Create("small.bin", NodeKind.File, CreateFlags.None);
    _WriteInChunks(fs, handle, content);
    fs.Close(handle);

    this._ssd.GetContent("small.bin", false).Should().Equal(content, "a file that fits lands in the landing zone");
  }

  [Test]
  [Category("EdgeCase")]
  public void Landing_GivenTheAnnouncedSizeDoesNotFit_ThenTheFileGoesStraightToStorage() {
    // copy tools announce the size before the first block; a file that cannot fit never lands
    using var fs = this._Engine();
    var content = _Content(200 * 1024);
    var handle = fs.Create("big.bin", NodeKind.File, CreateFlags.None);
    fs.SetLength(handle, content.Length);
    _WriteInChunks(fs, handle, content);
    fs.Close(handle);

    this._AssertInStorageOnly(fs, "big.bin", content);
  }

  [Test]
  [Category("EdgeCase")]
  public void Landing_GivenAFileOutgrowsTheLandingZoneWhileWritten_ThenItMovesToStorageAndContinuesThere() {
    using var fs = this._Engine();
    var content = _Content(200 * 1024);
    var handle = fs.Create("grows.bin", NodeKind.File, CreateFlags.None);
    _WriteInChunks(fs, handle, content); // no size announced: it starts in the landing zone and outgrows it

    _Read(fs, "grows.bin").Should().Equal(content, "while it is still open, every byte written so far reads back");
    fs.Close(handle);

    this._AssertInStorageOnly(fs, "grows.bin", content);
  }

  [Test]
  [Category("EdgeCase")]
  public void Landing_GivenOneLandingDiskAndStripingOff_ThenAnOutgrowingFileStillMovesToStorage() {
    // a single SSD is the usual landing zone, and moving a file that outgrows it is not a striping feature
    using var fs = this._Engine(""", "write": { "striping": false }""");
    var content = _Content(120 * 1024);
    var handle = fs.Create("grows.bin", NodeKind.File, CreateFlags.None);
    _WriteInChunks(fs, handle, content);
    fs.Close(handle);

    this._AssertInStorageOnly(fs, "grows.bin", content);
  }

  [Test]
  [Category("EdgeCase")]
  public void Landing_GivenAFileOutgrowsTheLandingZone_WhenThePowerIsCutAtEveryStep_ThenItIsWholeOrAbsent() {
    var content = _Content(96 * 1024);
    var stepsSeen = 0;
    for (var abortAfter = 1; ; ++abortAfter) {
      this.SetUp();
      var fired = 0;
      var remaining = abortAfter;
      void Hook(VolumeOp op, string path) {
        if (Volatile.Read(ref fired) == 1 || Interlocked.Decrement(ref remaining) == 0) {
          Interlocked.Exchange(ref fired, 1);
          throw new InvalidOperationException("power loss");
        }
      }

      var fs = this._Engine();
      foreach (var volume in this._All)
        volume.BeforeOperation = Hook;
      try {
        var handle = fs.Create("grows.bin", NodeKind.File, CreateFlags.None);
        _WriteInChunks(fs, handle, content);
        fs.Close(handle);
      } catch (Exception) {
        // the machine went down part-way
      }

      try {
        fs.Dispose();
      } catch (Exception) {
        // nothing shuts down cleanly without power
      }

      foreach (var volume in this._All)
        volume.BeforeOperation = null;

      if (fired == 0)
        break; // past the end: every step has been cut once

      stepsSeen = abortAfter;
      foreach (var volume in this._All)
        volume.SimulateCrash();

      using var recovered = this._Engine();
      foreach (var volume in this._All)
      foreach (var shadow in new[] { false, true })
        if (volume.GetContent("grows.bin", shadow) is { } copy)
          copy.Should().Equal(content, $"power cut at step {abortAfter}: a copy under the real name must be whole");

      this._All.SelectMany(v => v.FilePaths).Should().NotContain(p => p.Contains("TEMP.$DRIVEBENDER"),
        $"power cut at step {abortAfter}: recovery sweeps every temp");
    }

    stepsSeen.Should().BeGreaterThan(20, "the move to storage is part of what was interrupted, step by step");
  }


  [TestCase(2, 2, TestName = "Drain_GivenTwoAtOnce_ThenTwoFilesMoveInOnePassToDifferentStorageDisks")]
  [TestCase(1, 1, TestName = "Drain_GivenOneAtATime_ThenOneFileMovesPerPass")]
  [Category("HappyPath")]
  public void Drain_GivenSeveralSettledFiles_ThenAPassMovesUpToTheConcurrencyEachToItsOwnDisk(int concurrency, int expected) {
    using var fs = this._Engine($$""", "tiers": { "fast": { "drainConcurrency": {{concurrency}} } }""");
    for (var i = 0; i < 4; ++i) {
      var handle = fs.Create($"s{i}.bin", NodeKind.File, CreateFlags.None);
      fs.Write(handle, _Content(4 * 1024, i), 0, WriteMode.Normal);
      fs.Close(handle);
    }

    this._ssd.FilePaths.Count(p => p.StartsWith("s", StringComparison.Ordinal)).Should().Be(4, "all four landed first");

    // slow storage writes, so concurrent moves genuinely overlap
    foreach (var hdd in new[] { this._hdd1, this._hdd2 })
      hdd.BeforeOperation = (op, _) => {
        if (op == VolumeOp.Write)
          Thread.Sleep(30);
      };

    fs.DrainLandingFiles().Should().BeTrue();
    foreach (var hdd in new[] { this._hdd1, this._hdd2 })
      hdd.BeforeOperation = null;

    var moved = Enumerable.Range(0, 4).Select(i => $"s{i}.bin").Where(p => !this._ssd.FileExists(p, false)).ToArray();
    moved.Should().HaveCount(expected, $"a pass moves up to drainConcurrency ({concurrency}) files");
    moved.Select(p => this._hdd1.FileExists(p, false) ? "hdd1" : "hdd2").Distinct().Should().HaveCount(expected,
      "concurrent moves go to different storage disks");
  }

}
