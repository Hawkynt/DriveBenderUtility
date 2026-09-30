using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Scheduled snapshots (<c>snapshots.schedule</c>, docs/Snapshots.md): off by default; when set, the
/// mounted pool's background pump takes one every interval, named after the moment, and keeps the
/// newest few. Driven here by a hand-moved clock.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SnapshotScheduleTests {

  private static readonly Guid _pool = Guid.Parse("5c4ed000-0000-0000-0000-000000000005");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;
  private DateTime _now;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 22);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 22);
    this._now = new(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc);
  }

  private PoolFileSystem _Mounted(string snapshots, bool readOnly = false) {
    var cache = new CacheInstance("sc" + Guid.NewGuid().ToString("N"),
      new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._a), new(this._b)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 1, "snapshots": {{snapshots}} }"""), clock: () => this._now);
    fs.Mount(new(@"X:\") { ReadOnly = readOnly });
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static string[] _Scheduled(PoolFileSystem fs)
    => [.. fs.ListSnapshots().Where(s => s.Name.StartsWith(PoolFileSystem.ScheduledSnapshotPrefix, StringComparison.Ordinal)).Select(s => s.Name)];

  private const string _EVERY_SIX_HOURS = """{ "schedule": { "every": "6h", "keep": 3 } }""";

  #region taking

  [Test]
  [Category("HappyPath")]
  public void Pump_GivenAScheduleAndNoScheduledSnapshotYet_ThenOneIsTakenNamedAfterTheMoment() {
    using var fs = this._Mounted(_EVERY_SIX_HOURS);
    _Write(fs, "a.txt", [1]);

    fs.CreateScheduler().Quiesce();

    _Scheduled(fs).Should().Equal("auto-20260930-060000");
    fs.Snapshots.PathsIn(fs.ListSnapshots().Single().Id).Should().Contain("a.txt", "it is an ordinary snapshot of the pool");
  }

  [Test]
  [Category("HappyPath")]
  public void Pump_GivenNoSchedule_ThenNoSnapshotIsEverTaken() {
    // off by default: nothing in the built-in configuration takes a snapshot by itself
    using var fs = this._Mounted("""{ "reserve": "10%" }""");
    var scheduler = fs.CreateScheduler();

    for (var day = 0; day < 10; ++day) {
      this._now += TimeSpan.FromDays(1);
      scheduler.Quiesce();
    }

    fs.ListSnapshots().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void Pump_GivenTheScheduleIsOff_ThenNoSnapshotIsTaken() {
    using var fs = this._Mounted("""{ "schedule": { "every": "off", "keep": 3 } }""");
    this._now += TimeSpan.FromDays(3);

    fs.CreateScheduler().Quiesce();

    fs.ListSnapshots().Should().BeEmpty();
  }

  [TestCase(5 * 60 + 59, 1, TestName = "Pump_GivenOneMinuteShortOfTheInterval_ThenNoSecondSnapshotIsTaken")]
  [TestCase(6 * 60, 2, TestName = "Pump_GivenExactlyTheInterval_ThenTheSecondSnapshotIsTaken")]
  [TestCase(6 * 60 + 1, 2, TestName = "Pump_GivenJustPastTheInterval_ThenTheSecondSnapshotIsTaken")]
  [Category("EdgeCase")]
  public void Pump_GivenTimeHasPassed_ThenASnapshotIsTakenOnlyOnceTheIntervalHasElapsed(int minutesLater, int expected) {
    using var fs = this._Mounted(_EVERY_SIX_HOURS);
    var scheduler = fs.CreateScheduler();
    scheduler.Quiesce();

    this._now += TimeSpan.FromMinutes(minutesLater);
    scheduler.Quiesce();

    _Scheduled(fs).Should().HaveCount(expected);
  }

  [Test]
  [Category("HappyPath")]
  public void Pump_GivenManyIntervals_ThenOnlyTheNewestKeptCountSurviveAndHandTakenOnesAreUntouched() {
    using var fs = this._Mounted(_EVERY_SIX_HOURS);
    var scheduler = fs.CreateScheduler();
    var manual = fs.TakeSnapshot("before-the-upgrade");

    for (var i = 0; i < 5; ++i) {
      scheduler.Quiesce();
      this._now += TimeSpan.FromHours(6);
    }

    _Scheduled(fs).Should().Equal("auto-20260930-180000", "auto-20261001-000000", "auto-20261001-060000");
    fs.ListSnapshots().Should().Contain(s => s.Id == manual.Id, "the schedule drops only what the schedule took");
  }

  [Test]
  [Category("EdgeCase")]
  public void Pump_GivenARemountWithinTheInterval_ThenNoExtraSnapshotIsTaken() {
    using (var first = this._Mounted(_EVERY_SIX_HOURS))
      first.CreateScheduler().Quiesce();

    this._now += TimeSpan.FromHours(1);
    using var second = this._Mounted(_EVERY_SIX_HOURS);
    var scheduler = second.CreateScheduler();
    scheduler.Quiesce();
    _Scheduled(second).Should().HaveCount(1, "the last scheduled snapshot is on disk, and it is only an hour old");

    this._now += TimeSpan.FromHours(5);
    scheduler.Quiesce();
    _Scheduled(second).Should().HaveCount(2);
  }

  [Test]
  [Category("EdgeCase")]
  public void Pump_GivenTheClockWasSteppedBackwards_ThenTheScheduleDoesNotStopUntilItCatchesUp() {
    using var fs = this._Mounted(_EVERY_SIX_HOURS);
    var scheduler = fs.CreateScheduler();
    scheduler.Quiesce(); // at 06:00

    using var reopened = this._Mounted(_EVERY_SIX_HOURS);
    this._now -= TimeSpan.FromDays(2); // the RTC was wrong; the snapshot above now looks two days in the future
    var again = reopened.CreateScheduler();
    again.Quiesce();

    _Scheduled(reopened).Should().HaveCount(2, "a future-dated snapshot must not hold the schedule off for two days");
    this._now += TimeSpan.FromHours(1);
    again.Quiesce();
    _Scheduled(reopened).Should().HaveCount(2, "and the one just taken counts from now");
  }

  [Test]
  [Category("EdgeCase")]
  public void Pump_GivenAReadOnlyMount_ThenNoSnapshotIsTaken() {
    using var fs = this._Mounted(_EVERY_SIX_HOURS, readOnly: true);

    fs.CreateScheduler().Quiesce();

    fs.ListSnapshots().Should().BeEmpty();
  }

  [Test]
  [Category("HappyPath")]
  public void Reload_GivenAScheduleTurnedOnWhileMounted_ThenTheNextPumpTakesOne() {
    using var fs = this._Mounted("""{ "reserve": "10%" }""");
    var scheduler = fs.CreateScheduler();
    scheduler.Quiesce();
    fs.ListSnapshots().Should().BeEmpty();

    fs.ReloadConfig(ConfigResolver.ResolveEffective(null, """{ "duplication": 1, "snapshots": { "schedule": { "every": "1d" } } }"""));
    scheduler.Quiesce();

    _Scheduled(fs).Should().ContainSingle();
  }

  [Test]
  [Category("EdgeCase")]
  public void Pump_GivenNoKeepCount_ThenSevenAreKept() {
    using var fs = this._Mounted("""{ "schedule": { "every": "1d" } }""");
    var scheduler = fs.CreateScheduler();

    for (var day = 0; day < 9; ++day) {
      scheduler.Quiesce();
      this._now += TimeSpan.FromDays(1);
    }

    _Scheduled(fs).Should().HaveCount(7);
  }

  #endregion

  #region the reserve

  [Test]
  [Category("Exception")]
  public void Pump_GivenTheStoreIsOverItsReserveAndThePolicyRefuses_ThenTheSnapshotIsSkippedAndRetriedLater() {
    using var fs = this._Mounted("""{ "reserve": "1024", "onReserveFull": "refuse", "schedule": { "every": "6h", "keep": 3 } }""");
    _Write(fs, "big.bin", new byte[3000]);
    var scheduler = fs.CreateScheduler();
    scheduler.Quiesce(); // the first scheduled snapshot
    _Write(fs, "big.bin", [1]); // preserves 3000 bytes: the store is over its reserve

    this._now += TimeSpan.FromHours(6);
    var pumped = () => scheduler.Quiesce();
    pumped.Should().NotThrow("a refused snapshot is the policy working, not a failure of the pump");
    _Scheduled(fs).Should().HaveCount(1, "refuse means refuse, for the schedule as for a person");

    fs.DeleteSnapshot(fs.ListSnapshots().Single().Id); // the operator makes room
    this._now += TimeSpan.FromMinutes(30);
    scheduler.Quiesce();
    _Scheduled(fs).Should().BeEmpty("the retry waits, rather than trying on every pump");

    this._now += TimeSpan.FromMinutes(31);
    scheduler.Quiesce();
    _Scheduled(fs).Should().ContainSingle("and then it is taken");
  }

  #endregion

  #region names and the clock

  [TestCase("", TestName = "Take_GivenAnEmptyName_ThenItIsRefused")]
  [TestCase("   ", TestName = "Take_GivenAWhitespaceName_ThenItIsRefused")]
  [TestCase("a/b", TestName = "Take_GivenANameWithASlash_ThenItIsRefused")]
  [TestCase("a\\b", TestName = "Take_GivenANameWithABackslash_ThenItIsRefused")]
  [TestCase("..", TestName = "Take_GivenTheParentFolderName_ThenItIsRefused")]
  [Category("Exception")]
  public void Take_GivenANameThatCannotBeAFolder_ThenItIsRefused(string name) {
    using var fs = this._Mounted("""{ "reserve": "10%" }""");

    var take = () => fs.TakeSnapshot(name);

    take.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.InvalidArgument);
    fs.ListSnapshots().Should().BeEmpty();
  }

  [Test]
  [Category("Exception")]
  public void Take_GivenANameAlreadyInUse_ThenItIsRefused() {
    // the view finds a snapshot by name, case-insensitively: a second one of that name was unreachable
    using var fs = this._Mounted("""{ "reserve": "10%" }""");
    fs.TakeSnapshot("Nightly");

    var take = () => fs.TakeSnapshot("nightly");

    take.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
    fs.ListSnapshots().Should().ContainSingle();
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenAClockThatDoesNotMove_ThenTheVersionStillBelongsToTheSnapshot() {
    // A snapshot and the aside right after it compared by instant; on a clock that did not move
    // between them they were EQUAL, the version was not "after" the snapshot, and the snapshot
    // reported its content lost.
    using var fs = this._Mounted("""{ "reserve": "10%" }""");
    _Write(fs, "f.txt", [1, 2, 3]);
    var taken = fs.TakeSnapshot("frozen");

    _Write(fs, "f.txt", [9]);

    var version = fs.Snapshots.Resolve(taken.Id, "f.txt");
    version.Should().NotBeNull("the version set aside after the snapshot belongs to it, whatever the clock read");
    version!.Value.member.GetContentOf(version.Value.versionPath).Should().Equal(new byte[] { 1, 2, 3 });
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenTheClockSteppedBackwardsAcrossARemount_ThenTheVersionStillBelongsToTheSnapshot() {
    using (var fs = this._Mounted("""{ "reserve": "10%" }""")) {
      _Write(fs, "f.txt", [1, 2, 3]);
      fs.TakeSnapshot("before");
    }

    this._now -= TimeSpan.FromHours(3);
    using var reopened = this._Mounted("""{ "reserve": "10%" }""");
    _Write(reopened, "f.txt", [9]);

    var taken = reopened.ListSnapshots().Single();
    reopened.Snapshots.Resolve(taken.Id, "f.txt").Should().NotBeNull(
      "a write after the snapshot is after it, even when the clock says three hours earlier");
  }

  #endregion

  #region configuration

  [TestCase("abc", TestName = "Validate_GivenAScheduleIntervalThatIsNotADuration_ThenItIsRejected")]
  [TestCase("0s", TestName = "Validate_GivenAZeroScheduleInterval_ThenItIsRejected")]
  [TestCase("59s", TestName = "Validate_GivenAScheduleIntervalJustUnderAMinute_ThenItIsRejected")]
  [TestCase("-1h", TestName = "Validate_GivenANegativeScheduleInterval_ThenItIsRejected")]
  [Category("Exception")]
  public void Validate_GivenABadScheduleInterval_ThenItIsRejected(string every) {
    var config = ConfigResolver.ResolveEffective(null, $$"""{ "snapshots": { "schedule": { "every": "{{every}}" } } }""");

    var validate = () => ConfigValidator.Validate(config);

    validate.Should().Throw<ConfigValidationException>().WithMessage("*snapshots.schedule.every*");
  }

  [TestCase("1m", TestName = "Validate_GivenAScheduleIntervalOfExactlyAMinute_ThenItIsAccepted")]
  [TestCase("6h", TestName = "Validate_GivenAScheduleIntervalOfSixHours_ThenItIsAccepted")]
  [TestCase("off", TestName = "Validate_GivenTheScheduleOff_ThenItIsAccepted")]
  [TestCase("OFF", TestName = "Validate_GivenTheScheduleOffInCapitals_ThenItIsAccepted")]
  [Category("HappyPath")]
  public void Validate_GivenAGoodScheduleInterval_ThenItIsAccepted(string every) {
    var config = ConfigResolver.ResolveEffective(null, $$"""{ "snapshots": { "schedule": { "every": "{{every}}" } } }""");

    var validate = () => ConfigValidator.Validate(config);

    validate.Should().NotThrow();
  }

  [TestCase(0, false, TestName = "Validate_GivenAKeepCountOfZero_ThenItIsRejected")]
  [TestCase(-3, false, TestName = "Validate_GivenANegativeKeepCount_ThenItIsRejected")]
  [TestCase(1, true, TestName = "Validate_GivenAKeepCountOfOne_ThenItIsAccepted")]
  [Category("EdgeCase")]
  public void Validate_GivenAKeepCount_ThenOnlyAtLeastOneIsAccepted(int keep, bool accepted) {
    var config = ConfigResolver.ResolveEffective(null, $$"""{ "snapshots": { "schedule": { "every": "1d", "keep": {{keep}} } } }""");

    var validate = () => ConfigValidator.Validate(config);

    if (accepted)
      validate.Should().NotThrow();
    else
      validate.Should().Throw<ConfigValidationException>().WithMessage("*snapshots.schedule.keep*");
  }

  [Test]
  [Category("Exception")]
  public void Validate_GivenAReserveThatIsNotASize_ThenItIsRejected() {
    var config = ConfigResolver.ResolveEffective(null, """{ "snapshots": { "reserve": "lots" } }""");

    var validate = () => ConfigValidator.Validate(config);

    validate.Should().Throw<ConfigValidationException>().WithMessage("*snapshots.reserve*");
  }

  [Test]
  [Category("HappyPath")]
  public void Defaults_GivenNoSnapshotSettings_ThenTheScheduleIsOffAndKeepsSeven() {
    var config = ConfigResolver.ResolveEffective(null, null);

    config.Snapshots!.Schedule!.Interval.Should().Be(TimeSpan.Zero, "scheduled snapshots are off unless asked for");
    config.Snapshots.Schedule.Keep.Should().Be(7);
  }

  #endregion

}

internal static class _SnapshotScheduleTestExtensions {

  public static byte[] GetContentOf(this IVolumeIO member, string path) {
    using var stream = member.OpenRead(path, false);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

}
