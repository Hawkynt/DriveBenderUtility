using DivisonM.Vfs;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class ManifestSerializerTests {

  private static string _ValidJson(string extras = "") => $$"""
  {
    "schema": "drivebender-pool/1",
    "poolId": "b1f20000-0000-0000-0000-000000000001",
    "name": "MyPool",
    "version": 3,
    "members": [
      { "memberId": "a1000000-0000-0000-0000-000000000001", "path": "A:\\", "role": "landing", "label": "SSD" },
      { "memberId": "a2000000-0000-0000-0000-000000000002", "path": "B:\\test", "role": "capacity", "reserveBytes": "20GiB" },
      { "memberId": "a3000000-0000-0000-0000-000000000003", "path": "\\\\server\\share\\pool", "role": "capacity", "credential": "cred-ref:MyPool-server", "network": true }
    ],
    "mount": { "target": "X:\\", "volumeLabel": "MyPool" }{{extras}}
  }
  """;

  [Test]
  [Category("HappyPath")]
  public void Parse_GivenValidManifest_WhenParsed_ThenAllFieldsMapped() {
    var manifest = ManifestSerializer.Parse(_ValidJson());

    manifest.PoolId.Should().Be(Guid.Parse("b1f20000-0000-0000-0000-000000000001"));
    manifest.Name.Should().Be("MyPool");
    manifest.Version.Should().Be(3);
    manifest.Members.Should().HaveCount(3);
    manifest.Members[0].Role.Should().Be(MemberRole.Landing);
    manifest.Members[1].ReserveBytes.Should().Be(20L * 1024 * 1024 * 1024, "'20GiB' is a unit string");
    manifest.Members[2].Network.Should().BeTrue();
    manifest.Members[2].Credential.Should().Be("cred-ref:MyPool-server");
    manifest.Mount!.Target.Should().Be(@"X:\");
    manifest.IsVirtual.Should().BeFalse();
  }

  [Test]
  [Category("HappyPath")]
  public void WriteParse_GivenManifest_WhenRoundTripped_ThenEquivalent() {
    var original = ManifestSerializer.Parse(_ValidJson());
    var reparsed = ManifestSerializer.Parse(ManifestSerializer.Write(original));

    reparsed.Should().BeEquivalentTo(original, o => o.Excluding(m => m.ExtensionData));
  }

  [Test]
  [Category("EdgeCase")]
  public void WriteParse_GivenUnknownKeys_WhenRoundTripped_ThenPreserved() {
    var manifest = ManifestSerializer.Parse(_ValidJson(""", "futureFeature": {"x": 1}"""));
    var json = ManifestSerializer.Write(manifest);

    json.Should().Contain("futureFeature", "unknown keys must survive a rewrite (CFG-SCHEMA forward compatibility)");
  }

  [Test]
  [Category("Exception")]
  public void Parse_GivenNewerMajorSchema_WhenParsed_ThenRefused() {
    var json = _ValidJson().Replace("drivebender-pool/1", "drivebender-pool/2");
    var act = () => ManifestSerializer.Parse(json);
    act.Should().Throw<ManifestException>().WithMessage("*newer than this application supports*");
  }

  [Test]
  [Category("Exception")]
  public void Parse_GivenUnknownSchemaFamily_WhenParsed_ThenRefused() {
    var json = _ValidJson().Replace("drivebender-pool/1", "other-thing/1");
    var act = () => ManifestSerializer.Parse(json);
    act.Should().Throw<ManifestException>().WithMessage("*Unknown manifest schema*");
  }

  [Test]
  [Category("Exception")]
  public void Parse_GivenMissingName_WhenParsed_ThenRejected() {
    var json = _ValidJson().Replace("\"name\": \"MyPool\",", "\"name\": \"\",");
    var act = () => ManifestSerializer.Parse(json);
    act.Should().Throw<ManifestException>().WithMessage("*'name'*");
  }

  [Test]
  [Category("Exception")]
  public void Parse_GivenDuplicateMemberIds_WhenParsed_ThenRejected() {
    var json = _ValidJson().Replace("a2000000-0000-0000-0000-000000000002", "a1000000-0000-0000-0000-000000000001");
    var act = () => ManifestSerializer.Parse(json);
    act.Should().Throw<ManifestException>().WithMessage("*Duplicate memberId*");
  }

  [Test]
  [Category("Exception")]
  public void Parse_GivenGarbage_WhenParsed_ThenRejectedWithPreciseMessage() {
    var act = () => ManifestSerializer.Parse("{ not json !");
    act.Should().Throw<ManifestException>().WithMessage("*not valid JSON*");
  }

  [Test]
  [Category("HappyPath")]
  public void ParseMarker_GivenValidMarker_WhenParsed_ThenMapped() {
    var marker = ManifestSerializer.ParseMarker("""{ "poolId": "b1f20000-0000-0000-0000-000000000001", "memberId": "a1000000-0000-0000-0000-000000000001", "name": "SSD" }""");
    marker.PoolId.Should().Be(Guid.Parse("b1f20000-0000-0000-0000-000000000001"));
    marker.Name.Should().Be("SSD");
  }

  [Test]
  [Category("Exception")]
  public void ParseMarker_GivenEmptyIds_WhenParsed_ThenRejected() {
    var act = () => ManifestSerializer.ParseMarker("""{ "poolId": "00000000-0000-0000-0000-000000000000", "memberId": "a1000000-0000-0000-0000-000000000001" }""");
    act.Should().Throw<ManifestException>();
  }

}
