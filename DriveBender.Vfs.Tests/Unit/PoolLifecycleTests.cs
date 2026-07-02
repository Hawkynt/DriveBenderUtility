using DivisonM.Vfs;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class PoolLifecycleTests {

  private FakeHostEnvironment _host = null!;
  private ManifestStore _store = null!;
  private PoolLifecycle _lifecycle = null!;

  [SetUp]
  public void SetUp() {
    this._host = new();
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._host.AddVolume(@"B:\", "PHYS-B");
    this._host.AddVolume(@"C:\", "PHYS-C");
    this._store = new(this._host);
    this._lifecycle = new(this._host, this._store);
  }

  [Test]
  [Category("HappyPath")]
  public void Create_GivenMixedMemberKinds_WhenCreated_ThenManifestPersistedAndMarkersWritten() {
    this._host.AddDirectory(@"B:\test");

    var manifest = this._lifecycle.Create("MyPool", [
      new(@"A:\", MemberRole.Landing, "SSD"),
      new(@"B:\test"),
      new(@"C:\pools\dir"),
    ], mountTarget: @"X:\");

    manifest.PoolId.Should().NotBeEmpty();
    manifest.Version.Should().Be(1);
    manifest.Members.Should().HaveCount(3);
    manifest.Mount!.Target.Should().Be(@"X:\");
    this._host.DirectoryExists(@"C:\pools\dir").Should().BeTrue("missing member folders are created");
    this._host.FileExists(ManifestStore.MarkerPathFor(@"A:\")).Should().BeTrue();
    this._host.FileExists(this._store.RegistryPathFor(manifest.PoolId)).Should().BeTrue();
  }

  [Test]
  [Category("Exception")]
  public void Create_GivenNonEmptyFolderWithoutForce_WhenCreated_ThenRefused() {
    this._host.AddDirectory(@"B:\test");
    this._host.AddFile(@"B:\test\existing.txt", "precious");

    var act = () => this._lifecycle.Create("MyPool", [new(@"B:\test")]);

    act.Should().Throw<NonDestructiveViolationException>("pre-existing data needs explicit consent (SAFE-NONDESTRUCTIVE)");
  }

  [Test]
  [Category("HappyPath")]
  public void Create_GivenNonEmptyFolderWithForce_WhenCreated_ThenAcceptedAndDataUntouched() {
    this._host.AddDirectory(@"B:\test");
    this._host.AddFile(@"B:\test\existing.txt", "precious");

    var manifest = this._lifecycle.Create("MyPool", [new(@"B:\test")], force: true);

    manifest.Members.Should().ContainSingle();
    this._host.TryGetFileContent(@"B:\test\existing.txt").Should().Be("precious");
  }

  [Test]
  [Category("Exception")]
  public void Create_GivenFolderOwnedByAnotherPool_WhenCreatedEvenWithForce_ThenAlwaysRefused() {
    this._host.AddDirectory(@"B:\test");
    this._host.AddFile(ManifestStore.MarkerPathFor(@"B:\test"),
      ManifestSerializer.WriteMarker(new() { PoolId = Guid.NewGuid(), MemberId = Guid.NewGuid() }));

    var act = () => this._lifecycle.Create("MyPool", [new(@"B:\test")], force: true);

    act.Should().Throw<NonDestructiveViolationException>().WithMessage("*another pool*");
  }

  [Test]
  [Category("HappyPath")]
  public void Adopt_GivenNativePool_WhenAdopted_ThenExplicitManifestPersistedInPlace() {
    var poolId = Guid.NewGuid();
    var memberPath = $@"A:\{{{poolId}}}";
    this._host.AddDirectory(memberPath);
    this._host.AddFile($@"A:\{memberPath}\somefile.bin", "data"); // pre-existing pool data
    var virtualManifest = new PoolManifest {
      PoolId = poolId,
      Name = "NativePool",
      Members = [new() { MemberId = Guid.NewGuid(), Path = memberPath }],
      IsVirtual = true,
    };

    var adopted = this._lifecycle.Adopt(new(poolId, "NativePool", true, virtualManifest));

    adopted.IsVirtual.Should().BeFalse();
    adopted.Version.Should().Be(1);
    this._host.FileExists(this._store.RegistryPathFor(poolId)).Should().BeTrue();
    this._host.FileExists(ManifestStore.MarkerPathFor(memberPath)).Should().BeTrue("markers are written in place, no data moves (FR-ADOPT)");
  }

  [Test]
  [Category("Exception")]
  public void Adopt_GivenAlreadyExplicitPool_WhenAdopted_ThenRefused() {
    var manifest = this._lifecycle.Create("MyPool", [new(@"A:\")]);
    var act = () => this._lifecycle.Adopt(new(manifest.PoolId, manifest.Name, false, manifest));
    act.Should().Throw<ManifestException>().WithMessage("*already*");
  }

  [Test]
  [Category("HappyPath")]
  public void AddMember_GivenNewFolder_WhenAdded_ThenVersionBumpedAndMarkerWritten() {
    var manifest = this._lifecycle.Create("MyPool", [new(@"A:\")]);

    var updated = this._lifecycle.AddMember(manifest, new(@"C:\pools\extra"));

    updated.Members.Should().HaveCount(2);
    updated.Version.Should().Be(2);
    this._host.FileExists(ManifestStore.MarkerPathFor(@"C:\pools\extra")).Should().BeTrue();
  }

  [Test]
  [Category("Exception")]
  public void AddMember_GivenDuplicatePath_WhenAdded_ThenRefused() {
    var manifest = this._lifecycle.Create("MyPool", [new(@"A:\")]);
    var act = () => this._lifecycle.AddMember(manifest, new(@"A:\"));
    act.Should().Throw<ManifestException>().WithMessage("*already a member*");
  }

  [Test]
  [Category("HappyPath")]
  public void RemoveMember_GivenTwoMembers_WhenRemoved_ThenDataStaysAndSidecarsGone() {
    this._host.AddDirectory(@"B:\test");
    this._host.AddFile(@"B:\test\data.bin", "keep me");
    var manifest = this._lifecycle.Create("MyPool", [new(@"A:\"), new(@"B:\test")], force: true);
    var memberId = manifest.Members[1].MemberId;

    var updated = this._lifecycle.RemoveMember(manifest, memberId);

    updated.Members.Should().ContainSingle();
    this._host.TryGetFileContent(@"B:\test\data.bin").Should().Be("keep me", "removal never destroys user data");
    this._host.FileExists(ManifestStore.MarkerPathFor(@"B:\test")).Should().BeFalse("our sidecars are removed");
  }

  [Test]
  [Category("Exception")]
  public void RemoveMember_GivenLastMember_WhenRemoved_ThenRefused() {
    var manifest = this._lifecycle.Create("MyPool", [new(@"A:\")]);
    var act = () => this._lifecycle.RemoveMember(manifest, manifest.Members[0].MemberId);
    act.Should().Throw<ManifestException>().WithMessage("*last member*");
  }

  [Test]
  [Category("HappyPath")]
  public void Import_GivenExternalManifestJson_WhenImported_ThenRegistryEntryCreated() {
    var poolId = Guid.NewGuid();
    var memberId = Guid.NewGuid();
    var json = $$"""
    {
      "schema": "drivebender-pool/1",
      "poolId": "{{poolId}}",
      "name": "Imported",
      "members": [{ "memberId": "{{memberId}}", "path": "A:\\" }]
    }
    """;

    var imported = this._lifecycle.Import(json);

    imported.PoolId.Should().Be(poolId);
    this._host.FileExists(this._store.RegistryPathFor(poolId)).Should().BeTrue();
  }

  [Test]
  [Category("HappyPath")]
  public void Export_GivenManifest_WhenExported_ThenReimportable() {
    var manifest = this._lifecycle.Create("MyPool", [new(@"A:\")]);
    var json = this._lifecycle.Export(manifest);

    var reparsed = ManifestSerializer.Parse(json);
    reparsed.PoolId.Should().Be(manifest.PoolId);
    reparsed.Members.Should().HaveCount(1);
  }

}
