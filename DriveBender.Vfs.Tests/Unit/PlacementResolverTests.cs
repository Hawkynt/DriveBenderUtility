using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class PlacementResolverTests {

  private static readonly Guid _pool = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

  private FakeVolumeIO _ssd = null!;
  private FakeVolumeIO _hdd1 = null!;
  private FakeVolumeIO _hdd2 = null!;
  private MetadataCache _metadata = null!;

  [SetUp]
  public void SetUp() {
    this._ssd = new(Guid.NewGuid(), "ssd", "PHYS-SSD", capacity: 1000);
    this._hdd1 = new(Guid.NewGuid(), "hdd1", "PHYS-HDD1", capacity: 10_000);
    this._hdd2 = new(Guid.NewGuid(), "hdd2", "PHYS-HDD2", capacity: 20_000);
    this._metadata = new(EvictionPolicy.Lru, 1000, TimeSpan.FromMinutes(1));
  }

  private PlacementResolver _Resolver(PoolConfig? config = null, bool ssdIsLanding = true) {
    var members = new IVolumeIO[] { this._ssd, this._hdd1, this._hdd2 };
    var roles = new Dictionary<Guid, MemberRole> {
      [this._ssd.MemberId] = ssdIsLanding ? MemberRole.Landing : MemberRole.Capacity,
      [this._hdd1.MemberId] = MemberRole.Capacity,
      [this._hdd2.MemberId] = MemberRole.Capacity,
    };
    return new(_pool, members, this._metadata, config ?? ConfigResolver.ResolveEffective(null, null), roles);
  }

  [Test]
  [Category("HappyPath")]
  public void ResolveCopies_GivenPrimaryAndShadow_WhenResolved_ThenPrimariesFirst() {
    this._hdd1.Seed("docs/f.txt", false, [1]);
    this._hdd2.Seed("docs/f.txt", true, [1]);

    var copies = this._Resolver().ResolveCopies("docs/f.txt");

    copies.Should().HaveCount(2);
    copies[0].Shadow.Should().BeFalse();
    copies[0].Volume.Should().BeSameAs(this._hdd1);
    copies[1].Shadow.Should().BeTrue();
    copies[1].Volume.Should().BeSameAs(this._hdd2);
  }

  [Test]
  [Category("EdgeCase")]
  public void ResolveCopies_GivenOfflinePrimaryHolder_WhenResolved_ThenSurvivingShadowOnly() {
    this._hdd1.Seed("f.txt", false, [1]);
    this._hdd2.Seed("f.txt", true, [1]);
    this._hdd1.IsOnline = false;

    var copies = this._Resolver().ResolveCopies("f.txt");

    copies.Should().ContainSingle().Which.Volume.Should().BeSameAs(this._hdd2, "reads are served from surviving copies (SAFE-OFFLINE)");
  }

  [Test]
  [Category("HappyPath")]
  public void ChoosePrimaryTarget_GivenLandingZoneWithRoom_WhenPlacing_ThenFastTierWins() {
    var target = this._Resolver().ChoosePrimaryTarget(100);
    target.Should().BeSameAs(this._ssd, "writes land on the fastest eligible tier first (FR-TIER)");
  }

  [Test]
  [Category("EdgeCase")]
  public void ChoosePrimaryTarget_GivenLandingZoneAboveLowWatermark_WhenPlacing_ThenSpillsToCapacity() {
    // fill the SSD beyond its 75% low watermark
    this._ssd.Seed("filler.bin", false, new byte[800]);

    var target = this._Resolver().ChoosePrimaryTarget(100);

    target.Should().BeSameAs(this._hdd2, "ingest stops using a tier below its low watermark and spills down (§6.7); hdd2 has most free space");
  }

  [Test]
  [Category("HappyPath")]
  public void ChoosePrimaryTarget_GivenNoLandingZone_WhenPlacingByMostFree_ThenLargestFreeCapacityWins() {
    var target = this._Resolver(ssdIsLanding: false).ChoosePrimaryTarget(100);
    target.Should().BeSameAs(this._hdd2);
  }

  [Test]
  [Category("HappyPath")]
  public void ChoosePrimaryTarget_GivenRoundRobinStrategy_WhenPlacingTwice_ThenDifferentMembers() {
    var config = ConfigResolver.ResolveEffective(null, """{ "placement": { "strategy": "round-robin" }, "tiers": { "fast": { "members": [] } } }""");
    var resolver = this._Resolver(config, ssdIsLanding: false);

    var first = resolver.ChoosePrimaryTarget(10);
    var second = resolver.ChoosePrimaryTarget(10);

    second.Should().NotBeSameAs(first, "round-robin rotates the target");
  }

  [Test]
  [Category("Exception")]
  public void ChoosePrimaryTarget_GivenNothingFits_WhenPlacing_ThenNull() {
    this._Resolver().ChoosePrimaryTarget(1_000_000).Should().BeNull("no volume fits the file — the caller reports NoSpace (FR-BIGFILE)");
  }

  [Test]
  [Category("HappyPath")]
  public void ChooseShadowTarget_GivenPrimaryHolder_WhenPlacingShadow_ThenDifferentFailureDomain() {
    var target = this._Resolver().ChooseShadowTarget(100, [this._hdd2]);
    target.Should().NotBeNull();
    target!.PhysicalVolumeId.Should().NotBe(this._hdd2.PhysicalVolumeId, "copies never share a failure domain (SAFE-PHYS)");
  }

  [Test]
  [Category("EdgeCase")]
  public void ChooseShadowTarget_GivenAllDomainsOccupied_WhenPlacingShadow_ThenNullRatherThanCoLocation() {
    var resolver = this._Resolver();
    var target = resolver.ChooseShadowTarget(100, [this._ssd, this._hdd1, this._hdd2]);
    target.Should().BeNull("better to defer duplication than co-locate copies in one failure domain (SAFE-PHYS)");
  }

  [Test]
  [Category("EdgeCase")]
  public void ChooseShadowTarget_GivenSubfolderMembersOnOneDisk_WhenPlacingShadow_ThenTreatedAsOneDomain() {
    var sameDisk = new FakeVolumeIO(Guid.NewGuid(), "same-disk-other-folder", "PHYS-HDD1", capacity: 50_000);
    var members = new IVolumeIO[] { this._hdd1, sameDisk };
    var resolver = new PlacementResolver(_pool, members, this._metadata, ConfigResolver.ResolveEffective(null, null));

    resolver.ChooseShadowTarget(100, [this._hdd1]).Should().BeNull(
      "two members on the same physical volume are one failure domain — 2 copies there survive a disk loss as 0 (SAFE-PHYS)");
  }

  [Test]
  [Category("EdgeCase")]
  public void ChooseShadowTarget_GivenSameDiskAllowedAndNoIndependentDomain_WhenPlacing_ThenCoLocatesForBitRotProtection() {
    var sameDisk = new FakeVolumeIO(Guid.NewGuid(), "same-disk-other-folder", "PHYS-HDD1", capacity: 50_000);
    var members = new IVolumeIO[] { this._hdd1, sameDisk };
    var config = ConfigResolver.ResolveEffective(null, """{"placement":{"shadowNeverSamePhysical":false}}""");
    var resolver = new PlacementResolver(_pool, members, this._metadata, config);

    resolver.ChooseShadowTarget(100, [this._hdd1]).Should().Be(sameDisk,
      "opting out of shadowNeverSamePhysical lets a second copy land on another member of the same disk (bit-rot protection, not disk-loss protection)");
  }

  [Test]
  [Category("HappyPath")]
  public void ChoosePrimaryTarget_GivenLowestLatencyStrategy_WhenPlacing_ThenMeasuredFastestWins() {
    var slow = new MeasuredVolumeIO(this._hdd1);
    var fast = new MeasuredVolumeIO(this._hdd2);
    slow.RecordLatency(25);
    fast.RecordLatency(1.5);
    var config = ConfigResolver.ResolveEffective(null, """{"placement":{"strategy":"lowest-latency"}}""");
    var resolver = new PlacementResolver(_pool, [slow, fast], this._metadata, config);

    resolver.ChoosePrimaryTarget(100).Should().Be(fast,
      "lowest-latency places new primaries on the member that currently measures fastest");
  }

  [Test]
  [Category("HappyPath")]
  public void ChoosePrimaryTarget_GivenRoundRobinStrategy_WhenPlacingTwice_ThenTargetsAlternate() {
    var config = ConfigResolver.ResolveEffective(null, """{"placement":{"strategy":"round-robin"}}""");
    var resolver = new PlacementResolver(_pool, [this._hdd1, this._hdd2], this._metadata, config);

    var first = resolver.ChoosePrimaryTarget(100);
    var second = resolver.ChoosePrimaryTarget(100);

    second.Should().NotBe(first, "round-robin spreads consecutive new files across members for parallel throughput");
  }

  [Test]
  [Category("HappyPath")]
  public void UpdateRoles_GivenCapacityPromotedToLanding_WhenPlacingPrimary_ThenNewLandingPreferred() {
    var resolver = this._Resolver(ssdIsLanding: false); // everything starts as capacity

    resolver.UpdateRoles(new Dictionary<Guid, MemberRole> {
      [this._ssd.MemberId] = MemberRole.Landing,
      [this._hdd1.MemberId] = MemberRole.Capacity,
      [this._hdd2.MemberId] = MemberRole.Capacity,
    });

    resolver.ChoosePrimaryTarget(100).Should().Be(this._ssd,
      "the live role change makes the SSD the landing tier, which takes new writes first");
  }

  [Test]
  [Category("HappyPath")]
  public void ChooseShadowTarget_GivenSameDiskAllowedButIndependentDomainFree_WhenPlacing_ThenStillPrefersIndependentDomain() {
    var config = ConfigResolver.ResolveEffective(null, """{"placement":{"shadowNeverSamePhysical":false}}""");

    var target = this._Resolver(config).ChooseShadowTarget(100, [this._hdd1]);

    target!.PhysicalVolumeId.Should().NotBe(this._hdd1.PhysicalVolumeId,
      "an independent failure domain is still preferred — same-disk is only a last resort");
  }

  [Test]
  [Category("HappyPath")]
  public void ResolveCopies_GivenSecondCall_WhenCached_ThenServedFromMetadataCache() {
    this._hdd1.Seed("f.txt", false, [1]);
    var resolver = this._Resolver();

    var first = resolver.ResolveCopies("f.txt");
    this._hdd1.Delete("f.txt", false); // mutate behind the resolver
    var second = resolver.ResolveCopies("f.txt");

    second.Should().BeEquivalentTo(first, "placement is cached until invalidated");

    resolver.Invalidate("f.txt");
    resolver.ResolveCopies("f.txt").Should().BeEmpty("invalidation drops the stale entry");
  }

}
