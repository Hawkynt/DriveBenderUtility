using DivisonM.Vfs;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class NativeScanSourceTests {

  private static readonly Guid _poolId = Guid.Parse("11111111-2222-3333-4444-555555555555");

  private FakeHostEnvironment _host = null!;

  [SetUp]
  public void SetUp() => this._host = new();

  private void _AddNativeDrive(string root, string physicalId, Guid poolId, string label = "MediaPool") {
    this._host.AddVolume(root, physicalId);
    this._host.AddFile(Path.Combine(root, "pool info." + DriveBender.DriveBenderConstants.INFO_EXTENSION),
      $"volumelabel:{label}\nid:{poolId}\ndescription:native pool");
    this._host.AddDirectory(Path.Combine(root, $"{{{poolId}}}"));
  }

  [Test]
  [Category("HappyPath")]
  public void Enumerate_GivenTwoNativeDrives_WhenScanned_ThenOneVirtualManifestWithBothMembers() {
    this._AddNativeDrive(@"F:\", "PHYS-F", _poolId);
    this._AddNativeDrive(@"G:\", "PHYS-G", _poolId);

    var manifests = new NativeScanSource(this._host).Enumerate().ToArray();

    manifests.Should().ContainSingle();
    var manifest = manifests[0];
    manifest.PoolId.Should().Be(_poolId);
    manifest.Name.Should().Be("MediaPool");
    manifest.IsVirtual.Should().BeTrue();
    manifest.Members.Should().HaveCount(2);
    manifest.Members.Select(m => m.Path).Should().BeEquivalentTo(
      [$@"F:\{{{_poolId}}}", $@"G:\{{{_poolId}}}"],
      "members are the drives' pool-GUID root folders (§1.3)");
  }

  [Test]
  [Category("HappyPath")]
  public void Enumerate_GivenSamePool_WhenScannedTwice_ThenMemberIdsDeterministic() {
    this._AddNativeDrive(@"F:\", "PHYS-F", _poolId);

    var first = new NativeScanSource(this._host).Enumerate().Single().Members.Single().MemberId;
    var second = new NativeScanSource(this._host).Enumerate().Single().Members.Single().MemberId;

    first.Should().Be(second, "native member identity derives from (pool id, physical volume)");
    first.Should().Be(NativeScanSource.DeriveMemberId(_poolId, "PHYS-F"));
  }

  [Test]
  [Category("HappyPath")]
  public void Enumerate_GivenNativeScan_WhenComparedToEquivalentExplicitManifest_ThenSamePoolModel() {
    this._AddNativeDrive(@"F:\", "PHYS-F", _poolId);
    var virtualManifest = new NativeScanSource(this._host).Enumerate().Single();

    // the equivalent hand-written manifest a user could author for the same pool
    var explicitManifest = ManifestSerializer.Parse($$"""
    {
      "schema": "drivebender-pool/1",
      "poolId": "{{_poolId}}",
      "name": "MediaPool",
      "members": [
        { "memberId": "{{NativeScanSource.DeriveMemberId(_poolId, "PHYS-F")}}", "path": "F:\\{{{_poolId}}}", "role": "capacity", "label": "MediaPool" }
      ]
    }
    """);

    virtualManifest.Should().BeEquivalentTo(explicitManifest,
      o => o.Excluding(m => m.IsVirtual).Excluding(m => m.ExtensionData),
      "a native pool is a manifest pool whose membership is derived by scanning (§1.3)");
  }

  [Test]
  [Category("EdgeCase")]
  public void Enumerate_GivenInfoFileWithoutGuidFolder_WhenScanned_ThenDriveIgnored() {
    this._host.AddVolume(@"H:\");
    this._host.AddFile(@"H:\stale." + DriveBender.DriveBenderConstants.INFO_EXTENSION, $"volumelabel:Old\nid:{Guid.NewGuid()}");

    new NativeScanSource(this._host).Enumerate().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void Enumerate_GivenMalformedInfoFile_WhenScanned_ThenDriveIgnored() {
    this._host.AddVolume(@"H:\");
    this._host.AddFile(@"H:\broken." + DriveBender.DriveBenderConstants.INFO_EXTENSION, "no useful content");

    new NativeScanSource(this._host).Enumerate().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void Enumerate_GivenTwoDifferentPools_WhenScanned_ThenTwoManifests() {
    var otherPool = Guid.NewGuid();
    this._AddNativeDrive(@"F:\", "PHYS-F", _poolId);
    this._AddNativeDrive(@"G:\", "PHYS-G", otherPool, label: "BackupPool");

    var manifests = new NativeScanSource(this._host).Enumerate().ToArray();

    manifests.Should().HaveCount(2);
    manifests.Select(m => m.PoolId).Should().BeEquivalentTo([_poolId, otherPool]);
  }

}
