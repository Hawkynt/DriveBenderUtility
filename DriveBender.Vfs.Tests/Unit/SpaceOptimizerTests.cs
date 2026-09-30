using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The space optimizer (docs/SpaceSavings.md): it may share data between different files with the
/// same content and release runs of zeros — and must never change what any file holds, never pair a
/// file with its own copy, and never make a hard link where the files' metadata differs.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SpaceOptimizerTests {

  private const BackendCaps _BASE = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.AtomicRename | BackendCaps.DurableFlush
                                    | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps;

  private static readonly byte[] _CONTENT = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7 + 1))];

  private sealed class Held : IDisposable {
    public void Dispose() { }
  }

  private static FakeVolumeIO _Disk(BackendCaps extra) => new(Guid.NewGuid(), "disk", "PHYS-1", capacity: 1L << 26) { Caps = _BASE | extra };

  private static SpaceReport _Optimize(FakeVolumeIO disk, Func<string, IDisposable?>? lockPath = null, bool deduplicate = true, bool sparsify = true)
    => new SpaceOptimizer([disk], lockPath ?? (_ => new Held()), deduplicate, sparsify).Run();

  [Test]
  [Category("HappyPath")]
  public void Dedup_GivenTwoFilesWithTheSameContentAndMetadata_ThenTheyShareTheirData() {
    var disk = _Disk(BackendCaps.HardLinks);
    disk.Seed("docs/a.bin", false, _CONTENT);
    disk.Seed("backup/a.bin", false, _CONTENT);

    var report = _Optimize(disk);

    report.FilesDeduplicated.Should().Be(1);
    report.BytesDeduplicated.Should().Be(_CONTENT.Length);
    disk.LinkCount("docs/a.bin", false).Should().Be(2);
    disk.GetContent("backup/a.bin", false).Should().Equal(_CONTENT, "sharing never changes what a file holds");
  }

  [Test]
  [Category("EdgeCase")]
  public void Dedup_GivenTheSameContentButDifferentTimes_ThenNoHardLinkIsMade() {
    // a hard link shares times too: linking would change one file's modification time
    var disk = _Disk(BackendCaps.HardLinks);
    disk.Seed("a.bin", false, _CONTENT);
    disk.Seed("b.bin", false, _CONTENT);
    disk.SetTimestamps("b.bin", false, null, new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    var report = _Optimize(disk);

    report.FilesDeduplicated.Should().Be(0);
    report.SkippedMetadataDiffers.Should().Be(1);
    disk.LinkCount("a.bin", false).Should().Be(1);
  }

  [Test]
  [Category("HappyPath")]
  public void Dedup_GivenBlockCloning_ThenFilesWithDifferentTimesShareBlocksAndKeepTheirOwnTimes() {
    var disk = _Disk(BackendCaps.HardLinks | BackendCaps.BlockClone);
    disk.Seed("a.bin", false, _CONTENT);
    disk.Seed("b.bin", false, _CONTENT);
    var bTime = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    disk.SetTimestamps("b.bin", false, null, bTime);

    var report = _Optimize(disk);

    report.FilesDeduplicated.Should().Be(1, "a clone keeps its own metadata, so differing times are no obstacle");
    disk.LinkCount("b.bin", false).Should().Be(1, "cloned, not linked");
    disk.Stat("b.bin", false)!.Value.LastWriteTimeUtc.Should().Be(bTime, "the clone carries the duplicate's own time");
    disk.GetContent("b.bin", false).Should().Equal(_CONTENT);
  }

  [Test]
  [Category("Exception")]
  public void Dedup_GivenAFilesOwnTwoCopiesOnOneDisk_ThenTheyAreNeverPaired() {
    // co-located primary and shadow of ONE file: sharing them would undo its duplication
    var disk = _Disk(BackendCaps.HardLinks | BackendCaps.BlockClone);
    disk.Seed("a.bin", false, _CONTENT);
    disk.Seed("a.bin", true, _CONTENT);

    _Optimize(disk).FilesDeduplicated.Should().Be(0);
    disk.LinkCount("a.bin", false).Should().Be(1);
    disk.LinkCount("a.bin", true).Should().Be(1);
  }

  [Test]
  [Category("EdgeCase")]
  public void Dedup_GivenABusyFile_ThenItIsSkippedAndLeftAlone() {
    var disk = _Disk(BackendCaps.HardLinks);
    disk.Seed("a.bin", false, _CONTENT);
    disk.Seed("b.bin", false, _CONTENT);

    var report = _Optimize(disk, path => path == "b.bin" ? null : new Held());

    report.SkippedBusy.Should().Be(1);
    disk.LinkCount("b.bin", false).Should().Be(1);
  }

  [TestCase(1024, TestName = "Dedup_GivenSmallFiles_ThenTheyAreNotWorthIt")]
  [TestCase(-1, TestName = "Dedup_GivenTheSameSizeButDifferentContent_ThenNothingIsShared")]
  [Category("EdgeCase")]
  public void Dedup_GivenFilesThatDoNotQualify_ThenNothingIsShared(int size) {
    var disk = _Disk(BackendCaps.HardLinks);
    var other = (byte[])_CONTENT.Clone();
    other[^1] ^= 0xFF; // same size, one byte different
    disk.Seed("a.bin", false, size > 0 ? _CONTENT[..size] : _CONTENT);
    disk.Seed("b.bin", false, size > 0 ? _CONTENT[..size] : other);

    _Optimize(disk).FilesDeduplicated.Should().Be(0);
    disk.LinkCount("a.bin", false).Should().Be(1);
  }

  [Test]
  [Category("HappyPath")]
  public void Sparse_GivenLongRunsOfZeros_ThenTheyAreReleasedAndTheFileIsUnchanged() {
    var disk = _Disk(BackendCaps.Sparse);
    var content = new byte[SpaceOptimizer.SparseUnit * 5];
    content[0] = 1;
    content[^1] = 2; // zeros in the middle three megabytes
    disk.Seed("image.bin", false, content);
    var times = disk.Stat("image.bin", false)!.Value;

    var report = _Optimize(disk);

    report.FilesSparsified.Should().Be(1);
    report.BytesReleased.Should().Be(SpaceOptimizer.SparseUnit * 3L);
    disk.GetContent("image.bin", false).Should().Equal(content, "a released range reads as the zeros it held");
    disk.Stat("image.bin", false)!.Value.LastWriteTimeUtc.Should().Be(times.LastWriteTimeUtc, "releasing space does not count as the user's write");
  }

  [Test]
  [Category("EdgeCase")]
  public void Nothing_GivenStorageWithoutTheCapabilities_ThenNothingIsTouched() {
    var disk = _Disk(BackendCaps.None);
    disk.Seed("a.bin", false, _CONTENT);
    disk.Seed("b.bin", false, _CONTENT);

    _Optimize(disk).Should().Be(SpaceReport.Empty);
  }

  [Test]
  [Category("EdgeCase")]
  public void Dedup_GivenThePowerIsCutAtEveryStep_ThenBothFilesSurviveWholeAndNoTempRemainsAfterRecovery() {
    for (var step = 1; ; ++step) {
      var disk = _Disk(BackendCaps.HardLinks);
      disk.Seed("a.bin", false, _CONTENT);
      disk.Seed("b.bin", false, _CONTENT);

      var fired = 0;
      var remaining = step;
      disk.BeforeOperation = (_, _) => {
        if (Volatile.Read(ref fired) == 1 || Interlocked.Decrement(ref remaining) == 0) {
          Interlocked.Exchange(ref fired, 1);
          throw new InvalidOperationException("power loss");
        }
      };

      try {
        _Optimize(disk);
      } catch (Exception) {
        // down part-way
      }

      disk.BeforeOperation = null;
      if (fired == 0) {
        step.Should().BeGreaterThan(3);
        break;
      }

      disk.SimulateCrash();
      var cache = new CacheInstance("so" + Guid.NewGuid().ToString("N"), new() { Size = "1048576", BlockSize = "512", MetadataEntries = 100, MetadataTtl = "1m" });
      using (var recovered = new PoolFileSystem(Guid.NewGuid(), [new(disk)], cache, ConfigResolver.ResolveEffective(null, null)))
        recovered.Mount(new(@"X:\")); // recovery sweeps temps

      disk.GetContent("a.bin", false).Should().Equal(_CONTENT, $"power cut at step {step}");
      disk.GetContent("b.bin", false).Should().Equal(_CONTENT, $"power cut at step {step}");
      disk.FilePaths.Should().NotContain(p => p.Contains("TEMP.$DRIVEBENDER"), $"power cut at step {step}: recovery sweeps the temp");
    }
  }

}
