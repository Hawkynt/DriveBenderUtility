using DivisonM.Vfs;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class MemberResolverTests {

  private static readonly Guid _poolId = Guid.Parse("b1f20000-0000-0000-0000-00000000000b");
  private static readonly Guid _memberId = Guid.Parse("a1000000-0000-0000-0000-000000000011");

  private FakeHostEnvironment _host = null!;
  private ManifestStore _store = null!;

  [SetUp]
  public void SetUp() {
    this._host = new();
    this._store = new(this._host);
  }

  private static PoolManifest _Manifest(string memberPath, bool isVirtual = false) => new() {
    PoolId = _poolId,
    Name = "P",
    Members = [new() { MemberId = _memberId, Path = memberPath }],
    IsVirtual = isVirtual,
  };

  private void _PlaceMarker(string path, Guid? poolId = null, Guid? memberId = null)
    => this._host.AddFile(ManifestStore.MarkerPathFor(path), ManifestSerializer.WriteMarker(new() {
      PoolId = poolId ?? _poolId,
      MemberId = memberId ?? _memberId,
    }));

  [Test]
  [Category("HappyPath")]
  public void Resolve_GivenValidPathHintWithMarker_WhenResolved_ThenOnlineAtHint() {
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._PlaceMarker(@"A:\");

    var member = new MemberResolver(this._host, this._store).Resolve(_Manifest(@"A:\"), _Manifest(@"A:\").Members[0]);

    member.Online.Should().BeTrue();
    member.ResolvedPath.Should().Be(@"A:\");
    member.PhysicalVolumeId.Should().Be("PHYS-A");
    member.MarkerVerified.Should().BeTrue();
    member.PathChanged.Should().BeFalse();
  }

  [Test]
  [Category("HappyPath")]
  public void Resolve_GivenDriveLetterChanged_WhenResolved_ThenFoundByMarkerContentAtNewLetter() {
    // the manifest still says A:\ but the removable drive now mounts as E:\
    this._host.AddVolume(@"E:\", "PHYS-REMOVABLE");
    this._PlaceMarker(@"E:\");

    var manifest = _Manifest(@"A:\");
    var member = new MemberResolver(this._host, this._store).Resolve(manifest, manifest.Members[0]);

    member.Online.Should().BeTrue();
    member.ResolvedPath.Should().Be(@"E:\", "resolution is by marker content, not path (FR-RESOLVE-MEMBER)");
    member.PathChanged.Should().BeTrue();
  }

  [Test]
  [Category("HappyPath")]
  public void Resolve_GivenSubfolderMemberFoundViaSearchPath_WhenResolved_ThenOnline() {
    this._host.AddVolume(@"C:\", "PHYS-C");
    this._host.AddDirectory(@"C:\pools\dir");
    this._PlaceMarker(@"C:\pools\dir");

    var manifest = _Manifest(@"D:\oldplace");
    var member = new MemberResolver(this._host, this._store, [@"C:\pools"]).Resolve(manifest, manifest.Members[0]);

    member.Online.Should().BeTrue();
    member.ResolvedPath.Should().Be(@"C:\pools\dir");
  }

  [Test]
  [Category("EdgeCase")]
  public void Resolve_GivenMemberNowhere_WhenResolved_ThenOfflineKeepingHint() {
    this._host.AddVolume(@"C:\");

    var manifest = _Manifest(@"A:\");
    var member = new MemberResolver(this._host, this._store).Resolve(manifest, manifest.Members[0]);

    member.Online.Should().BeFalse("an unplugged member degrades, it does not fail the pool (SAFE-OFFLINE)");
    member.ResolvedPath.Should().Be(@"A:\", "the hint is kept for reconciliation on return");
  }

  [Test]
  [Category("EdgeCase")]
  public void Resolve_GivenHintClaimedByForeignPool_WhenResolved_ThenScanFindsRealMemberElsewhere() {
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._host.AddVolume(@"E:\", "PHYS-E");
    this._PlaceMarker(@"A:\", poolId: Guid.NewGuid()); // someone else's member now lives at the hint
    this._PlaceMarker(@"E:\");

    var manifest = _Manifest(@"A:\");
    var member = new MemberResolver(this._host, this._store).Resolve(manifest, manifest.Members[0]);

    member.ResolvedPath.Should().Be(@"E:\");
    member.Online.Should().BeTrue();
  }

  [Test]
  [Category("EdgeCase")]
  public void Resolve_GivenVirtualManifest_WhenResolved_ThenPathTrustedWithoutMarker() {
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._host.AddDirectory(@"A:\{guid}");

    var manifest = _Manifest(@"A:\{guid}", isVirtual: true);
    var member = new MemberResolver(this._host, this._store).Resolve(manifest, manifest.Members[0]);

    member.Online.Should().BeTrue("a scan-synthesized manifest's paths come from the live scan itself");
    member.MarkerVerified.Should().BeTrue();
  }

  [Test]
  [Category("EdgeCase")]
  public void Resolve_GivenExplicitManifestWithMarkerlessExistingHint_WhenResolved_ThenOnlineButUnverified() {
    this._host.AddVolume(@"B:\", "PHYS-B");
    this._host.AddDirectory(@"B:\test");

    var manifest = _Manifest(@"B:\test");
    var member = new MemberResolver(this._host, this._store).Resolve(manifest, manifest.Members[0]);

    member.Online.Should().BeTrue();
    member.MarkerVerified.Should().BeFalse("no marker exists yet — the provider should surface this");
  }

}
