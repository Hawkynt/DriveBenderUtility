using DivisonM.Vfs;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class ManifestStoreTests {

  private FakeHostEnvironment _host = null!;
  private ManifestStore _store = null!;
  private PoolManifest _manifest = null!;

  private static readonly Guid _poolId = Guid.Parse("b1f20000-0000-0000-0000-00000000000a");
  private static readonly Guid _member1 = Guid.Parse("a1000000-0000-0000-0000-000000000001");
  private static readonly Guid _member2 = Guid.Parse("a2000000-0000-0000-0000-000000000002");

  [SetUp]
  public void SetUp() {
    this._host = new();
    this._host.AddVolume(@"A:\");
    this._host.AddVolume(@"B:\");
    this._host.AddDirectory(@"B:\test");
    this._store = new(this._host);
    this._manifest = new() {
      PoolId = _poolId,
      Name = "MyPool",
      Members = [
        new() { MemberId = _member1, Path = @"A:\", Label = "SSD" },
        new() { MemberId = _member2, Path = @"B:\test" },
      ],
    };
  }

  [Test]
  [Category("HappyPath")]
  public void Save_GivenTwoOnlineMembers_WhenSaved_ThenRegistryAndBothMirrorsAndMarkersWritten() {
    var persisted = this._store.Save(this._manifest);

    persisted.Version.Should().Be(1, "every save bumps the version (SAFE-MANIFEST)");
    this._host.FileExists(this._store.RegistryPathFor(_poolId)).Should().BeTrue();
    this._host.FileExists(ManifestStore.MirrorPathFor(@"A:\")).Should().BeTrue();
    this._host.FileExists(ManifestStore.MirrorPathFor(@"B:\test")).Should().BeTrue();

    var marker = ManifestSerializer.ParseMarker(this._host.TryGetFileContent(ManifestStore.MarkerPathFor(@"B:\test"))!);
    marker.PoolId.Should().Be(_poolId);
    marker.MemberId.Should().Be(_member2);
  }

  [Test]
  [Category("EdgeCase")]
  public void Save_GivenOfflineMember_WhenSaved_ThenOtherCopiesStillWritten() {
    this._host.SetVolumeOnline(@"B:\", false);

    var act = () => this._store.Save(this._manifest);
    act.Should().NotThrow("an offline member must not block persisting the definition");

    this._host.FileExists(this._store.RegistryPathFor(_poolId)).Should().BeTrue();
    this._host.FileExists(ManifestStore.MirrorPathFor(@"A:\")).Should().BeTrue();
  }

  [Test]
  [Category("HappyPath")]
  public void Reconcile_GivenStaleRegistry_WhenReconciled_ThenHighestMemberVersionWinsAndRefreshes() {
    var v1 = this._store.Save(this._manifest);
    // a newer copy exists only on member B (e.g. edited while this machine was away)
    var newer = v1 with { Version = 5, Name = "RenamedPool" };
    this._host.AddFile(ManifestStore.MirrorPathFor(@"B:\test"), ManifestSerializer.Write(newer));

    var winner = this._store.Reconcile(_poolId, [@"A:\", @"B:\test"]);

    winner!.Version.Should().Be(5);
    winner.Name.Should().Be("RenamedPool");
    ManifestSerializer.Parse(this._host.TryGetFileContent(this._store.RegistryPathFor(_poolId))!).Version.Should().Be(5, "stale copies are refreshed");
    ManifestSerializer.Parse(this._host.TryGetFileContent(ManifestStore.MirrorPathFor(@"A:\"))!).Version.Should().Be(5);
  }

  [Test]
  [Category("HappyPath")]
  public void Reconcile_GivenLostRegistry_WhenReconciled_ThenPoolReconstructedFromSingleMemberMirror() {
    this._store.Save(this._manifest);
    this._host.RemoveFile(this._store.RegistryPathFor(_poolId));

    var winner = this._store.Reconcile(_poolId, [@"B:\test"]);

    winner.Should().NotBeNull("a pool must be reconstructable from any single member marker (SAFE-MANIFEST)");
    winner!.PoolId.Should().Be(_poolId);
    this._host.FileExists(this._store.RegistryPathFor(_poolId)).Should().BeTrue("the registry copy is restored");
  }

  [Test]
  [Category("EdgeCase")]
  public void Reconcile_GivenNoCopiesAnywhere_WhenReconciled_ThenNull()
    => this._store.Reconcile(Guid.NewGuid(), [@"A:\"]).Should().BeNull();

  [Test]
  [Category("HappyPath")]
  public void Save_GivenAnyWrite_WhenPersisted_ThenAlwaysAtomic() {
    this._store.Save(this._manifest);
    this._host.AtomicWriteCount.Should().BeGreaterThan(0);
  }

  [Test]
  [Category("EdgeCase")]
  public void LoadRegistry_GivenOneCorruptEntry_WhenLoaded_ThenOthersStillReturned() {
    this._store.Save(this._manifest);
    this._host.AddFile(Path.Combine(this._store.RegistryDirectory, "corrupt.json"), "{ broken");

    this._store.LoadRegistry().Should().ContainSingle(m => m.PoolId == _poolId);
  }

}
