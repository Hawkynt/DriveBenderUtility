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

  [Test]
  [Category("Exception")]
  public void Parse_GivenSmartctlCouldNotOpenTheDevice_WhenParsed_ThenUnknownRatherThanFailing() {
    // Verbatim from smartctl 7.5 on a machine where the caller is not root, which is the ordinary
    // case for a daemon a user starts: valid JSON, an error in `smartctl.messages`, a non-zero
    // `exit_status`, and NO `smart_status` object at all.
    //
    // The parser read a missing `smart_status.passed` as false, i.e. as the drive having FAILED its
    // own self-assessment, so every member of every pool on such a machine was reported as a dying
    // disk. That is the worst direction for a health signal to be wrong in: it is indistinguishable
    // from the real thing, it fires on hardware that is perfectly fine, and once an operator has
    // seen it cry wolf they stop believing the one that matters.
    const string json = """
      {
        "smartctl": {
          "version": [7, 5],
          "messages": [{ "string": "Smartctl open device: /dev/nvme0n1 failed: Permission denied", "severity": "error" }],
          "exit_status": 2
        },
        "local_time": { "time_t": 1788458610 }
      }
      """;

    var status = SmartParsing.Parse("/dev/nvme0n1", json);
    status.Health.Should().Be(DiskHealth.Unknown,
      "a drive that could not be ASKED is not a drive that answered badly");
    status.Detail.Should().Contain("Permission denied",
      "and the reason must be carried through, or the operator cannot tell 'no smartctl' from 'not allowed'");
  }

  [Test]
  [Category("HappyPath")]
  public void Parse_GivenRealSmartctlOutputFromAnNvmeDrive_WhenParsed_ThenItIsReadCorrectly() {
    // Captured verbatim from `smartctl -j -i -H -A` on a healthy NVMe (identifiers removed). Kept as
    // a fixture because the synthetic cases above were written from the documentation and this one
    // was not: it is what the tool ACTUALLY emits, and it settled two things the hand-written JSON
    // could not. This drive has no `ata_smart_attributes` object AT ALL, so the parser that read
    // only ATA ids 5 and 197 had literally nothing to work with on hardware of this kind; and
    // `model_name` appears only when `-i` is passed, which the query did not do.
    const string json = """
      {
        "json_format_version": [1, 0],
        "smartctl": { "version": [7, 5], "exit_status": 0 },
        "device": { "name": "/dev/nvme0n1", "type": "nvme", "protocol": "NVMe" },
        "model_name": "KINGSTON SFYRD4000G",
        "firmware_version": "EIFK31.6",
        "smart_status": { "passed": true },
        "temperature": { "current": 28 },
        "nvme_smart_health_information_log": {
          "critical_warning": 0,
          "temperature": 28,
          "available_spare": 100,
          "available_spare_threshold": 10,
          "percentage_used": 0,
          "data_units_written": 4457984,
          "media_errors": 0,
          "power_on_hours": 10300
        }
      }
      """;

    var status = SmartParsing.Parse("/dev/nvme0n1", json);
    status.Health.Should().Be(DiskHealth.Healthy, "this drive is genuinely fine and must be reported so");
    status.Model.Should().Be("KINGSTON SFYRD4000G", "the model is what names a member in the report");
    status.TemperatureCelsius.Should().Be(28);
    status.PowerOnHours.Should().Be(10300);
    status.PercentageUsed.Should().Be(0);
    status.AvailableSparePercent.Should().Be(100);
    status.MediaErrors.Should().Be(0);
    status.ReallocatedSectors.Should().BeNull("an NVMe has no ATA attribute table to read them from");
  }

  [Test]
  [Category("EdgeCase")]
  public void Parse_GivenAnNvmeDriveWearingOut_WhenParsed_ThenItIsNotReportedAsHealthy() {
    // NVMe reports its health in `nvme_smart_health_information_log`, not in the ATA attribute
    // table — so a parser that only reads ATA ids 5 and 197 sees nothing but `passed: true` and a
    // temperature. This drive has used 96% of its rated endurance, has almost no spare blocks left
    // and is logging media errors, and every one of those was invisible.
    const string json = """
      {
        "model_name": "Some NVMe SSD",
        "smart_status": { "passed": true },
        "temperature": { "current": 41 },
        "nvme_smart_health_information_log": {
          "critical_warning": 0,
          "temperature": 41,
          "available_spare": 4,
          "available_spare_threshold": 10,
          "percentage_used": 96,
          "media_errors": 17,
          "power_on_hours": 41000
        }
      }
      """;

    var status = SmartParsing.Parse("/dev/nvme0n1", json);
    status.Health.Should().NotBe(DiskHealth.Healthy,
      "a drive at 96% of its rated life, below its spare threshold and logging media errors is not healthy");
    status.PowerOnHours.Should().Be(41000, "NVMe reports its hours in its own log");
  }

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
  [Category("HappyPath")]
  public void Check_GivenBitRot_WhenDefaultScan_ThenNotSeenButDeepScanFindsItWithoutRepairing() {
    this._v1.Seed("rot.bin", false, [5, 5, 5]);
    this._v2.Seed("rot.bin", true, [5, 5, 5]);
    var integrity = new IntegrityService([this._v1, this._v2, this._v3]);
    integrity.RecordWholeFile(this._v1, "rot.bin", false, [5, 5, 5]);
    integrity.RecordWholeFile(this._v2, "rot.bin", true, [5, 5, 5]);
    integrity.SaveAll();
    this._v1.CorruptSilently("rot.bin", false, c => c[0] = 99); // size/mtime unchanged — invisible to metadata

    var media = new MediaLifecycle([this._v1, this._v2, this._v3], this._journal, 2);
    var health = new HealthService([this._v1, this._v2, this._v3], this._smart, integrity, media);

    var quick = health.Check();
    quick.DeepScan.Should().BeFalse();
    quick.IntegrityIssues.Should().BeEmpty("bit-rot with unchanged metadata is only findable by re-checksumming — that is the deep scan's job");

    var deep = health.Check(deep: true);
    deep.DeepScan.Should().BeTrue();
    deep.IntegrityIssues.Should().ContainSingle(i => i.Kind == IntegrityIssueKind.BitRotDetected && i.Path == "rot.bin");
    deep.Healthy.Should().BeFalse();
    this._v1.GetContent("rot.bin", false)![0].Should().Be(99, "a health CHECK never mutates the pool — only fix repairs");

    var fixedReport = health.CheckAndCorrect();
    fixedReport.IntegrityIssues.Should().Contain(i => i.Kind == IntegrityIssueKind.BitRotRepaired);
    this._v1.GetContent("rot.bin", false).Should().Equal(new byte[] { 5, 5, 5 }, "fix repaired the rot from the checksum-verified copy");
  }

  [Test]
  [Category("HappyPath")]
  public void Check_GivenSizeMismatchAcrossCopies_WhenDefaultScan_ThenInconsistencyFound() {
    // an inconsistent file (copies deviate in size) must show up in the CHEAP scan already
    this._v1.Seed("f.bin", false, [1, 2, 3]);
    this._v2.Seed("f.bin", true, [1, 2]);

    var report = this._Health(2).Check();

    report.IntegrityIssues.Should().NotBeEmpty("copies of different length are inconsistent — no deep scan needed to see that");
    this._v2.GetContent("f.bin", true).Should().Equal(new byte[] { 1, 2 }, "the check reported without touching anything");
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

/// <summary>
/// The background SMART sampler. Its job is to keep an expensive, slow-moving query OFF the
/// once-a-second metrics path, so what matters is that it samples rarely, never runs two sweeps at
/// once, and never lets a misbehaving drive reach its caller as an exception.
/// </summary>
[TestFixture]
[Category("Unit")]
public class MemberSmartCacheTests {

  private sealed class CountingMonitor(Func<string, SmartStatus>? answer = null) : ISmartMonitor {
    private int _queries;
    public int Queries => Volatile.Read(ref this._queries);
    public bool IsSupported => true;

    public SmartStatus Query(string device) {
      Interlocked.Increment(ref this._queries);
      return answer?.Invoke(device) ?? new(device, DiskHealth.Healthy, 30, 0, 0, 100, "Fake", "SMART passed");
    }
  }

  private static FakeVolumeIO _Member(string name) => new(Guid.NewGuid(), name, "PHYS-" + name, capacity: 1 << 20);

  private static void _Settle(MemberSmartCache cache, int expected) {
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (cache.Current.Count < expected && DateTime.UtcNow < deadline)
      Thread.Sleep(20);
  }

  [Test]
  [Category("HappyPath")]
  public void Refresh_GivenOnlineMembers_WhenSwept_ThenEachIsSampledOnce() {
    var monitor = new CountingMonitor();
    var cache = new MemberSmartCache(monitor, TimeSpan.FromMinutes(5));
    var members = new IVolumeIO[] { _Member("a"), _Member("b") };

    cache.RefreshInBackground(members, m => m.PhysicalVolumeId);
    _Settle(cache, 2);

    cache.Current.Should().HaveCount(2);
    monitor.Queries.Should().Be(2, "one query per member per sweep");
  }

  [Test]
  [Category("EdgeCase")]
  public void Refresh_GivenItIsCalledEverySecond_ThenItDoesNotQueryEveryTime() {
    // the whole point: the metrics snapshot publishes once a second, and smartctl is a process
    // launch that may take ten. Sampling on every publish would put one fork per member per second
    // in front of the timer the drain, the heal and the unmount request all share.
    var monitor = new CountingMonitor();
    var cache = new MemberSmartCache(monitor, TimeSpan.FromMinutes(5));
    var members = new IVolumeIO[] { _Member("a") };

    for (var tick = 0; tick < 50; ++tick)
      cache.RefreshInBackground(members, m => m.PhysicalVolumeId);

    _Settle(cache, 1);
    Thread.Sleep(100);
    monitor.Queries.Should().Be(1, "fifty ticks inside one interval are one sweep, not fifty");
  }

  [Test]
  [Category("EdgeCase")]
  public void Refresh_GivenAMemberIsOffline_ThenItIsNotQueriedAndItsStaleReadingIsDropped() {
    // asking a disk that is gone means blocking on a device that will not answer, and the stale
    // reading it left behind would otherwise be painted as its current health
    var monitor = new CountingMonitor();
    var cache = new MemberSmartCache(monitor, TimeSpan.Zero);
    var member = _Member("a");
    var members = new IVolumeIO[] { member };

    cache.RefreshInBackground(members, m => m.PhysicalVolumeId);
    _Settle(cache, 1);
    cache.Current.Should().ContainKey(member.MemberId);

    member.IsOnline = false;
    cache.RefreshInBackground(members, m => m.PhysicalVolumeId);

    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (cache.Current.Count > 0 && DateTime.UtcNow < deadline)
      Thread.Sleep(20);

    cache.Current.Should().NotContainKey(member.MemberId,
      "a member that is not there has nothing to say about its health");
  }

  [Test]
  [Category("Exception")]
  public void Refresh_GivenTheMonitorThrows_ThenTheFailureIsRecordedRatherThanEscaping() {
    var monitor = new CountingMonitor(_ => throw new InvalidOperationException("smartctl exploded"));
    var cache = new MemberSmartCache(monitor, TimeSpan.Zero);
    var member = _Member("a");

    var act = () => cache.RefreshInBackground([member], m => m.PhysicalVolumeId);
    act.Should().NotThrow("health sampling must never perturb the pool it is watching");

    _Settle(cache, 1);
    cache.Current[member.MemberId].Health.Should().Be(DiskHealth.Unknown,
      "a drive that could not be queried is unknown, never unhealthy");
  }

}
