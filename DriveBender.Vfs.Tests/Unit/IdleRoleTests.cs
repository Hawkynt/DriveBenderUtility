using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// An IDLE member is a disk joined to the pool to receive the files of a disk being retired, and for
/// nothing else: it never takes new files or their copies, the drainer and the healer never put
/// anything on it — and the moving service fills it FIRST. Every case gives the idle disk the most
/// free space, so anything that chose by free space alone would pick it.
/// </summary>
[TestFixture]
[Category("Unit")]
public class IdleRoleTests {

  private static readonly Guid _pool = Guid.Parse("1d1e0000-0000-0000-0000-000000000001");

  private FakeVolumeIO _ssd = null!;
  private FakeVolumeIO _hdd = null!;
  private FakeVolumeIO _idle = null!;
  private FakeVolumeIO _old = null!;

  [SetUp]
  public void SetUp() {
    this._ssd = new(Guid.NewGuid(), "ssd", "PHYS-SSD", capacity: 1L << 20);
    this._hdd = new(Guid.NewGuid(), "hdd", "PHYS-HDD", capacity: 1L << 22);
    this._idle = new(Guid.NewGuid(), "idle", "PHYS-IDLE", capacity: 1L << 26); // by far the emptiest
    this._old = new(Guid.NewGuid(), "old", "PHYS-OLD", capacity: 1L << 22);
  }

  private static MetadataCache _Metadata() => new(EvictionPolicy.Lru, 1000, TimeSpan.FromMinutes(1));

  private PlacementResolver _Resolver(bool withLanding = false) {
    IVolumeIO[] members = [this._ssd, this._hdd, this._idle];
    var roles = new Dictionary<Guid, MemberRole> {
      [this._ssd.MemberId] = withLanding ? MemberRole.Landing : MemberRole.Capacity,
      [this._hdd.MemberId] = MemberRole.Capacity,
      [this._idle.MemberId] = MemberRole.Idle,
    };
    return new(_pool, members, _Metadata(), ConfigResolver.ResolveEffective(null, null), roles);
  }

  #region placement: nothing automatic ever lands on an idle member

  [TestCase(false, TestName = "Placement_GivenAnIdleMemberWithTheMostFreeSpace_WhenANewFileIsPlaced_ThenItGoesToACapacityMember")]
  [TestCase(true, TestName = "Placement_GivenAnIdleMemberAndALandingZone_WhenANewFileIsPlaced_ThenItGoesToTheLandingZone")]
  [Category("HappyPath")]
  public void Placement_GivenAnIdleMember_WhenANewFileIsPlaced_ThenItNeverGoesThere(bool withLanding) {
    var target = this._Resolver(withLanding).ChoosePrimaryTarget(100);

    target.Should().NotBeNull();
    target!.MemberId.Should().NotBe(this._idle.MemberId, "an idle member never takes new files");
  }

  [Test]
  [Category("EdgeCase")]
  public void Placement_GivenOnlyTheIdleMemberHasRoom_WhenANewFileIsPlaced_ThenThereIsNoTargetRatherThanTheIdleOne() {
    // the last-resort branch ("better full than failing") must not reach for it either
    var target = this._Resolver().ChoosePrimaryTarget(1L << 24); // fits nowhere but the idle disk

    target.Should().BeNull("an idle member is not spare capacity for new files, even as a last resort");
  }

  [Test]
  [Category("HappyPath")]
  public void Placement_GivenAnIdleMember_WhenADuplicateCopyIsPlaced_ThenItNeverGoesThere() {
    var target = this._Resolver().ChooseShadowTarget(100, [this._hdd]);

    target.Should().NotBeNull();
    target!.MemberId.Should().Be(this._ssd.MemberId, "the only other non-idle member takes the copy");
  }

  [Test]
  [Category("EdgeCase")]
  public void Placement_GivenTheIdleMemberIsTheOnlyOtherDisk_WhenADuplicateCopyIsPlaced_ThenTheCopyStaysOwed() {
    // what the healer calls too: an idle disk is not where a missing copy is rebuilt
    var target = this._Resolver().ChooseShadowTarget(100, [this._hdd, this._ssd]);

    target.Should().BeNull("the copy stays owed rather than landing on the idle member");
  }

  [Test]
  [Category("HappyPath")]
  public void Placement_GivenAnIdleMember_WhenTheLandingZoneDrains_ThenNothingGoesThere() {
    var target = this._Resolver(withLanding: true).ChooseDrainTarget(100, [this._ssd]);

    target.Should().NotBeNull();
    target!.MemberId.Should().Be(this._hdd.MemberId, "a drain goes to capacity, never to an idle member");
  }

  [Test]
  [Category("HappyPath")]
  public void Engine_GivenAnIdleMember_WhenFilesAreWrittenAndHealed_ThenNoneOfThemEndUpOnIt() {
    // the whole engine, not just the resolver: create, duplicate, heal
    var cache = new CacheInstance("idle" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "512", MetadataEntries = 1000, MetadataTtl = "1m" });
    using var fs = new PoolFileSystem(_pool, [new(this._ssd), new(this._hdd), new(this._idle, MemberRole.Idle)], cache,
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2 }"""));
    fs.Mount(new(@"X:\"));

    for (var i = 0; i < 10; ++i) {
      var handle = fs.Create($"f{i}.bin", NodeKind.File, CreateFlags.None);
      fs.Write(handle, new byte[256], 0, WriteMode.Normal);
      fs.Close(handle);
    }

    fs.RequestHeal();
    fs.CreateScheduler().Quiesce();

    this._idle.FilePaths.Where(p => !p.Contains(".drivebenderutility")).Should().BeEmpty("nothing automatic puts files on an idle member");
  }

  #endregion

  #region the moving service: an idle member is where a retired disk's files go

  private MediaLifecycle _Lifecycle(int duplication, params (FakeVolumeIO member, MemberRole role)[] members)
    => new([.. members.Select(m => (IVolumeIO)m.member)], new(new MemberJournalStore([.. members.Select(m => m.member)])), duplication,
      roles: members.ToDictionary(m => m.member.MemberId, m => m.role));

  [Test]
  [Category("HappyPath")]
  public void Retire_GivenAnIdleMember_WhenADiskIsRetired_ThenItsFilesMoveOntoTheIdleMember() {
    this._old.Seed("a.bin", false, [1, 2, 3]);
    this._old.Seed("docs/b.bin", false, [4, 5]);
    this._hdd.Seed("filler.bin", false, new byte[16]); // the capacity disk is otherwise a fine target

    this._Lifecycle(1, (this._old, MemberRole.Capacity), (this._hdd, MemberRole.Capacity), (this._idle, MemberRole.Idle))
      .ScatterAndRemove(this._old.MemberId);

    this._idle.GetContent("a.bin", false).Should().Equal(new byte[] { 1, 2, 3 });
    this._idle.GetContent("docs/b.bin", false).Should().Equal(new byte[] { 4, 5 });
    this._hdd.FileExists("a.bin", false).Should().BeFalse("the idle member is filled first");
  }

  [Test]
  [Category("HappyPath")]
  public void Retire_GivenTheIdleMemberIsPreferredEvenWhenItHasLessRoom_ThenItStillTakesTheFiles() {
    // preference by ROLE, not by free space: a smaller idle disk still takes what fits
    var smallIdle = new FakeVolumeIO(Guid.NewGuid(), "small-idle", "PHYS-SMALL", capacity: 1L << 16);
    this._old.Seed("a.bin", false, [9, 9]);

    this._Lifecycle(1, (this._old, MemberRole.Capacity), (this._hdd, MemberRole.Capacity), (smallIdle, MemberRole.Idle))
      .ScatterAndRemove(this._old.MemberId);

    smallIdle.GetContent("a.bin", false).Should().Equal(new byte[] { 9, 9 });
  }

  [Test]
  [Category("EdgeCase")]
  public void Retire_GivenTheIdleMemberIsFull_WhenADiskIsRetired_ThenTheRestGoesToCapacity() {
    var tinyIdle = new FakeVolumeIO(Guid.NewGuid(), "tiny-idle", "PHYS-TINY", capacity: 4);
    this._old.Seed("big.bin", false, new byte[64]);

    this._Lifecycle(1, (this._old, MemberRole.Capacity), (this._hdd, MemberRole.Capacity), (tinyIdle, MemberRole.Idle))
      .ScatterAndRemove(this._old.MemberId);

    this._hdd.GetContent("big.bin", false).Should().HaveCount(64, "what does not fit on the idle member still has to go somewhere");
  }

  [Test]
  [Category("Exception")]
  public void Retire_GivenAReadOnlyMember_WhenADiskIsRetired_ThenNothingIsMovedOntoIt() {
    // the moving service used to choose by free space alone, so a read-only member — the emptiest
    // here — would have received the retired disk's files
    var readOnly = new FakeVolumeIO(Guid.NewGuid(), "ro", "PHYS-RO", capacity: 1L << 26);
    this._old.Seed("a.bin", false, [7]);

    this._Lifecycle(1, (this._old, MemberRole.Capacity), (this._hdd, MemberRole.Capacity), (readOnly, MemberRole.ReadOnly))
      .ScatterAndRemove(this._old.MemberId);

    readOnly.FileExists("a.bin", false).Should().BeFalse("a read-only member takes nothing");
    this._hdd.GetContent("a.bin", false).Should().Equal(new byte[] { 7 });
  }

  [Test]
  [Category("HappyPath")]
  public void Restore_GivenAnIdleMember_WhenDuplicationIsRestored_ThenNoCopyIsMadeOnIt() {
    // restoring duplication is the pool's own housekeeping, not the moving service
    this._hdd.Seed("f.bin", false, [3, 1, 4]);

    var report = this._Lifecycle(2, (this._hdd, MemberRole.Capacity), (this._ssd, MemberRole.Capacity), (this._idle, MemberRole.Idle))
      .RestorePool();

    report.CopiesCreated.Should().Be(1);
    this._ssd.GetContent("f.bin", true).Should().Equal(new byte[] { 3, 1, 4 });
    this._idle.FileExists("f.bin", true).Should().BeFalse();
  }

  #endregion

}
