using DivisonM.Vfs;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class PoolProviderTests {

  private static readonly Guid _poolId = Guid.Parse("b1f20000-0000-0000-0000-00000000000c");
  private static readonly Guid _member1 = Guid.Parse("a1000000-0000-0000-0000-000000000021");
  private static readonly Guid _member2 = Guid.Parse("a2000000-0000-0000-0000-000000000022");

  private FakeHostEnvironment _host = null!;
  private ManifestStore _store = null!;

  [SetUp]
  public void SetUp() {
    this._host = new();
    this._store = new(this._host);
  }

  private PoolProvider _Provider() => new(this._host, this._store, [new JsonManifestSource(this._store), new NativeScanSource(this._host)]);

  private PoolManifest _TwoMemberManifest(string path1 = @"A:\", string path2 = @"B:\test") => new() {
    PoolId = _poolId,
    Name = "MyPool",
    Members = [
      new() { MemberId = _member1, Path = path1, Label = "M1" },
      new() { MemberId = _member2, Path = path2, Label = "M2" },
    ],
  };

  private void _SetUpTwoHealthyMembers() {
    this._host.AddVolume(@"A:\", "PHYS-A", bytesFree: 100, bytesTotal: 200);
    this._host.AddVolume(@"B:\", "PHYS-B", bytesFree: 50, bytesTotal: 100);
    this._host.AddDirectory(@"B:\test");
    this._store.Save(this._TwoMemberManifest());
  }

  [Test]
  [Category("HappyPath")]
  public void Discover_GivenExplicitAndNativePools_WhenDiscovered_ThenUnionReturned() {
    this._SetUpTwoHealthyMembers();
    var nativePool = Guid.NewGuid();
    this._host.AddVolume(@"F:\", "PHYS-F");
    this._host.AddFile(@"F:\p." + DriveBender.DriveBenderConstants.INFO_EXTENSION, $"volumelabel:Native\nid:{nativePool}");
    this._host.AddDirectory($@"F:\{{{nativePool}}}");

    var pools = this._Provider().Discover();

    pools.Should().HaveCount(2);
    pools.Should().ContainSingle(p => p.PoolId == _poolId && !p.IsVirtual);
    pools.Should().ContainSingle(p => p.PoolId == nativePool && p.IsVirtual);
  }

  [Test]
  [Category("EdgeCase")]
  public void Discover_GivenAdoptedPoolVisibleToBothSources_WhenDiscovered_ThenExplicitManifestWins() {
    // a native pool that has been adopted: the registry AND the scan both yield poolId
    this._host.AddVolume(@"F:\", "PHYS-F");
    this._host.AddFile(@"F:\p." + DriveBender.DriveBenderConstants.INFO_EXTENSION, $"volumelabel:Native\nid:{_poolId}");
    this._host.AddDirectory($@"F:\{{{_poolId}}}");
    this._store.Save(new() {
      PoolId = _poolId,
      Name = "AdoptedPool",
      Members = [new() { MemberId = _member1, Path = $@"F:\{{{_poolId}}}" }],
    });

    var pools = this._Provider().Discover();

    pools.Should().ContainSingle();
    pools[0].IsVirtual.Should().BeFalse();
    pools[0].Name.Should().Be("AdoptedPool");
  }

  [Test]
  [Category("HappyPath")]
  public void Open_GivenAllMembersOnline_WhenOpened_ThenMountPointWithAllVolumesAndCleanHealth() {
    this._SetUpTwoHealthyMembers();
    var provider = this._Provider();
    var pool = provider.Discover().Single();

    var mountPoint = provider.Open(pool, out var health);

    mountPoint.Volumes.Should().HaveCount(2);
    mountPoint.Id.Should().Be(_poolId);
    health.OfflineMembers.Should().BeEmpty();
    health.IsDegraded.Should().BeFalse();
    health.IndependentFailureDomains.Should().Be(2);
  }

  [Test]
  [Category("EdgeCase")]
  public void Open_GivenOneOfflineMember_WhenOpened_ThenDegradedMountWithSurvivingMember() {
    this._SetUpTwoHealthyMembers();
    this._host.SetVolumeOnline(@"B:\", false);
    var provider = this._Provider();
    var pool = provider.Discover().Single();

    var mountPoint = provider.Open(pool, out var health);

    mountPoint.Volumes.Should().HaveCount(1, "the mount stays up on surviving members (SAFE-OFFLINE)");
    health.IsDegraded.Should().BeTrue();
    health.OfflineMembers.Should().ContainSingle(m => m.MemberId == _member2);
    health.Warnings.Should().Contain(w => w.Contains("offline"));
  }

  [Test]
  [Category("Exception")]
  public void Open_GivenNoResolvableMember_WhenOpened_ThenRefusedWithOfflineError() {
    this._store.Save(this._TwoMemberManifest());
    var provider = this._Provider();
    var pool = provider.Discover().Single();

    var act = () => provider.Open(pool, out _);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Offline);
  }

  [Test]
  [Category("EdgeCase")]
  public void Inspect_GivenTwoMembersOnOnePhysicalVolume_WhenInspected_ThenOneFailureDomainReported() {
    // two subfolder members on the same disk defeat duplication (SAFE-PHYS)
    this._host.AddVolume(@"C:\", "PHYS-C", bytesFree: 1000, bytesTotal: 2000);
    this._host.AddDirectory(@"C:\poolA");
    this._host.AddDirectory(@"C:\poolB");
    this._store.Save(this._TwoMemberManifest(@"C:\poolA", @"C:\poolB"));
    var provider = this._Provider();

    var health = provider.Inspect(provider.Discover().Single());

    health.SharedFailureDomains.Should().ContainSingle().Which.Should().HaveCount(2);
    health.IndependentFailureDomains.Should().Be(1);
    health.IsDegraded.Should().BeTrue();
    health.Warnings.Should().Contain(w => w.Contains("physical volume"));
  }

  [Test]
  [Category("HappyPath")]
  public void Inspect_GivenSharedVolumeAndReserve_WhenInspected_ThenFreeSpaceDeduplicatedAndReserveSubtracted() {
    this._host.AddVolume(@"C:\", "PHYS-C", bytesFree: 1000, bytesTotal: 2000);
    this._host.AddDirectory(@"C:\poolA");
    this._host.AddDirectory(@"C:\poolB");
    var manifest = new PoolManifest {
      PoolId = _poolId,
      Name = "MyPool",
      Members = [
        new() { MemberId = _member1, Path = @"C:\poolA", ReserveBytes = 300 },
        new() { MemberId = _member2, Path = @"C:\poolB", ReserveBytes = 200 },
      ],
    };
    this._store.Save(manifest);
    var provider = this._Provider();

    var health = provider.Inspect(provider.Discover().Single());

    health.BytesTotal.Should().Be(2000, "one physical volume is counted once (FR-SPACE-SHARED)");
    health.BytesFree.Should().Be(1000 - 300 - 200, "reserveBytes reduce usable space");
  }

  [Test]
  [Category("HappyPath")]
  public void Open_GivenMemberMovedToNewLetter_WhenOpened_ThenManifestUpdatedWithResolvedPath() {
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._host.AddVolume(@"E:\", "PHYS-E");
    this._host.AddDirectory(@"B:\test");
    var saved = this._store.Save(this._TwoMemberManifest());
    // member 2 moved from B:\test to E:\ — the marker travels with the data, the old location loses it
    this._host.AddFile(ManifestStore.MarkerPathFor(@"E:\"), ManifestSerializer.WriteMarker(new() { PoolId = _poolId, MemberId = _member2 }));
    this._host.RemoveFile(ManifestStore.MarkerPathFor(@"B:\test"));
    this._host.RemoveFile(ManifestStore.MirrorPathFor(@"B:\test"));
    var provider = this._Provider();

    provider.Open(provider.Discover().Single(), out var health);

    health.Members.Single(m => m.MemberId == _member2).ResolvedPath.Should().Be(@"E:\");
    var persisted = this._store.TryLoadRegistry(_poolId)!;
    persisted.FindMember(_member2)!.Path.Should().Be(@"E:\", "newly resolved paths are written back (FR-RESOLVE-MEMBER)");
    persisted.Version.Should().BeGreaterThan(saved.Version);
  }

  [Test]
  [Category("EdgeCase")]
  public void Inspect_GivenNetworkMember_WhenInspected_ThenDurabilityWarningRaised() {
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._host.AddVolume(@"\\server\share", "UNC-SERVER-SHARE");
    this._host.AddDirectory(@"\\server\share\pool");
    var manifest = new PoolManifest {
      PoolId = _poolId,
      Name = "MyPool",
      Members = [
        new() { MemberId = _member1, Path = @"A:\" },
        new() { MemberId = _member2, Path = @"\\server\share\pool", Network = true },
      ],
    };
    this._store.Save(manifest);
    var provider = this._Provider();

    var health = provider.Inspect(provider.Discover().Single());

    health.Warnings.Should().Contain(w => w.Contains("SAFE-NET-DURABILITY"), "unverified network durability must be surfaced");
  }

}
