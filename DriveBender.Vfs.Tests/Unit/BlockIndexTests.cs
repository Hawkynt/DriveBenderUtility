using DivisonM.Vfs.Integrity;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class BlockIndexTests {

  private static readonly long[] _sizes = [0, 1, 63, 64, 1000, 65_535, 65_536, 65_537, 2 * 1024 * 1024 - 1, 2 * 1024 * 1024, 2 * 1024 * 1024 + 1, 5 * 1024 * 1024 + 123];

  private static byte[] _Content(long size, int seed = 7) {
    var bytes = new byte[size];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static BlockIndex _Index(byte[] content, int parity)
    => BlockIndex.Build(new MemoryStream(content), content.Length, 123456789, parity);

  private static byte[] _Shard(BlockIndex index, byte[] content, int stripe, int shard) {
    var (start, length) = index.ShardRange(stripe, shard);
    return content.AsSpan((int)start, length).ToArray();
  }

  [Test]
  public void Shards_GivenAnySize_ThenTheyTileTheFileExactlyOnce([ValueSource(nameof(_sizes))] long size) {
    var index = _Index(_Content(size), 2);

    var covered = new bool[size];
    foreach (var (stripe, shard) in index.ShardsOverlapping(0, size)) {
      var (start, length) = index.ShardRange(stripe, shard);
      length.Should().BeGreaterThan(0);
      for (var i = start; i < start + length; ++i) {
        covered[i].Should().BeFalse($"byte {i} is in exactly one shard");
        covered[i] = true;
      }
    }

    if (size > 0)
      covered.Should().OnlyContain(c => c);
  }

  [Test]
  public void Shards_GivenAnySize_ThenTheyNeverStraddleA64KiBBoundary([ValueSource(nameof(_sizes))] long size) {
    var index = _Index(_Content(size), 1);
    foreach (var (stripe, shard) in index.ShardsOverlapping(0, size)) {
      var (start, length) = index.ShardRange(stripe, shard);
      (start / 65536).Should().Be((start + length - 1) / 65536, "a shard is verifiable from one 64 KiB cache block");
    }
  }

  [Test]
  public void Hashes_GivenTheOriginalContent_ThenEveryShardMatches([ValueSource(nameof(_sizes))] long size) {
    var content = _Content(size);
    var index = _Index(content, 2);

    foreach (var (stripe, shard) in index.ShardsOverlapping(0, size))
      BlockIndex.Hash(_Shard(index, content, stripe, shard)).Should().Be(index.ExpectedHash(stripe, shard));
  }

  [Test]
  public void Serialize_ThenDeserialize_ThenEverythingRoundTrips([ValueSource(nameof(_sizes))] long size, [Values(0, 1, 16)] int parity) {
    var index = _Index(_Content(size), parity);

    var back = BlockIndex.Deserialize(index.Serialize());

    back.Should().NotBeNull();
    back!.Length.Should().Be(size);
    back.MTimeTicks.Should().Be(123456789);
    back.ParityShards.Should().Be(parity);
    back.Serialize().Should().Equal(index.Serialize());
  }

  [Test]
  public void Deserialize_GivenAnyFlippedByte_ThenTheIndexIsRejected() {
    var bytes = _Index(_Content(300_000), 2).Serialize();
    var random = new Random(3);
    for (var trial = 0; trial < 200; ++trial) {
      var damaged = (byte[])bytes.Clone();
      damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
      BlockIndex.Deserialize(damaged).Should().BeNull("a rotted index must never be trusted");
    }

    BlockIndex.Deserialize(bytes[..^1]).Should().BeNull();
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(4)]
  [TestCase(8)]
  [TestCase(16)]
  public void RepairStripe_GivenUpToParityShardsDamaged_ThenTheContentIsRebuiltExactly(int parity) {
    var content = _Content(5 * 1024 * 1024 + 4321, 11);
    var index = _Index(content, parity);
    var random = new Random(parity);

    for (var stripe = 0; stripe < index.StripeCount; ++stripe) {
      var count = BlockIndex.ShardCountFor(BlockIndex.StripeLength(content.Length, stripe));
      var damaged = Enumerable.Range(0, count).OrderBy(_ => random.Next()).Take(Math.Min(parity, count)).ToHashSet();

      var repaired = index.RepairStripe(stripe, shard => {
        var bytes = _Shard(index, content, stripe, shard);
        if (damaged.Contains(shard))
          bytes[random.Next(bytes.Length)] ^= 0x5A; // silent rot
        return bytes;
      });

      repaired.Should().NotBeNull();
      for (var shard = 0; shard < count; ++shard)
        repaired![shard].Should().Equal(_Shard(index, content, stripe, shard));
    }
  }

  [Test]
  public void RepairStripe_GivenUnreadableShards_ThenTheyAreRebuiltToo() {
    var content = _Content(3 * 1024 * 1024, 5);
    var index = _Index(content, 2);

    var repaired = index.RepairStripe(1, shard => shard is 0 or 7 ? null : _Shard(index, content, 1, shard));

    repaired.Should().NotBeNull();
    repaired![7].Should().Equal(_Shard(index, content, 1, 7));
  }

  [Test]
  public void RepairStripe_GivenMoreDamageThanParity_ThenItRefusesRatherThanGuessing() {
    var content = _Content(2 * 1024 * 1024, 9);
    var index = _Index(content, 2);

    index.RepairStripe(0, shard => shard < 3 ? new byte[index.ShardRange(0, shard).Length] : _Shard(index, content, 0, shard))
      .Should().BeNull();
  }

  [Test]
  public void RepairStripe_GivenNoParity_ThenOnlyAnIntactStripeIsReturned() {
    var content = _Content(100_000, 4);
    var index = _Index(content, 0);

    index.RepairStripe(0, shard => _Shard(index, content, 0, shard)).Should().NotBeNull();
    index.RepairStripe(0, shard => shard == 1 ? null : _Shard(index, content, 0, shard)).Should().BeNull();
  }

  [Test]
  public void Parity_GivenSmallFiles_ThenItCostsAboutTheConfiguredShare() {
    foreach (var size in new[] { 1000, 10_000, 100_000, 1_000_000 }) {
      var index = _Index(_Content(size), 2);
      var overhead = index.Serialize().Length - 24 - 8 - BlockIndex.ShardCountFor(size) * 8;
      ((double)overhead / size).Should().BeLessThan(0.2, $"parity for {size} bytes must stay near 2/32, not a whole block per shard");
    }
  }

  [Test]
  public void Build_GivenAStreamThatChangesLength_ThenItRefuses() {
    var content = _Content(1000);
    var build = () => BlockIndex.Build(new MemoryStream(content), 2000, 0, 1);
    build.Should().Throw<IOException>();
    var longer = () => BlockIndex.Build(new MemoryStream(content), 500, 0, 1);
    longer.Should().Throw<IOException>();
  }

}
