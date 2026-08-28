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

  private PlacementResolver _Resolver(PoolConfig? config = null, bool ssdIsLanding = true,
    IReadOnlyDictionary<Guid, long>? reserves = null, Func<IVolumeIO, double>? loadOf = null) {
    var members = new IVolumeIO[] { this._ssd, this._hdd1, this._hdd2 };
    var roles = new Dictionary<Guid, MemberRole> {
      [this._ssd.MemberId] = ssdIsLanding ? MemberRole.Landing : MemberRole.Capacity,
      [this._hdd1.MemberId] = MemberRole.Capacity,
      [this._hdd2.MemberId] = MemberRole.Capacity,
    };
    return new(_pool, members, this._metadata, config ?? ConfigResolver.ResolveEffective(null, null), roles, reserves, loadOf);
  }

  [Test]
  [Category("EdgeCase")]
  public void Placement_GivenAStorageIsAlreadyBusy_ThenTheNextFileGoesElsewhere() {
    // Picking purely by free space sends CONSECUTIVE new files to the same member, because writing
    // one barely moves its free space. A burst of small files then queues on one device while the
    // others sit idle, and the pool delivers ONE disk's IOPS however many disks it has.
    var busy = new Dictionary<Guid, double> {
      [this._hdd2.MemberId] = 8, // most free space, but eight operations already in flight
    };

    var target = this._Resolver(ssdIsLanding: false, loadOf: v => busy.GetValueOrDefault(v.MemberId))
      .ChoosePrimaryTarget(100);

    target!.MemberId.Should().NotBe(this._hdd2.MemberId,
      "the roomiest member is the busiest one, and queueing behind eight operations costs more than "
      + "the space it would save");
  }

  [Test]
  [Category("HappyPath")]
  public void Placement_GivenEverythingIsIdle_ThenFreeSpaceStillDecides() {
    // the balancing property must survive: with nothing in flight the load term ties for every
    // candidate and capacity is what is left to choose on, exactly as before
    var target = this._Resolver(ssdIsLanding: false, loadOf: _ => 0)
      .ChoosePrimaryTarget(100);

    target!.MemberId.Should().Be(this._hdd2.MemberId,
      "an idle pool must still fill by free space, or load-awareness would quietly break balancing");
  }

  [Test]
  [Category("EdgeCase")]
  public void Placement_GivenConsecutiveFilesAndRisingLoad_ThenTheySpreadAcrossStorages() {
    // the shape that matters: each placement makes its target busier, so the next file should land
    // somewhere else rather than piling onto one spindle
    var inFlight = new Dictionary<Guid, double>();
    var resolver = this._Resolver(ssdIsLanding: false, loadOf: v => inFlight.GetValueOrDefault(v.MemberId));

    var chosen = new List<Guid>();
    for (var file = 0; file < 6; ++file) {
      var target = resolver.ChoosePrimaryTarget(100)!;
      chosen.Add(target.MemberId);
      inFlight[target.MemberId] = inFlight.GetValueOrDefault(target.MemberId) + 1; // it is now writing
    }

    chosen.Distinct().Should().HaveCountGreaterThan(1,
      $"six consecutive files all went to one storage, so the other spindles stayed idle and the "
      + $"pool delivered one disk's IOPS: {string.Join(", ", chosen.Select(id => id.ToString()[..4]))}");
  }

  [Test]
  [Category("EdgeCase")]
  public void Placement_GivenAMemberIsReservedToTheBrim_ThenNothingIsPlacedOnIt() {
    // A reserve is a promise to leave room - for the host filesystem, for another tenant, for the
    // headroom a nearly-full disk needs. Placement looked only at raw free space, so the pool would
    // fill a member straight through its reserve and stop only when the DEVICE refused, which is
    // the one moment there is no room left to fail gracefully in.
    var reserves = new Dictionary<Guid, long> {
      [this._hdd1.MemberId] = 10_000, // its entire capacity
      [this._hdd2.MemberId] = 20_000,
    };

    var target = this._Resolver(ssdIsLanding: false, reserves: reserves).ChoosePrimaryTarget(500);

    target.Should().NotBeNull("the SSD has no reserve and can still take the file");
    target!.MemberId.Should().Be(this._ssd.MemberId,
      "a member reserved to its capacity has no space to lend the pool, whatever the device reports free");
  }

  [Test]
  [Category("Exception")]
  public void Placement_GivenEveryMemberIsReservedToTheBrim_ThenThereIsNowhereToPlace() {
    var reserves = new Dictionary<Guid, long> {
      [this._ssd.MemberId] = 1_000,
      [this._hdd1.MemberId] = 10_000,
      [this._hdd2.MemberId] = 20_000,
    };

    // the caller turns this into NoSpace; what matters here is that placement REFUSES rather than
    // handing back a member it is about to overfill
    this._Resolver(ssdIsLanding: false, reserves: reserves).ChoosePrimaryTarget(500)
      .Should().BeNull("with every member reserved there is genuinely nowhere the file may go");
  }

  [Test]
  [Category("EdgeCase")]
  public void Placement_GivenAReserveLeavesRoom_ThenTheFileStillFits() {
    // the other side of the same rule: a reserve must not make a member unusable, only bounded
    var reserves = new Dictionary<Guid, long> { [this._hdd2.MemberId] = 19_000 };

    this._Resolver(ssdIsLanding: false, reserves: reserves).ChoosePrimaryTarget(500)
      .Should().NotBeNull("500 bytes fit in the 1,000 the reserve leaves");
  }


  [Test]
  [Category("EdgeCase")]
  public void Throttle_GivenAMemberIsRateLimited_ThenItsOperationsAreHeldToTheLimit() {
    // The feature in its own right: a pool is rarely the only user of a disk, so a mechanical drive
    // shared with something else - or a cloud endpoint with a rate limit and a bill - can be told
    // to take only its share.
    var queues = new VolumeQueues(ConfigResolver.ResolveEffective(null, null), new Dictionary<Guid, MemberRole>());
    queues.SetThrottles([(this._hdd1.MemberId, 20, 0)]); // 20 operations per second

    // the bucket starts full, so drain the first second of credit before timing anything
    for (var i = 0; i < 20; ++i)
      queues.Enter(this._hdd1).Dispose();

    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 10; ++i)
      queues.Enter(this._hdd1).Dispose();

    clock.Stop();
    clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(250),
      $"ten operations against a 20/s limit cannot finish in {clock.ElapsedMilliseconds} ms — the "
      + "limit is not being applied at all");
  }

  [Test]
  [Category("HappyPath")]
  public void Throttle_GivenNoLimitIsSet_ThenNothingIsSlowedDown() {
    var queues = new VolumeQueues(ConfigResolver.ResolveEffective(null, null), new Dictionary<Guid, MemberRole>());
    queues.SetThrottles([(this._hdd1.MemberId, 5, 0)]); // a limit on a DIFFERENT member

    var clock = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 200; ++i)
      queues.Enter(this._hdd2).Dispose();

    clock.Stop();
    clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
      "an unthrottled member must not pay for another member's limit");
  }

  [Test]
  [Category("EdgeCase")]
  public void Throttle_GivenAThrottledMemberIsBusy_ThenNewFilesPreferTheOtherStorage() {
    // Why the two features belong together, and the thing a single-disk machine cannot otherwise
    // demonstrate: a throttled storage stays busy longer, so load-aware placement routes new files
    // away from it. Throttling one member is how a slow device can be SIMULATED on hardware where
    // every member is the same NVMe.
    var inFlight = new Dictionary<Guid, double> { [this._hdd1.MemberId] = 4 }; // still working through its allowance
    var resolver = this._Resolver(ssdIsLanding: false, loadOf: v => inFlight.GetValueOrDefault(v.MemberId));

    var chosen = new List<Guid>();
    for (var file = 0; file < 4; ++file)
      chosen.Add(resolver.ChoosePrimaryTarget(100)!.MemberId);

    chosen.Should().NotContain(this._hdd1.MemberId,
      "a storage that is already backed up behind its own rate limit is the last place to send more work");
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
