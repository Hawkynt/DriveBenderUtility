using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Auto landing-zone: latency measurement and the re-tiering advisor (FR-AUTO-TIER).</summary>
[TestFixture]
[Category("Unit")]
public class AutoTierTests {

  private static MemberSpeed _Speed(string name, double ms, MemberRole role = MemberRole.Capacity, long samples = 100, bool network = false)
    => new(Guid.NewGuid(), name, ms, samples, network, role);

  [Test]
  [Category("HappyPath")]
  public void Advise_GivenNoLandingAndOneClearlyFastMember_WhenAdvised_ThenPromotesIt() {
    var ssd = _Speed("ssd", 1.0);
    var advice = new AutoTierAdvisor().Advise([ssd, _Speed("hdd1", 9.0), _Speed("hdd2", 11.0)]);

    advice.Should().NotBeNull("a decisively faster member becomes the landing zone (auto-detect)");
    advice!.PromoteToLanding.Should().Be(ssd.MemberId);
    advice.DemoteToCapacity.Should().BeNull();
  }

  [Test]
  [Category("EdgeCase")]
  public void Advise_GivenSimilarSpeeds_WhenAdvised_ThenNoChange() {
    var advice = new AutoTierAdvisor().Advise([_Speed("a", 5.0), _Speed("b", 6.0), _Speed("c", 7.0)]);

    advice.Should().BeNull("no member is decisively faster — flapping between near-equals is worse than no landing zone");
  }

  [Test]
  [Category("HappyPath")]
  public void Advise_GivenLandingTurnedSlow_WhenAdvised_ThenSwapsToTheFastMember() {
    var slowLz = _Speed("busy-ssd", 20.0, MemberRole.Landing);
    var fast = _Speed("idle-hdd", 4.0);

    var advice = new AutoTierAdvisor().Advise([slowLz, fast]);

    advice.Should().NotBeNull("a slow/busy landing zone gets swapped for the measured-fastest member");
    advice!.PromoteToLanding.Should().Be(fast.MemberId);
    advice.DemoteToCapacity.Should().Be(slowLz.MemberId);
  }

  [Test]
  [Category("EdgeCase")]
  public void Advise_GivenLandingOnlySlightlySlower_WhenAdvised_ThenNoFlapping() {
    var lz = _Speed("ssd", 2.0, MemberRole.Landing);

    new AutoTierAdvisor().Advise([lz, _Speed("hdd", 1.5)]).Should().BeNull(
      "hysteresis: re-tiering needs a decisive speed advantage, not a marginal one");
  }

  [Test]
  [Category("EdgeCase")]
  public void Advise_GivenRecentChange_WhenAdvisedAgain_ThenCooldownBlocksIt() {
    var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var advisor = new AutoTierAdvisor(cooldown: TimeSpan.FromMinutes(10), clock: () => now);
    var first = advisor.Advise([_Speed("ssd", 1.0), _Speed("hdd", 9.0)]);
    first.Should().NotBeNull();

    now = now.AddMinutes(5);
    advisor.Advise([_Speed("nvme", 0.2, MemberRole.Capacity), _Speed("ssd", 1.0, MemberRole.Landing)])
      .Should().BeNull("inside the cooldown window no further re-tiering happens");

    now = now.AddMinutes(6);
    advisor.Advise([_Speed("nvme", 0.2, MemberRole.Capacity), _Speed("ssd", 1.0, MemberRole.Landing)])
      .Should().NotBeNull("after the cooldown the advisor acts again");
  }

  [Test]
  [Category("EdgeCase")]
  public void Advise_GivenRemoteOrUnsampledMembers_WhenAdvised_ThenTheyAreNeverLandingCandidates() {
    new AutoTierAdvisor().Advise([_Speed("cloud", 0.5, network: true), _Speed("hdd", 9.0)])
      .Should().BeNull("a remote member is no landing candidate no matter how fast it measures");

    new AutoTierAdvisor().Advise([_Speed("ssd", 0.5, samples: 3), _Speed("hdd", 9.0)])
      .Should().BeNull("too few samples — no decision on noise");
  }

  [Test]
  [Category("HappyPath")]
  public void MeasuredVolumeIO_GivenStreamTraffic_WhenObserved_ThenLatencyEwmaFills() {
    var fake = new FakeVolumeIO(Guid.NewGuid(), "v", "PHYS-1");
    var measured = new MeasuredVolumeIO(fake);

    using (var stream = measured.OpenWrite("f.bin", false, true)) {
      stream.Write([1, 2, 3], 0, 3);
      stream.Flush();
    }

    measured.Samples.Should().BeGreaterThan(0, "every storage-touching operation feeds the EWMA");
    measured.AverageLatencyMs.Should().BeGreaterThanOrEqualTo(0);
    fake.FileExists("f.bin", false).Should().BeTrue("the decorator is transparent");
  }

}
