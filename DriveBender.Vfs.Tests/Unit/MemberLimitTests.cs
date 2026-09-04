using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Per-kind rate limits (§6.4): what a member may be asked to do, separately for reads, writes and
/// the pool's own background copying.
///
/// The three are separated because they compete for a disk on different terms. A read is what an
/// application is blocked on; a write is what it is blocked on to be safe; background work is a
/// drain, a heal or an exchange, which moves far more data than either and which nobody is waiting
/// for. Holding the last one down while leaving the first two alone is most of what "leave some of
/// this disk for everything else" means.
/// </summary>
[TestFixture]
[Category("Unit")]
public class MemberLimitTests {

  private const long _MIB = 1024 * 1024;

  private static VolumeQueues _Queues(MemberLimits limits, out FakeVolumeIO member) {
    member = new(Guid.NewGuid(), "m", "PHYS-M", capacity: 1L << 30);
    var queues = new VolumeQueues(ConfigResolver.ResolveEffective(null, null), new Dictionary<Guid, MemberRole>());
    queues.SetThrottles([(member.MemberId, limits)]);
    return queues;
  }

  /// <summary>Wall-clock cost of spending a whole second of one kind's credit twice over.</summary>
  private static TimeSpan _CostOf(VolumeQueues queues, IVolumeIO member, IoKind kind, long rate) {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    queues.Enter(member, kind, rate).Dispose(); // spends the bucket's burst
    queues.Enter(member, kind, rate).Dispose(); // and this one has to wait for a refill
    stopwatch.Stop();
    return stopwatch.Elapsed;
  }

  [Test]
  [Category("HappyPath")]
  public void Limits_GivenOnlyBackgroundIsLimited_ThenReadsAndWritesAreUntouched() {
    // the shape an operator actually wants: the pool may heal as slowly as it likes, but must not
    // make the application wait for the privilege
    var queues = _Queues(new() { BackgroundThroughput = 1 * _MIB }, out var member);

    _CostOf(queues, member, IoKind.Read, 8 * _MIB).Should().BeLessThan(TimeSpan.FromMilliseconds(200),
      "nothing limits reads, so they must not pay for the background limit");
    _CostOf(queues, member, IoKind.Write, 8 * _MIB).Should().BeLessThan(TimeSpan.FromMilliseconds(200),
      "nor writes");
    _CostOf(queues, member, IoKind.Background, 1 * _MIB).Should().BeGreaterThan(TimeSpan.FromMilliseconds(500),
      "while background work is held to its rate");
  }

  [Test]
  [Category("EdgeCase")]
  public void Limits_GivenOneKindSpendsItsCredit_ThenTheOthersStillHaveTheirs() {
    // a shared bucket would make three rates meaningless the moment more than one was set: a heal
    // at its own generous rate would spend the credit a read was about to need
    var queues = _Queues(new() { ReadThroughput = 4 * _MIB, BackgroundThroughput = 4 * _MIB }, out var member);

    queues.Enter(member, IoKind.Background, 4 * _MIB).Dispose(); // spend background's whole second
    var read = System.Diagnostics.Stopwatch.StartNew();
    queues.Enter(member, IoKind.Read, 4 * _MIB).Dispose();
    read.Stop();

    read.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200),
      "the read's own credit was never spent, so it must not queue behind the background copy's");
  }

  [Test]
  [Category("HappyPath")]
  public void Limits_GivenOnlyTheSimpleRate_ThenEveryKindIsHeldToIt() {
    // the shape every existing manifest carries: one number, and it covers the pool's own copying
    // too — which is the case it most needs to cover
    var queues = _Queues(MemberLimits.Simple(0, 2 * _MIB), out var member);

    foreach (var kind in new[] { IoKind.Read, IoKind.Write, IoKind.Background })
      _CostOf(queues, member, kind, 2 * _MIB).Should().BeGreaterThan(TimeSpan.FromMilliseconds(500),
        $"a single rate covers {kind} as well");
  }

  [Test]
  [Category("Exception")]
  public void Limits_GivenADelayCap_ThenTheLimiterNeverWaitsLongerThanIt() {
    // A limit is a target; the cap is the promise that honouring it costs no more than this. Without
    // it a rate set far too low would be indistinguishable from a wedged pool, and an operator who
    // mistypes a number would have no way back short of unmounting.
    var queues = _Queues(new() { MaxThroughput = 1 * _MIB, TimeoutMs = 250 }, out var member);

    queues.Enter(member, IoKind.Write, 1 * _MIB).Dispose(); // spend the burst
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    queues.Enter(member, IoKind.Write, 16 * _MIB).Dispose(); // would otherwise be a sixteen-second wait
    stopwatch.Stop();

    stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
      "the limiter must give up waiting at the cap and let the operation through");
  }

  [Test]
  [Category("HappyPath")]
  public void Limits_GivenNoLimitsAtAll_ThenNothingIsDelayed() {
    var queues = _Queues(MemberLimits.None, out var member);

    _CostOf(queues, member, IoKind.Background, 64 * _MIB).Should().BeLessThan(TimeSpan.FromMilliseconds(200),
      "an unlimited member is the default and must cost nothing");
  }

  [Test]
  [Category("EdgeCase")]
  public void EffectiveLimits_GivenOnlyTheLegacyPair_ThenItReadsAsASimpleRate() {
    // older manifests carry maxIops/maxThroughput and no limits block; they must keep meaning
    // exactly what they always did
    var definition = new PoolMemberDefinition { MemberId = Guid.NewGuid(), Path = "/m", MaxThroughput = 5 * _MIB };

    definition.EffectiveLimits.ThroughputFor(IoKind.Read).Should().Be(5 * _MIB);
    definition.EffectiveLimits.ThroughputFor(IoKind.Background).Should().Be(5 * _MIB);
    definition.EffectiveLimits.DelayCapFor(IoKind.Write).Should().BeNull("no timeout was asked for");
  }

}
