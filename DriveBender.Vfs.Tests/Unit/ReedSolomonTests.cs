using System.Numerics;
using DivisonM.Vfs.Integrity;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The erasure codec behind per-file parity. A wrong parity byte is silent until the day it is
/// needed, so reconstruction is proven byte-exact for every erasure pattern the code is meant to
/// survive (exhaustively where the pattern count is small, sampled where not), the vector paths
/// are proven identical to the scalar one, and bytes outside the coded range are proven untouched.
/// Agreement with the original implementation is pinned separately in <see cref="ReedSolomonOracleTests"/>.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ReedSolomonTests {

  internal static readonly int[] ParityCounts = [1, 2, 4, 8, 16];

  /// <summary>The 64-bit LCG the Java oracle uses too, so both sides see the same data.</summary>
  internal static byte[][] NewShards(long seed, int dataShards, int parityShards, int shardSize) {
    var state = (ulong)seed;
    var shards = new byte[dataShards + parityShards][];
    for (var i = 0; i < shards.Length; ++i)
      shards[i] = new byte[shardSize];

    for (var i = 0; i < dataShards; ++i)
    for (var j = 0; j < shardSize; ++j) {
      state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
      shards[i][j] = (byte)(state >> 56);
    }

    return shards;
  }

  private static byte[][] Clone(byte[][] shards) => shards.Select(s => (byte[])s.Clone()).ToArray();

  /// <summary>Erases the shards in <paramref name="missingMask"/>, decodes, and demands the original back.</summary>
  private static void AssertRecovers(ReedSolomon codec, byte[][] original, ulong missingMask, int offset, int byteCount) {
    var damaged = Clone(original);
    var present = new bool[codec.TotalShards];
    for (var i = 0; i < present.Length; ++i) {
      present[i] = (missingMask & (1UL << i)) == 0;
      if (!present[i])
        damaged[i].AsSpan(offset, byteCount).Fill(0xA5);
    }

    codec.DecodeMissing(damaged, present, offset, byteCount);

    for (var i = 0; i < original.Length; ++i)
      if (!damaged[i].AsSpan().SequenceEqual(original[i]))
        Assert.Fail($"k={codec.DataShards} m={codec.ParityShards} missing=0x{missingMask:X}: shard {i} not reconstructed");
  }

  #region construction

  [Test]
  [Category("Exception")]
  public void Constructor_GivenShardCountsOutOfRange_ThenThrows(
    [Values(0, 33)] int badData, [Values(0, 17)] int badParity) {
    FluentActions.Invoking(() => new ReedSolomon(badData, 2)).Should().Throw<ArgumentOutOfRangeException>();
    FluentActions.Invoking(() => new ReedSolomon(4, badParity)).Should().Throw<ArgumentOutOfRangeException>();
  }

  [Test]
  [Category("HappyPath")]
  public void Constructor_GivenLimits_ThenAccepted() {
    var codec = new ReedSolomon(32, 16);
    codec.TotalShards.Should().Be(48);
    new ReedSolomon(1, 1).TotalShards.Should().Be(2);
  }

  [Test]
  [Category("HappyPath")]
  public void Galois_GivenTheField_ThenEveryNonZeroElementHasAnInverseAndProductsMatchTheTable() {
    for (var a = 1; a < 256; ++a) {
      var inverse = ReedSolomon.Galois.Divide(1, (byte)a);
      ReedSolomon.Galois.Multiply((byte)a, inverse).Should().Be(1, $"{a} must have an inverse");
    }

    // multiplication by shift-and-reduce with polynomial 0x11D, independent of the log tables
    for (var a = 0; a < 256; ++a)
    for (var b = 0; b < 256; ++b) {
      int x = a, y = b, product = 0;
      while (y != 0) {
        if ((y & 1) != 0)
          product ^= x;
        x <<= 1;
        if ((x & 0x100) != 0)
          x ^= 0x11D;
        y >>= 1;
      }

      if (ReedSolomon.Galois.MultiplicationTable[(a << 8) | b] != product)
        Assert.Fail($"{a}*{b}: table {ReedSolomon.Galois.MultiplicationTable[(a << 8) | b]}, expected {product}");
    }
  }

  #endregion

  #region round trips

  [Test]
  [Category("HappyPath")]
  public void EncodeThenDecode_GivenEveryParityCountAndAnyMissingSetOfThatSize_ThenRecoversByteExact(
    [ValueSource(nameof(ParityCounts))] int m, [Values(1, 7, 32)] int k) {
    var codec = new ReedSolomon(k, m);
    var shards = NewShards(k * 100 + m, k, m, 65536 + 13);
    codec.EncodeParity(shards, 0, shards[0].Length);
    codec.IsParityCorrect(shards, 0, shards[0].Length).Should().BeTrue();

    // data shards pass through unchanged: the top square of the matrix is the identity
    var pristine = NewShards(k * 100 + m, k, m, 65536 + 13);
    for (var i = 0; i < k; ++i)
      shards[i].AsSpan().SequenceEqual(pristine[i]).Should().BeTrue($"data shard {i} is unchanged by encoding");

    var random = new Random(k * 31 + m);
    for (var trial = 0; trial < 20; ++trial) {
      var count = trial == 0 ? m : random.Next(1, m + 1);
      ulong mask = 0;
      foreach (var index in Enumerable.Range(0, k + m).OrderBy(_ => random.Next()).Take(count))
        mask |= 1UL << index;
      AssertRecovers(codec, shards, mask, 0, shards[0].Length);
    }
  }

  [Test]
  [Category("HappyPath")]
  public void Decode_GivenEveryErasurePatternUpToParity_ThenRecoversByteExact(
    [Values(1, 2, 3, 5, 7, 10, 32)] int k, [ValueSource(nameof(ParityCounts))] int m) {
    var total = k + m;
    var codec = new ReedSolomon(k, m);
    var shards = NewShards(total, k, m, 97);
    codec.EncodeParity(shards, 0, shards[0].Length);

    var tested = 0;
    if (total <= 12) {
      // exhaustive: every non-empty subset of at most m shards
      for (ulong mask = 1; mask < 1UL << total; ++mask)
        if (BitOperations.PopCount(mask) <= m) {
          AssertRecovers(codec, shards, mask, 0, shards[0].Length);
          ++tested;
        }
    } else {
      var random = new Random(total * 7919);
      for (; tested < 400; ++tested) {
        var count = random.Next(1, m + 1);
        ulong mask = 0;
        while (BitOperations.PopCount(mask) < count)
          mask |= 1UL << random.Next(total);
        AssertRecovers(codec, shards, mask, 0, shards[0].Length);
      }
    }

    tested.Should().BeGreaterThan(0);
  }

  [Test]
  [Category("Exception")]
  public void Decode_GivenMoreMissingThanParity_ThenThrowsAndWritesNothing([ValueSource(nameof(ParityCounts))] int m) {
    var codec = new ReedSolomon(7, m);
    var shards = NewShards(3, 7, m, 64);
    codec.EncodeParity(shards, 0, 64);
    var before = Clone(shards);

    var present = Enumerable.Range(0, 7 + m).Select(i => i > m).ToArray(); // m + 1 missing
    FluentActions.Invoking(() => codec.DecodeMissing(shards, present, 0, 64)).Should().Throw<ArgumentException>();
    shards.Should().BeEquivalentTo(before, o => o.WithStrictOrdering());
  }

  [Test]
  [Category("EdgeCase")]
  public void Decode_GivenNothingMissing_ThenLeavesEverythingAlone() {
    var codec = new ReedSolomon(4, 2);
    var shards = NewShards(5, 4, 2, 50);
    var before = Clone(shards);
    codec.DecodeMissing(shards, Enumerable.Repeat(true, 6).ToArray(), 0, 50);
    shards.Should().BeEquivalentTo(before, o => o.WithStrictOrdering());
    codec.DecodePlanBuilds.Should().Be(0);
  }

  #endregion

  #region ranges

  [Test]
  [Category("EdgeCase")]
  public void Code_GivenOddOffsetsAndLengths_ThenOnlyTheRangeIsTouchedAndItMatchesAWholeBufferCode(
    [Values(0, 1, 3, 17, 33)] int offset, [Values(0, 1, 15, 16, 31, 33, 4095, 4097, 65537)] int byteCount) {
    const int k = 5, m = 3, slack = 40;
    var codec = new ReedSolomon(k, m);
    var size = offset + byteCount + slack;
    var shards = NewShards(offset * 1000 + byteCount, k, m, size);
    for (var p = k; p < k + m; ++p)
      shards[p].AsSpan().Fill(0xEE); // sentinel outside the range

    codec.EncodeParity(shards, offset, byteCount);

    // reference: the same bytes coded as whole buffers starting at 0
    var reference = new byte[k + m][];
    for (var i = 0; i < k; ++i)
      reference[i] = shards[i].AsSpan(offset, byteCount).ToArray();
    for (var p = k; p < k + m; ++p)
      reference[p] = new byte[byteCount];
    codec.EncodeParity(reference, 0, byteCount);

    for (var p = k; p < k + m; ++p) {
      shards[p].AsSpan(offset, byteCount).SequenceEqual(reference[p]).Should().BeTrue($"parity {p} over the range");
      shards[p].AsSpan(0, offset).ToArray().All(b => b == 0xEE).Should().BeTrue("bytes before the range are untouched");
      shards[p].AsSpan(offset + byteCount).ToArray().All(b => b == 0xEE).Should().BeTrue("bytes after the range are untouched");
    }

    codec.IsParityCorrect(shards, offset, byteCount).Should().BeTrue();

    // erase a data and a parity shard inside the range only and recover them
    var damaged = Clone(shards);
    damaged[1].AsSpan(offset, byteCount).Clear();
    damaged[k].AsSpan(offset, byteCount).Clear();
    var present = Enumerable.Range(0, k + m).Select(i => i != 1 && i != k).ToArray();
    codec.DecodeMissing(damaged, present, offset, byteCount);
    for (var i = 0; i < k + m; ++i)
      damaged[i].Should().Equal(shards[i], $"shard {i}, including the untouched bytes around the range");
  }

  [Test]
  [Category("HappyPath")]
  public void IsParityCorrect_GivenOneFlippedBit_ThenFalse([Values(0, 1000, 65535, 70000)] int where) {
    var codec = new ReedSolomon(6, 4);
    var shards = NewShards(where, 6, 4, 70001);
    codec.EncodeParity(shards, 0, 70001);
    codec.IsParityCorrect(shards, 0, 70001).Should().BeTrue();

    shards[3][where] ^= 0x10;
    codec.IsParityCorrect(shards, 0, 70001).Should().BeFalse("a data change makes the parity wrong");
    shards[3][where] ^= 0x10;
    shards[9][where] ^= 0x01;
    codec.IsParityCorrect(shards, 0, 70001).Should().BeFalse("so does a parity change");
  }

  [Test]
  [Category("Exception")]
  public void Calls_GivenBadBuffers_ThenThrow() {
    var codec = new ReedSolomon(3, 2);
    var shards = NewShards(1, 3, 2, 10);

    FluentActions.Invoking(() => codec.EncodeParity(shards.Take(4).ToArray(), 0, 10)).Should().Throw<ArgumentException>();
    FluentActions.Invoking(() => codec.EncodeParity(shards, 5, 6)).Should().Throw<ArgumentException>("the range overruns the buffers");
    FluentActions.Invoking(() => codec.EncodeParity(shards, -1, 1)).Should().Throw<ArgumentOutOfRangeException>();

    var aliasedParity = Clone(shards);
    aliasedParity[4] = aliasedParity[3];
    FluentActions.Invoking(() => codec.EncodeParity(aliasedParity, 0, 10)).Should().Throw<ArgumentException>("a written shard must not share a buffer");

    FluentActions.Invoking(() => codec.DecodeMissing(shards, new bool[4], 0, 10)).Should().Throw<ArgumentException>();
  }

  [Test]
  [Category("EdgeCase")]
  public void Encode_GivenTailShardsSharingOneZeroBuffer_ThenAcceptedAndRecoverable() {
    // the end of a file: the last data shards are all zero padding, backed by a single buffer
    var codec = new ReedSolomon(6, 2);
    var shards = NewShards(9, 6, 2, 300);
    var zero = new byte[300];
    shards[4] = zero;
    shards[5] = zero;
    codec.EncodeParity(shards, 0, 300);

    var damaged = Clone(shards);
    var present = new[] { true, false, true, true, true, true, false, true };
    codec.DecodeMissing(damaged, present, 0, 300);
    damaged[1].Should().Equal(shards[1]);
    damaged[6].Should().Equal(shards[6]);
  }

  #endregion

  #region kernels

  private static IEnumerable<ReedSolomon.KernelKind> SupportedKernels()
    => new[] { ReedSolomon.KernelKind.Scalar, ReedSolomon.KernelKind.Vector128, ReedSolomon.KernelKind.Vector256 }
      .Where(ReedSolomon.IsSupported);

  [Test]
  [Category("HappyPath")]
  public void Kernels_GivenTheSameInput_ThenProduceIdenticalOutput(
    [Values(1, 2, 3, 4, 5, 7, 8, 16)] int m, [Values(1, 3, 7, 32)] int k, [Values(1, 31, 4096 + 47, 65536)] int size) {
    // m covers every group shape of the vector kernels (1..4 outputs, and groups of 4 plus a rest)
    var kernels = SupportedKernels().ToArray();
    TestContext.Out.WriteLine("kernels: " + string.Join(", ", kernels));

    byte[][]? scalarEncoded = null;
    byte[][]? scalarDecoded = null;
    var missing = Enumerable.Range(0, k + m).Where(i => i % 2 == 0).Take(m).ToHashSet();
    var present = Enumerable.Range(0, k + m).Select(i => !missing.Contains(i)).ToArray();

    foreach (var kernel in kernels) {
      var codec = new ReedSolomon(k, m) { Kernel = kernel };
      var shards = NewShards(size + k, k, m, size + 3);
      codec.EncodeParity(shards, 3, size);

      var decoded = Clone(shards);
      foreach (var i in missing)
        decoded[i].AsSpan(3).Fill(0x5A);
      codec.DecodeMissing(decoded, present, 3, size);

      if (scalarEncoded == null) {
        kernel.Should().Be(ReedSolomon.KernelKind.Scalar);
        scalarEncoded = shards;
        scalarDecoded = decoded;
        continue;
      }

      for (var i = 0; i < k + m; ++i) {
        shards[i].Should().Equal(scalarEncoded[i], $"{kernel} encode, shard {i}");
        decoded[i].Should().Equal(scalarDecoded![i], $"{kernel} decode, shard {i}");
      }
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Kernel_GivenUnsupportedOnThisCpu_ThenRefused() {
    var codec = new ReedSolomon(2, 1);
    foreach (var kind in Enum.GetValues<ReedSolomon.KernelKind>().Where(k => !ReedSolomon.IsSupported(k)))
      FluentActions.Invoking(() => codec.Kernel = kind).Should().Throw<PlatformNotSupportedException>();
  }

  #endregion

  #region decode plan cache

  [Test]
  [Category("HappyPath")]
  public void Decode_GivenTheSameMissingSetAgain_ThenReusesThePlan() {
    var codec = new ReedSolomon(10, 4);
    var shards = NewShards(77, 10, 4, 1000);
    codec.EncodeParity(shards, 0, 1000);

    AssertRecovers(codec, shards, 0b101, 0, 1000);
    AssertRecovers(codec, shards, 0b101, 0, 1000);
    AssertRecovers(codec, shards, 0b101, 0, 1000);
    codec.DecodePlanBuilds.Should().Be(1, "one inversion serves every repair of the same pattern");

    AssertRecovers(codec, shards, 0b11 << 10, 0, 1000);
    codec.DecodePlanBuilds.Should().Be(2);
  }

  [Test]
  [Category("EdgeCase")]
  public void DecodeCache_GivenManyPatterns_ThenStaysBoundedAndEvictsTheLeastRecent() {
    var codec = new ReedSolomon(32, 2);
    var shards = NewShards(5, 32, 2, 64);
    codec.EncodeParity(shards, 0, 64);

    for (var i = 0; i < 34; ++i)
      AssertRecovers(codec, shards, 1UL << i, 0, 64);
    codec.CachedDecodePlans.Should().BeLessThanOrEqualTo(32);
    codec.DecodePlanBuilds.Should().Be(34);

    AssertRecovers(codec, shards, 1UL << 33, 0, 64); // most recent: still cached
    codec.DecodePlanBuilds.Should().Be(34);
    AssertRecovers(codec, shards, 1UL << 0, 0, 64); // oldest: evicted, rebuilt
    codec.DecodePlanBuilds.Should().Be(35);
  }

  [Test]
  [Category("Concurrency")]
  public void Decode_GivenParallelRepairs_ThenAllRecoverCorrectly() {
    var codec = new ReedSolomon(8, 4);
    var shards = NewShards(11, 8, 4, 5000);
    codec.EncodeParity(shards, 0, 5000);

    Parallel.For(0, 200, i => AssertRecovers(codec, shards, (ulong)((i % 12) | (1 << ((i * 5) % 12))), 0, 5000));
  }

  #endregion
}
