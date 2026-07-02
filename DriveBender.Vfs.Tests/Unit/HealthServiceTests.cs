using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Fake SMART source so health checks run headlessly (TST-FAKE).</summary>
internal sealed class FakeSmartMonitor : ISmartMonitor {
  private readonly Dictionary<string, SmartStatus> _byDevice = new(StringComparer.OrdinalIgnoreCase);
  public bool IsSupported => true;
  public void Set(string device, SmartStatus status) => this._byDevice[device] = status;
  public SmartStatus Query(string device) => this._byDevice.GetValueOrDefault(device, SmartStatus.Unavailable(device));
}

[TestFixture]
[Category("Unit")]
public class SmartParsingTests {

  [Test]
  [Category("HappyPath")]
  public void Parse_GivenHealthyDrive_WhenParsed_ThenHealthy() {
    const string json = """{ "model_name": "WDC Blue", "smart_status": { "passed": true }, "temperature": { "current": 34 }, "power_on_time": { "hours": 1200 } }""";
    var status = SmartParsing.Parse("/dev/sda", json);
    status.Health.Should().Be(DiskHealth.Healthy);
    status.TemperatureCelsius.Should().Be(34);
    status.Model.Should().Be("WDC Blue");
    status.PowerOnHours.Should().Be(1200);
  }

  [Test]
  [Category("Exception")]
  public void Parse_GivenFailedSmartStatus_WhenParsed_ThenFailing() {
    const string json = """{ "smart_status": { "passed": false }, "temperature": { "current": 40 } }""";
    SmartParsing.Parse("/dev/sda", json).Health.Should().Be(DiskHealth.Failing);
  }

  [Test]
  [Category("EdgeCase")]
  public void Parse_GivenPendingSectors_WhenParsed_ThenFailing() {
    const string json = """{ "smart_status": { "passed": true }, "ata_smart_attributes": { "table": [ { "id": 197, "raw": { "value": 8 } } ] } }""";
    SmartParsing.Parse("/dev/sda", json).Health.Should().Be(DiskHealth.Failing);
  }

  [Test]
  [Category("EdgeCase")]
  public void Parse_GivenReallocatedSectorsOrHotTemp_WhenParsed_ThenWarning() {
    SmartParsing.Parse("/dev/sda", """{ "smart_status": { "passed": true }, "ata_smart_attributes": { "table": [ { "id": 5, "raw": { "value": 3 } } ] } }""")
      .Health.Should().Be(DiskHealth.Warning);
    SmartParsing.Parse("/dev/sda", """{ "smart_status": { "passed": true }, "temperature": { "current": 58 } }""")
      .Health.Should().Be(DiskHealth.Warning);
  }

  [Test]
  [Category("Exception")]
  public void Parse_GivenGarbage_WhenParsed_ThenUnknown()
    => SmartParsing.Parse("/dev/sda", "not json").Health.Should().Be(DiskHealth.Unknown);

}

[TestFixture]
[Category("Unit")]
public class HealthServiceTests {

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private FakeVolumeIO _v3 = null!;
  private FakeSmartMonitor _smart = null!;
  private Journal _journal = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    this._v3 = new(Guid.NewGuid(), "v3", "PHYS-3", capacity: 1L << 20);
    this._smart = new();
    this._journal = new(new MemberJournalStore([this._v1, this._v2, this._v3]));
  }

  private HealthService _Health(int duplication) {
    var members = new IVolumeIO[] { this._v1, this._v2, this._v3 };
    var integrity = new IntegrityService(members);
    var media = new MediaLifecycle(members, this._journal, duplication);
    return new(members, this._smart, integrity, media);
  }

  [Test]
  [Category("HappyPath")]
  public void Check_GivenHealthyPool_WhenChecked_ThenHealthy() {
    this._v1.Seed("f.bin", false, [1]);
    this._v2.Seed("f.bin", true, [1]);
    foreach (var phys in new[] { "PHYS-1", "PHYS-2", "PHYS-3" })
      this._smart.Set(phys, new(phys, DiskHealth.Healthy, 30, 0, 0, 100, "model", "ok"));

    var report = this._Health(2).Check();

    report.Healthy.Should().BeTrue();
    report.UnderDuplicatedFiles.Should().Be(0);
    report.Members.Should().OnlyContain(m => m.Smart.Health == DiskHealth.Healthy);
  }

  [Test]
  [Category("HappyPath")]
  public void Check_GivenUnderDuplicated_WhenChecked_ThenReported() {
    this._v1.Seed("lonely.bin", false, [9]); // only one copy, D=2

    this._Health(2).Check().UnderDuplicatedFiles.Should().Be(1);
  }

  [Test]
  [Category("HappyPath")]
  public void CheckAndCorrect_GivenMissingShadowsAndBitRot_WhenCorrected_ThenBothFixed() {
    // under-duplicated file + a silently corrupted duplicated file
    this._v1.Seed("under.bin", false, [1, 2]);
    this._v1.Seed("rot.bin", false, [5, 5, 5]);
    this._v2.Seed("rot.bin", true, [5, 5, 5]);

    var integrity = new IntegrityService([this._v1, this._v2, this._v3]);
    integrity.RecordWholeFile(this._v1, "rot.bin", false, [5, 5, 5]);
    integrity.RecordWholeFile(this._v2, "rot.bin", true, [5, 5, 5]);
    integrity.SaveAll();
    this._v1.CorruptSilently("rot.bin", false, c => c[0] = 99); // bit-rot: content changed, size/mtime same

    var media = new MediaLifecycle([this._v1, this._v2, this._v3], this._journal, 2);
    var health = new HealthService([this._v1, this._v2, this._v3], this._smart, integrity, media);

    var report = health.CheckAndCorrect();

    report.Corrected.Should().BeTrue();
    report.IntegrityIssues.Should().Contain(i => i.Kind == IntegrityIssueKind.BitRotRepaired);
    this._v1.GetContent("rot.bin", false).Should().Equal(new byte[] { 5, 5, 5 }, "bit-rot repaired from the good copy");
    report.CopiesRepaired.Should().BeGreaterThan(0, "the under-duplicated file got its missing shadow");
    report.UnderDuplicatedFiles.Should().Be(0, "duplication fully restored after correction");
  }

  [Test]
  [Category("EdgeCase")]
  public void Check_GivenFailingDrive_WhenChecked_ThenSurfacedAsUnhealthy() {
    this._v1.Seed("f.bin", false, [1]);
    this._v2.Seed("f.bin", true, [1]);
    this._smart.Set("PHYS-1", new("PHYS-1", DiskHealth.Failing, 62, 40, 8, 40000, "old-disk", "SMART reported a problem"));

    var report = this._Health(2).Check();

    report.Healthy.Should().BeFalse();
    report.UnhealthyMembers.Should().ContainSingle(m => m.Member == "v1");
  }

}
