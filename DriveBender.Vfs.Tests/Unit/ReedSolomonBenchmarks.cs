using System.Diagnostics;
using System.Globalization;
using DivisonM.Vfs.Integrity;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Codec throughput on 64 KiB shards, in MB/s of DATA processed per core (k x shard size per
/// stripe). Explicit and never a gate: absolute rates are machine-dependent; each run only checks
/// that the work it timed was real.
/// </summary>
[TestFixture]
[Explicit("benchmark — run by name")]
[Category("Performance")]
[NonParallelizable]
public class ReedSolomonBenchmarks {

  private const int _SHARD = 64 * 1024;

  [TestCase(32, 2)]
  [TestCase(32, 4)]
  [TestCase(32, 16)]
  public void Encode(int k, int m) {
    var shards = ReedSolomonTests.NewShards(1, k, m, _SHARD);
    foreach (var kernel in Kernels()) {
      var codec = new ReedSolomon(k, m) { Kernel = kernel };
      var rate = Measure(() => codec.EncodeParity(shards, 0, _SHARD), (long)k * _SHARD);
      TestContext.Out.WriteLine($"encode {k}+{m} {kernel,-9}: {rate.ToString("N0", CultureInfo.InvariantCulture),8} MB/s");
    }

    new ReedSolomon(k, m).IsParityCorrect(shards, 0, _SHARD).Should().BeTrue();
  }

  [TestCase(32, 2)]
  [TestCase(32, 4)]
  public void DecodeTwoMissing(int k, int m) {
    var shards = ReedSolomonTests.NewShards(2, k, m, _SHARD);
    new ReedSolomon(k, m).EncodeParity(shards, 0, _SHARD);
    var original = (byte[])shards[5].Clone();

    var present = Enumerable.Repeat(true, k + m).ToArray();
    present[5] = present[20] = false; // two data shards lost

    foreach (var kernel in Kernels()) {
      var codec = new ReedSolomon(k, m) { Kernel = kernel };
      var rate = Measure(() => codec.DecodeMissing(shards, present, 0, _SHARD), (long)k * _SHARD);
      TestContext.Out.WriteLine($"decode {k}+{m}, 2 missing {kernel,-9}: {rate.ToString("N0", CultureInfo.InvariantCulture),8} MB/s");
      shards[5].Should().Equal(original);
    }
  }

  private static IEnumerable<ReedSolomon.KernelKind> Kernels()
    => new[] { ReedSolomon.KernelKind.Vector256, ReedSolomon.KernelKind.Vector128, ReedSolomon.KernelKind.Scalar }
      .Where(ReedSolomon.IsSupported);

  private static double Measure(Action stripe, long bytesPerStripe) {
    for (var i = 0; i < 5; ++i)
      stripe(); // warm-up and tiering

    var best = 0d;
    for (var round = 0; round < 3; ++round) {
      var watch = Stopwatch.StartNew();
      long stripes = 0;
      while (watch.ElapsedMilliseconds < 400) {
        stripe();
        ++stripes;
      }

      best = Math.Max(best, stripes * bytesPerStripe / watch.Elapsed.TotalSeconds / 1e6);
    }

    return best;
  }
}
