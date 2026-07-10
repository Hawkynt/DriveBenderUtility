using DivisonM.Vfs;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class PoolDeletePurgeTests {

  private FakeHostEnvironment _host = null!;
  private ManifestStore _store = null!;
  private PoolLifecycle _lifecycle = null!;

  [SetUp]
  public void SetUp() {
    this._host = new();
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._host.AddVolume(@"B:\", "PHYS-B");
    this._host.AddDirectory(@"A:\pool");
    this._host.AddDirectory(@"B:\pool");
    this._store = new(this._host);
    this._lifecycle = new(this._host, this._store);
  }

  private PoolManifest _CreatePoolWithData() {
    var manifest = this._lifecycle.Create("DelPool", [new(@"A:\pool"), new(@"B:\pool")], force: true);
    this._host.AddFile(@"A:\pool\movie.mkv", "big");
    this._host.AddFile(@"A:\pool\docs\report.txt", "text");
    this._host.AddFile(@"B:\pool\movie.mkv", "big");
    return manifest;
  }

  [Test]
  [Category("HappyPath")]
  public void Delete_GivenPool_WhenDeletedWithoutPurge_ThenRegistryAndSidecarsGoneButDataKept() {
    var manifest = this._CreatePoolWithData();

    this._lifecycle.Delete(manifest, purgeData: false);

    this._host.FileExists(this._store.RegistryPathFor(manifest.PoolId)).Should().BeFalse("the pool is removed from the registry");
    this._host.FileExists(ManifestStore.MarkerPathFor(@"A:\pool")).Should().BeFalse("member markers are removed");
    this._host.TryGetFileContent(@"A:\pool\movie.mkv").Should().Be("big", "data is preserved on a plain delete");
    this._host.TryGetFileContent(@"A:\pool\docs\report.txt").Should().Be("text");
  }

  [Test]
  [Category("HappyPath")]
  public void Delete_GivenPool_WhenPurged_ThenDataWipedAndRegistryGone() {
    var manifest = this._CreatePoolWithData();

    this._lifecycle.Delete(manifest, purgeData: true);

    this._host.FileExists(this._store.RegistryPathFor(manifest.PoolId)).Should().BeFalse();
    this._host.TryGetFileContent(@"A:\pool\movie.mkv").Should().BeNull("purge wipes pool content");
    this._host.TryGetFileContent(@"A:\pool\docs\report.txt").Should().BeNull("purge wipes nested content too");
    this._host.TryGetFileContent(@"B:\pool\movie.mkv").Should().BeNull("purge wipes every member");
  }

  [Test]
  [Category("Exception")]
  public void Delete_GivenPathNowOwnedByAnotherPool_WhenPurged_ThenForeignDataIsNotWiped() {
    var manifest = this._CreatePoolWithData();

    // simulate: B: was reassigned and B:\pool now holds a DIFFERENT pool's member (its marker)
    this._host.AddFile(ManifestStore.MarkerPathFor(@"B:\pool"),
      ManifestSerializer.WriteMarker(new() { PoolId = Guid.NewGuid(), MemberId = Guid.NewGuid(), Name = "StrangerPool" }));

    this._lifecycle.Delete(manifest, purgeData: true);

    this._host.TryGetFileContent(@"B:\pool\movie.mkv").Should().Be("big",
      "purge must never wipe a path whose marker identifies a different pool (reassigned drive letter)");
    this._host.TryGetFileContent(@"A:\pool\movie.mkv").Should().BeNull("the genuine member is still purged");
    this._host.FileExists(this._store.RegistryPathFor(manifest.PoolId)).Should().BeFalse("the registry entry still clears");
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenMissingMemberFolder_WhenDeleted_ThenSkipsGracefully() {
    var manifest = this._lifecycle.Create("P", [new(@"A:\pool")], force: true);
    this._host.SetVolumeOnline(@"A:\", false); // member vanished

    var act = () => this._lifecycle.Delete(manifest, purgeData: true);
    act.Should().NotThrow("a missing member is skipped, the registry entry still clears");
    this._host.FileExists(this._store.RegistryPathFor(manifest.PoolId)).Should().BeFalse();
  }

}
