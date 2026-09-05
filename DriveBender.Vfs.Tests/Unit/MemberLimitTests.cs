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
  public void Pace_GivenACopyBetweenTwoMembers_ThenBothEndsAreCharged() {
    // A copy is not something one disk does. Charging only the receiving member reads the limit as
    // "how fast may this be written to", when an operator sets it to mean "leave this disk some
    // room for everything else" — and the disk an exchange EMPTIES is under just as much load, and
    // is very often the limited one, because it is the tired member being evacuated.
    var source = new FakeVolumeIO(Guid.NewGuid(), "source", "PHYS-S", capacity: 1L << 30);
    var target = new FakeVolumeIO(Guid.NewGuid(), "target", "PHYS-T", capacity: 1L << 30);
    var charged = new List<(Guid member, long bytes)>();

    WholeFilePublisher.Pace((m, b) => charged.Add((m.MemberId, b)), source, target)!(4096);

    charged.Should().Equal([(source.MemberId, 4096L), (target.MemberId, 4096L)],
      "the chunk cost both disks the same work, so it must be charged to both");
  }

  [Test]
  [Category("EdgeCase")]
  public void Pace_GivenBothEndsAreTheSameMember_ThenItIsChargedOnce() {
    // promoting a shadow in place copies within one member; charging it twice would quietly halve
    // the rate the operator asked for, on the one operation where the accounting is easiest to get
    // wrong and hardest to notice
    var member = new FakeVolumeIO(Guid.NewGuid(), "m", "PHYS-M", capacity: 1L << 30);
    var charged = 0L;

    WholeFilePublisher.Pace((_, b) => charged += b, member, member)!(4096);

    charged.Should().Be(4096, "one disk did the work once");
  }

  [Test]
  [Category("HappyPath")]
  public void Pace_GivenTheSlowerEndIsTheSource_ThenTheCopyRunsAtItsRate() {
    // the point of charging both: a copy proceeds at the slower of the two allowances, so a limit
    // on either end is a real bound on the copy rather than a suggestion
    var source = new FakeVolumeIO(Guid.NewGuid(), "slow-source", "PHYS-S", capacity: 1L << 30);
    var target = new FakeVolumeIO(Guid.NewGuid(), "fast-target", "PHYS-T", capacity: 1L << 30);
    var queues = new VolumeQueues(ConfigResolver.ResolveEffective(null, null), new Dictionary<Guid, MemberRole>());
    queues.SetThrottles([
      (source.MemberId, new MemberLimits { BackgroundThroughput = 1 * _MIB }),
      (target.MemberId, MemberLimits.None),
    ]);

    var pace = WholeFilePublisher.Pace((m, b) => queues.Enter(m, IoKind.Background, b).Dispose(), source, target)!;
    pace(1 * _MIB); // spends the source's burst
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    pace(1 * _MIB);
    stopwatch.Stop();

    stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500),
      "an unlimited target cannot buy the copy out of the source's limit");
  }

  [Test]
  [Category("HappyPath")]
  public void Replace_GivenALimitedMember_ThenTheExchangeIsChargedForWhatItMoves() {
    // "a fixed rate for all operations, even heal and exchange" (§6.4). An exchange reads one disk
    // end to end and writes another flat out, for hours, while the pool stays in use — it is the
    // single operation an operator most wants held down, and it used to be entirely unmetered.
    var old = new FakeVolumeIO(Guid.NewGuid(), "old", "PHYS-O", capacity: 1L << 20);
    var replacement = new FakeVolumeIO(Guid.NewGuid(), "new", "PHYS-N", capacity: 1L << 20);
    old.Seed("docs/a.bin", false, new byte[4096]);
    old.Seed("docs/b.bin", false, new byte[2048]);

    var charged = new Dictionary<Guid, long>();
    var journal = new Journal(new MemberJournalStore([old, replacement]));
    new MediaLifecycle([old, replacement], journal, 1,
      admit: (m, b) => charged[m.MemberId] = charged.GetValueOrDefault(m.MemberId) + b)
      .Replace(old.MemberId, replacement);

    charged.Should().ContainKey(old.MemberId, "the disk being emptied did the reading")
      .And.ContainKey(replacement.MemberId, "and the one receiving did the writing");
    charged[old.MemberId].Should().Be(4096 + 2048, "every byte moved off it was accounted for");
    charged[replacement.MemberId].Should().Be(4096 + 2048);
  }

  [Test]
  [Category("Exception")]
  public void Quiesce_GivenBackgroundWorkThatNeverFinishes_ThenItGivesUpAtTheBudget() {
    // A clean unmount quiesces the scheduler, and housekeeping runs at whatever rate the operator
    // allowed it. Unbounded, throttling a member costs the ability to shut down cleanly: the
    // unmount request times out, the process is killed, and the next mount replays the journal.
    // Nothing acknowledged is at stake — durability is the filesystem's Unmount, which runs after
    // this regardless — so the right answer past the budget is to stop waiting.
    var scheduler = new BackgroundScheduler([new EndlessJob()]);
    var budget = TimeSpan.FromMilliseconds(500);

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var finished = scheduler.Quiesce(budget);
    stopwatch.Stop();

    finished.Should().BeFalse("work was still pending, and saying otherwise would hide it from the log");
    stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
      "a job that never runs out of work must not be able to hold the unmount open");
  }

  [Test]
  [Category("HappyPath")]
  public void Quiesce_GivenTheWorkFinishes_ThenItReportsSoWithoutSpendingTheBudget() {
    var scheduler = new BackgroundScheduler([new FiniteJob(3)]);

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var finished = scheduler.Quiesce(TimeSpan.FromSeconds(30));
    stopwatch.Stop();

    finished.Should().BeTrue("the jobs ran out of work, which is the normal case");
    stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "and it returns as soon as they do");
  }

  /// <summary>Housekeeping that always has more to do — a drain held to a crawl, in effect.</summary>
  private sealed class EndlessJob : IBackgroundJob {
    public string Name => "endless";
    public bool RunOnce() {
      Thread.Sleep(20);
      return true;
    }
  }

  private sealed class FiniteJob(int units) : IBackgroundJob {
    private int _left = units;
    public string Name => "finite";
    public bool RunOnce() => this._left-- > 0;
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
