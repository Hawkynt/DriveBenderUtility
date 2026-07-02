using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Each ICacheEvictionPolicy against known access traces (FR-EVICT, §11.1).</summary>
[TestFixture]
[Category("Unit")]
public class EvictionPolicyTests {

  private static ICacheEvictionPolicy<string> _Create(EvictionPolicy policy, int capacity = 4)
    => EvictionPolicyFactory.Create<string>(policy, capacity);

  [Test]
  [Category("HappyPath")]
  public void Lru_GivenAccessTrace_WhenEvicting_ThenLeastRecentlyUsedGoesFirst() {
    var policy = _Create(EvictionPolicy.Lru);
    policy.OnInsert("a");
    policy.OnInsert("b");
    policy.OnInsert("c");
    policy.OnAccess("a"); // a is now most recent

    policy.SelectVictim().Should().Be("b");
    policy.SelectVictim().Should().Be("c");
    policy.SelectVictim().Should().Be("a");
  }

  [Test]
  [Category("HappyPath")]
  public void Mru_GivenAccessTrace_WhenEvicting_ThenMostRecentlyUsedGoesFirst() {
    var policy = _Create(EvictionPolicy.Mru);
    policy.OnInsert("a");
    policy.OnInsert("b");
    policy.OnAccess("a");

    policy.SelectVictim().Should().Be("a");
    policy.SelectVictim().Should().Be("b");
  }

  [Test]
  [Category("HappyPath")]
  public void Fifo_GivenAccessedFirstEntry_WhenEvicting_ThenRecencyIgnored() {
    var policy = _Create(EvictionPolicy.Fifo);
    policy.OnInsert("a");
    policy.OnInsert("b");
    policy.OnAccess("a"); // must not save a — FIFO ignores recency

    policy.SelectVictim().Should().Be("a", "FIFO evicts in admission order regardless of hits");
  }

  [Test]
  [Category("HappyPath")]
  public void Lfu_GivenFrequencyTrace_WhenEvicting_ThenLeastFrequentGoesFirstWithFifoTieBreak() {
    var policy = _Create(EvictionPolicy.Lfu);
    policy.OnInsert("hot");
    policy.OnInsert("warm");
    policy.OnInsert("cold");
    policy.OnAccess("hot");
    policy.OnAccess("hot");
    policy.OnAccess("warm");

    policy.SelectVictim().Should().Be("cold");
    policy.SelectVictim().Should().Be("warm");
    policy.SelectVictim().Should().Be("hot");
  }

  [Test]
  [Category("HappyPath")]
  public void Clock_GivenReferencedEntry_WhenEvicting_ThenSecondChanceGranted() {
    var policy = _Create(EvictionPolicy.Clock);
    policy.OnInsert("a");
    policy.OnInsert("b");
    policy.OnAccess("a"); // reference bit set

    policy.SelectVictim().Should().Be("b", "the sweep clears a's bit and evicts the unreferenced b");
    policy.SelectVictim().Should().Be("a");
  }

  [Test]
  [Category("HappyPath")]
  public void Random_GivenSameSeed_WhenEvicting_ThenDeterministicAndComplete() {
    var policy = _Create(EvictionPolicy.Random);
    foreach (var key in new[] { "a", "b", "c", "d" })
      policy.OnInsert(key);

    var victims = new List<string>();
    while (policy.SelectVictim() is { } victim)
      victims.Add(victim);

    victims.Should().BeEquivalentTo(["a", "b", "c", "d"], "every entry is eventually evicted exactly once");
  }

  [Test]
  [Category("HappyPath")]
  public void Slru_GivenPromotedEntry_WhenEvicting_ThenProbationGoesFirst() {
    var policy = _Create(EvictionPolicy.Slru, capacity: 8);
    policy.OnInsert("promoted");
    policy.OnInsert("probation1");
    policy.OnAccess("promoted"); // hit → protected segment
    policy.OnInsert("probation2");

    policy.SelectVictim().Should().Be("probation1");
    policy.SelectVictim().Should().Be("probation2");
    policy.SelectVictim().Should().Be("promoted", "protected entries outlive all probationary ones");
  }

  [Test]
  [Category("HappyPath")]
  public void TwoQueue_GivenReadmissionAfterEviction_WhenEvicting_ThenEntryLivesInMainQueue() {
    var policy = _Create(EvictionPolicy.TwoQueue, capacity: 4);
    policy.OnInsert("x");
    policy.SelectVictim().Should().Be("x", "first-time entries sit in the A1in FIFO");

    policy.OnInsert("x"); // ghost hit in A1out → admit to Am
    policy.OnInsert("scan1");
    policy.SelectVictim().Should().Be("scan1", "one-shot entries evict before the re-used Am resident");
  }

  /// <summary>The §11.1 hit-rate ordering claim: scan-resistant policies beat plain LRU under a scan.</summary>
  [TestCase(EvictionPolicy.Arc)]
  [TestCase(EvictionPolicy.Slru)]
  [TestCase(EvictionPolicy.TwoQueue)]
  [Category("HappyPath")]
  public void ScanResistance_GivenHotSetPlusScan_WhenReplayed_ThenPolicyBeatsLru(EvictionPolicy candidate) {
    var trace = _HotSetPlusScanTrace().ToArray();
    var candidateHits = _SimulateHits(_Create(candidate, 8), 8, trace);
    var lruHits = _SimulateHits(_Create(EvictionPolicy.Lru, 8), 8, trace);

    candidateHits.Should().BeGreaterThan(lruHits, "{0} must not let a one-shot scan flush the hot working set", candidate);
  }

  private static IEnumerable<string> _HotSetPlusScanTrace() {
    var random = new Random(42);
    var hot = new[] { "h0", "h1", "h2", "h3" };
    for (var round = 0; round < 60; ++round) {
      // touch the hot set a few times
      for (var i = 0; i < 6; ++i)
        yield return hot[random.Next(hot.Length)];

      // one-shot scan items that never repeat
      for (var i = 0; i < 6; ++i)
        yield return $"scan-{round}-{i}";
    }
  }

  private static int _SimulateHits(ICacheEvictionPolicy<string> policy, int capacity, IEnumerable<string> trace) {
    var resident = new HashSet<string>();
    var hits = 0;
    foreach (var key in trace) {
      if (resident.Contains(key)) {
        ++hits;
        policy.OnAccess(key);
        continue;
      }

      policy.OnInsert(key);
      resident.Add(key);
      while (resident.Count > capacity) {
        var victim = policy.SelectVictim();
        if (victim == null)
          break;

        resident.Remove(victim);
      }
    }

    return hits;
  }

  [TestCase(EvictionPolicy.Lru)]
  [TestCase(EvictionPolicy.Mru)]
  [TestCase(EvictionPolicy.Fifo)]
  [TestCase(EvictionPolicy.Lfu)]
  [TestCase(EvictionPolicy.Clock)]
  [TestCase(EvictionPolicy.ClockPro)]
  [TestCase(EvictionPolicy.Slru)]
  [TestCase(EvictionPolicy.TwoQueue)]
  [TestCase(EvictionPolicy.Arc)]
  [TestCase(EvictionPolicy.Random)]
  [Category("EdgeCase")]
  public void AnyPolicy_GivenChurn_WhenDrained_ThenCountConsistentAndEmptyReturnsNull(EvictionPolicy kind) {
    var policy = _Create(kind, 16);
    for (var i = 0; i < 40; ++i) {
      policy.OnInsert($"k{i}");
      if (i % 3 == 0)
        policy.OnAccess($"k{i / 2}");
      if (i % 5 == 0)
        policy.Remove($"k{i - 1}");
    }

    var drained = 0;
    while (policy.SelectVictim() != null)
      ++drained;

    drained.Should().Be(policy.Count + drained, "Count must be zero after draining");
    policy.Count.Should().Be(0);
    policy.SelectVictim().Should().BeNull("an empty policy has no victim");
  }

}
