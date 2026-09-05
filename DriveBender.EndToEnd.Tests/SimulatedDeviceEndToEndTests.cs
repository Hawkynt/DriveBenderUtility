using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Pools whose members sit on storage of KNOWN, unequal speed, built with the product's own
/// per-member rate limits (<c>maxIops</c>, <c>maxThroughput</c>).
///
/// A real SD card is the honest evidence and a poor experiment: a machine has whatever disks it has,
/// they differ between hosts, their measured rate wanders with whatever else is running, and CI has
/// one disk. A rate limit is the same on every machine and every run, models hardware nobody has to
/// hand, and — being the product's own feature — is worth exercising for its own sake. The real
/// devices are covered next door in <see cref="HeterogeneousDeviceEndToEndTests"/>; these are the
/// repeatable half of the same question.
///
/// The first two scenarios are the PREMISE. Every other one here assumes a limited member is really
/// slower, and if that assumption were false they would all pass while measuring nothing — so the
/// limit is pinned from both sides before anything is built on it.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class SimulatedDeviceEndToEndTests {

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>
  /// A limit's token bucket holds ONE SECOND of credit, so a member that has been idle may burst
  /// that much before the limit is felt at all. A workload sized at the limit would therefore be
  /// entirely burst and measure nothing; this is the size that spends the burst and then spends four
  /// more seconds actually being limited.
  /// </summary>
  private const int _THROTTLE_SECONDS = 4;

  #region the premise: a limited member really is slower

  /// <summary>
  /// What this machine writes at through a real mount with NO limit, measured once.
  ///
  /// Every scenario in this file rests on a limit being slower than the machine. That is usually
  /// obvious and occasionally false: a loaded CI worker managed 7 MiB/s through the driver, which is
  /// below the 16 MiB/s limit the scenarios use — and on such a host a limited member is not slower
  /// than an unlimited one, so nothing here can be demonstrated and a failure would be reporting the
  /// runner. Measured through the mount rather than with a plain file write, because the fixed costs
  /// of the driver, the journal and the fsync are most of it at these sizes.
  /// </summary>
  private static readonly Lazy<double> _hostRate = new(() => {
    const int size = 32 * 1024 * 1024;
    using var pool = MountedPool.Create(members: 1, storageKinds: [StorageKind.Ram]);
    var content = _Payload(size, 900);
    var stopwatch = Stopwatch.StartNew();
    File.WriteAllBytes(pool.PathTo("hostrate.bin"), content);
    stopwatch.Stop();
    return stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : size / stopwatch.Elapsed.TotalSeconds;
  });

  /// <summary>
  /// The limit the two premise scenarios use — deliberately far below anything a machine that can
  /// run this suite at all will manage.
  ///
  /// It started as the SD-card profile's 16 MiB/s, and a CI worker that wrote at 7 MiB/s skipped
  /// them. That was the guard behaving correctly and the CHOICE OF LIMIT being wrong, and it cost
  /// more than a skip: whether these two run then depends on how fast the runner happens to be that
  /// day, so the generated coverage matrix flips between "pass" and "skipped" run to run, CI commits
  /// the regenerated matrix onto the branch each time, and the branch's checks never settle. A skip
  /// that varies with the weather is not a property of the test. At this rate the guard is a
  /// backstop for a pathological host rather than something that fires in normal service.
  /// </summary>
  private const long _PREMISE_LIMIT = 2 * 1024 * 1024;

  /// <summary>Skips unless this machine can outrun the limit a scenario is about to impose.</summary>
  private static void _RequireTheHostOutrunsTheLimit(long limit) {
    var host = _hostRate.Value;
    if (host > 0 && host < limit * 2)
      Assert.Ignore(
        $"this host writes at about {host / (1024 * 1024):F0} MiB/s through a mount, against a limit "
        + $"of {limit / (1024 * 1024)} MiB/s — a limited member is not measurably slower than an "
        + "unlimited one here, so nothing about the limit can be shown.");
  }

  [Test]
  [Category("Performance")]
  [Description("A member the manifest limits to a byte rate really is held to it through a real mount, rather than the limit being decoration.")]
  public void Throttle_GivenAMemberLimitedToAByteRate_ThenTheMountIsHeldToIt() {
    var limit = _PREMISE_LIMIT;
    _RequireTheHostOutrunsTheLimit(limit);

    var size = (int)(limit * (_THROTTLE_SECONDS + 1)); // one second of it is the bucket's burst

    using var pool = MountedPool.Create(members: 1,
      storageKinds: [new("premise (simulated)", null, 0, limit)]);
    var content = _Payload(size, 901);

    var stopwatch = Stopwatch.StartNew();
    File.WriteAllBytes(pool.PathTo("limited.bin"), content);
    stopwatch.Stop();

    // the burst is free, everything after it is not
    var chargeable = size - limit;
    var floor = TimeSpan.FromSeconds(chargeable / (double)limit * 0.6); // 40% of slack for the engine's own cost
    stopwatch.Elapsed.Should().BeGreaterThan(floor,
      $"a member limited to {limit / (1024 * 1024)} MiB/s must actually take about "
      + $"{chargeable / (double)limit:F1}s to absorb {chargeable / (1024 * 1024)} MiB beyond its burst, "
      + $"and took {stopwatch.Elapsed.TotalSeconds:F2}s. A limit that costs nothing is decoration, and "
      + $"every simulated-device scenario in this file would be measuring an unlimited disk."
      + $"{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("limited.bin")).Should().Equal(content,
      "being slow is not permission to be wrong");
  }

  [Test]
  [Category("Performance")]
  [Description("The same pool without the limit is far faster, so the limit is what the previous scenario measured and not the host.")]
  public void Throttle_GivenNoLimit_ThenTheSamePoolIsFarFaster() {
    var limit = _PREMISE_LIMIT;
    _RequireTheHostOutrunsTheLimit(limit);

    var host = _hostRate.Value;
    TestContext.Out.WriteLine($"[throttle] this host writes at {host / (1024 * 1024):F0} MiB/s unlimited, "
                              + $"against a {limit / (1024 * 1024)} MiB/s limit.");

    host.Should().BeGreaterThan(limit * 2,
      "the limited scenario is only evidence if the SAME work is much faster unlimited — and the "
      + "guard above has already skipped the hosts where it is not");
  }

  [Test]
  [Category("Performance")]
  [Description("Starving the pool's own background copying does not starve the application: writes stay fast while the drain crawls.")]
  public void Limits_GivenBackgroundIsStarvedOnTheLandingZone_ThenTheApplicationIsNotHeldToIt() {
    // The whole reason the three kinds are separated (§6.4). An operator holds background work down
    // to leave a disk usable for everything else; if that allowance leaked onto the write path the
    // setting would be worse than useless, because the disk they were protecting would be the one
    // that stopped responding.
    const long starved = 64 * 1024; // per second — a rate at which the drain gets nowhere
    const int files = 20;
    const int size = 1024 * 1024;

    using var pool = MountedPool.CreateTiered();
    DbMount.SetMemberThroughput(pool.PoolName, pool.MemberPaths[0], background: starved);
    DbMount.RequestLiveReload(pool.PoolName);
    Thread.Sleep(2500); // the pump consumes the reload on its next tick

    var written = new Dictionary<string, byte[]>();
    var stopwatch = Stopwatch.StartNew();
    for (var index = 0; index < files; ++index) {
      var content = _Payload(size, 950 + index);
      File.WriteAllBytes(pool.PathTo($"burst-{index}.bin"), content);
      written[$"burst-{index}.bin"] = content;
    }

    stopwatch.Stop();

    // An ABSOLUTE bound, not a ratio against an unthrottled run. Twenty megabytes at the starved
    // rate would be five and a half minutes; anything under a minute cannot have been paid for out
    // of that bucket on any host, so this catches the leak without betting on the machine's speed.
    var atTheStarvedRate = TimeSpan.FromSeconds(files * (double)size / starved);
    stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60),
      $"{files} MiB of application writes took {stopwatch.Elapsed.TotalSeconds:F1}s while background "
      + $"work was held to {starved / 1024} KiB/s — at that rate the same bytes would need "
      + $"{atTheStarvedRate.TotalSeconds:F0}s, so the background limit has leaked onto the write path "
      + $"and is throttling the very application it exists to protect."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    foreach (var (name, content) in written)
      File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content,
        $"'{name}' was acknowledged while the drain was starved, and must still read back whole."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file stays readable at full speed while the pool is relocating it, even when that relocation is throttled to a crawl.")]
  public void Read_GivenTheFileIsMidDrain_ThenItIsServedAtOnceRatherThanAtTheDrainsPace() {
    // Tiering is supposed to be transparent. It was not: the drainer held the path's WRITE lease
    // across its whole copy, and a foreground read takes a READ lease on the same path — so every
    // read of a file blocked for exactly as long as the pool took to relocate it. Invisible while a
    // drain is milliseconds, and an outage once it is not: eight megabytes down a member held to
    // 64 KiB/s made the file unreadable through the mount for over two minutes.
    //
    // Measured both ways on clean builds: 126.8s with the lease held across the copy, 0.0s with it
    // released. Deterministic, not a flake.
    const long starved = 64 * 1024;

    using var pool = MountedPool.CreateTiered();
    DbMount.SetMemberThroughput(pool.PoolName, pool.MemberPaths[0], background: starved);
    DbMount.RequestLiveReload(pool.PoolName);
    Thread.Sleep(2500);

    var content = _Payload(8 * 1024 * 1024, 980);
    File.WriteAllBytes(pool.PathTo("relocating.bin"), content);

    // the read has to happen while the copy is genuinely in flight, or it proves nothing
    MountedPool.WaitUntil(() => pool.StagingFiles().Count > 0, TimeSpan.FromMinutes(2)).Should().BeTrue(
      $"the drain must be under way for a read to race it.{Environment.NewLine}{pool.DescribeMembers()}");

    var stopwatch = Stopwatch.StartNew();
    var served = File.ReadAllBytes(pool.PathTo("relocating.bin"));
    stopwatch.Stop();
    TestContext.Out.WriteLine($"[tiering] read of a file mid-drain took {stopwatch.Elapsed.TotalSeconds:F1}s");

    served.Should().Equal(content, "and it is the file, not a half-copied one");
    stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
      $"reading a file the pool happens to be moving took {stopwatch.Elapsed.TotalSeconds:F0}s — at the "
      + $"drain's {starved / 1024} KiB/s the whole copy is {8 * 1024 / (starved / 1024)}s, so the read is "
      + $"waiting on the relocation instead of being served beside it."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A pool whose background work is throttled to a crawl still unmounts cleanly, instead of having to be killed.")]
  public void Unmount_GivenBackgroundWorkIsStarved_ThenThePoolStillComesDownCleanly() {
    // Throttling a disk must not cost the ability to shut down. It did: the stop request was checked
    // AFTER the background pump in the same non-reentrant tick, and one unit of that pump is a whole
    // file — so a drain held to 64 KiB/s meant the unmount was not noticed for minutes. The verb
    // waited twenty seconds, reported the mount still active, and the process had to be killed,
    // which throws away the clean shutdown and makes the next mount replay the journal.
    const long starved = 64 * 1024;

    using var pool = MountedPool.CreateTiered();
    DbMount.SetMemberThroughput(pool.PoolName, pool.MemberPaths[0], background: starved);
    DbMount.RequestLiveReload(pool.PoolName);
    Thread.Sleep(2500);

    var content = _Payload(8 * 1024 * 1024, 970);
    File.WriteAllBytes(pool.PathTo("pending.bin"), content);

    // enough to guarantee the drainer is mid-copy, which is the state that used to wedge the unmount
    Thread.Sleep(3000);

    // WhileUnmounted runs the real `dbmount unmount` verb and throws if it is refused, so this is
    // the assertion; the timing only says it did not merely scrape in.
    var stopwatch = Stopwatch.StartNew();
    Action unmountCleanly = () => pool.WhileUnmounted(() => { });
    unmountCleanly.Should().NotThrow(
      $"a member's rate limit governs how fast the pool may copy, not whether it may stop."
      + $"{Environment.NewLine}{pool.MountLog}");

    stopwatch.Stop();
    TestContext.Out.WriteLine($"[unmount] clean shutdown with background starved took {stopwatch.Elapsed.TotalSeconds:F1}s");
    stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(45),
      $"the unmount took {stopwatch.Elapsed.TotalSeconds:F0}s with background work starved — it must "
      + $"abandon the copy it has not finished (journalled, and resumed on the next mount) rather "
      + $"than wait it out at {starved / 1024} KiB/s.{Environment.NewLine}{pool.MountLog}");

    var readBack = Stopwatch.StartNew();
    var served = File.ReadAllBytes(pool.PathTo("pending.bin"));
    readBack.Stop();
    TestContext.Out.WriteLine($"[unmount] reading the file back after the abandoned drain took {readBack.Elapsed.TotalSeconds:F1}s");
    served.Should().Equal(content, "and coming down without finishing the drain must not cost the file");
  }

  #endregion

  #region tiering across storage that is genuinely unequal

  /// <summary>
  /// Skips unless the landing zone actually has room to be one.
  ///
  /// <c>ChoosePrimaryTarget</c> offers a new file to the fast tier only while that tier is below its
  /// own low watermark (75% by default) and falls back to capacity otherwise — deliberately, because
  /// filling a nearly-full fast tier is how a landing zone stops working. So on a host whose disk is
  /// already past that mark the pool declines to use it, every tiering claim below is false, and
  /// nothing is wrong. That is exactly what happens on a CI worker, whose system disk runs at over
  /// 80% used, and it is why the burst there lands on the capacity tier.
  /// </summary>
  private static void _RequireTheFastTierHasRoom(StorageKind fast) {
    var where = fast.Path ?? Path.GetTempPath();
    double used;
    try {
      var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(where)! : where);
      if (drive.TotalSize <= 0)
        return; // cannot tell; let the scenario run rather than skip on a failed probe

      used = 1.0 - drive.AvailableFreeSpace / (double)drive.TotalSize;
    } catch (Exception) {
      return;
    }

    const double lowWatermark = 0.75; // tiers.fast.lowWatermark
    if (used > lowWatermark)
      Assert.Ignore(
        $"the landing zone would sit on '{where}', which is {used * 100:F0}% used — past the fast "
        + $"tier's {lowWatermark * 100:F0}% low watermark, so the pool correctly refuses to place new "
        + "data there and no tiering claim can be demonstrated on this host.");
  }


  /// <summary>Fast tier in front of slow capacity, across the range of speeds a real pool meets.</summary>
  private static IEnumerable<TestCaseData> _TieringPairs() {
    yield return new TestCaseData(StorageKind.Ram, StorageKind.SimulatedSdCard).SetName("{m}(RAM over SD card)");
    yield return new TestCaseData(StorageKind.Ram, StorageKind.SimulatedCloud).SetName("{m}(RAM over cloud)");
    yield return new TestCaseData(StorageKind.SimulatedSsd, StorageKind.SimulatedHardDisk).SetName("{m}(SSD over HDD)");
    yield return new TestCaseData(StorageKind.SimulatedSsd, StorageKind.SimulatedSdCard).SetName("{m}(SSD over SD card)");
    yield return new TestCaseData(StorageKind.SimulatedHardDisk, StorageKind.SimulatedCloud).SetName("{m}(HDD over cloud)");
  }

  [TestCaseSource(nameof(_TieringPairs))]
  [Category("Performance")]
  [Explicit("Benchmark: a 32 MiB burst takes a second or two, and at that size the driver, the "
            + "journal and the fsync are most of it — so on a shared runner the number says more "
            + "about the machine than the tier. Measured there across several runs it read 115, 46 "
            + "and 17 MiB/s against controls of 30, 30 and 23; the last of those puts the tiered "
            + "pool BELOW a single slow member, which is not a thing the pool did. Run it "
            + "deliberately on a quiet machine and read the printed numbers. The half of this claim "
            + "that needs no clock — that new data lands on the fast tier — is next door and stays "
            + "in the battery.")]
  [Description("Prices a write burst absorbed through a landing zone against the same burst written straight to the capacity tier.")]
  public void Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstRunsAtTheFastTiersPace(
    StorageKind fast, StorageKind slow) {
    // The claim needs headroom to be visible. Against a capacity tier at 140 MiB/s, a landing zone
    // capped at 400 MiB/s leaves a spread of under 3x, and the engine's own cost plus the drain now
    // running concurrently eats enough of that to put the measurement below any bar worth setting —
    // it read 186 MiB/s against the capacity tier's 140, which IS faster and is not convincingly so.
    // Skipping is the honest answer; lowering the bar until this pairing passed would have made the
    // scenario agree with itself on every pairing while distinguishing nothing.
    // The landing zone has to have room to BE one; see _RequireTheFastTierHasRoom. This replaced a
    // blanket hold-back on Windows, which was the wrong diagnosis: the burst was landing on the
    // capacity tier there because the runner's system disk sits past the fast tier's low watermark
    // and the pool was correctly declining to fill it, not because tiering is broken on that
    // platform. The companion scenario above is what settled it, by asking where the data went
    // instead of how fast it got there.
    _RequireTheFastTierHasRoom(fast);

    const double neededSpread = 4.0;
    if (fast.MaxThroughput > 0 && fast.MaxThroughput < slow.MaxThroughput * neededSpread)
      Assert.Ignore(
        $"{fast} is only {fast.MaxThroughput / (double)slow.MaxThroughput:F1}x {slow}; a tier that "
        + $"absorbs at the fast tier's pace cannot be told apart from one that does not without at "
        + $"least {neededSpread:F0}x between them.");

    // sized against the SLOW tier: big enough that writing through to it could not keep up
    var size = (int)(slow.MaxThroughput * 2);
    var content = _Payload(size, 903);

    // The CONTROL is the same burst written to a pool that is only the slow tier. Comparing against
    // an absolute byte rate instead looked reasonable and was not: at these sizes the fixed costs of
    // a write through the driver — the journal, the staged temp, an fsync per copy — are a large
    // part of the time, so on a slower machine the tiered pool missed a bar it should clear while
    // nothing was wrong with it. Both sides of this comparison pay those costs.
    double throughTheSlowTier;
    using (var control = MountedPool.Create(members: 1, storageKinds: [slow])) {
      var clock = Stopwatch.StartNew();
      File.WriteAllBytes(control.PathTo("burst.bin"), content);
      clock.Stop();
      throughTheSlowTier = size / clock.Elapsed.TotalSeconds;
    }

    using var pool = MountedPool.Create(members: 2, landingZones: 1, storageKinds: [fast, slow]);

    var stopwatch = Stopwatch.StartNew();
    File.WriteAllBytes(pool.PathTo("burst.bin"), content);
    stopwatch.Stop();

    var rate = size / stopwatch.Elapsed.TotalSeconds;
    TestContext.Out.WriteLine(
      $"[tiering] {fast} over {slow}: {rate / (1024 * 1024):F1} MiB/s through the landing zone, "
      + $"{throughTheSlowTier / (1024 * 1024):F1} MiB/s writing straight to the capacity tier.");

    // Measurably faster, not twice as fast. Doubling assumes the HOST has that much headroom over
    // the slow tier's limit, and it often does not: a runner that manages 46 MiB/s through the
    // driver cannot be twice a control that is already running at 30, and the pool is behaving
    // perfectly when it reaches the host's ceiling. A quarter again is far outside run-to-run noise
    // for a burst this size and is the strongest claim the measurement actually supports.
    rate.Should().BeGreaterThan(throughTheSlowTier * 1.25,
      $"a landing zone exists so the slow tier is BEHIND the write rather than in it: {fast} over "
      + $"{slow} absorbed {size / (1024 * 1024)} MiB at {rate / (1024 * 1024):F1} MiB/s, against "
      + $"{throughTheSlowTier / (1024 * 1024):F1} MiB/s for the same burst written straight to "
      + $"{slow} on this machine.{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("burst.bin")).Should().Equal(content,
      "absorbing a burst quickly is worthless if the bytes are not the user's bytes");
  }

  [TestCaseSource(nameof(_TieringPairs))]
  [Category("EdgeCase")]
  [Description("A burst written to a tiered pool lands on the FAST tier, which is the timing-free half of what a landing zone promises.")]
  public void Tiering_GivenAFastLandingZoneOverSlowCapacity_ThenTheBurstLandsOnTheFastTier(
    StorageKind fast, StorageKind slow) {
    // The rate half of this claim is held back on Windows, where a tiered pool absorbs at its
    // capacity tier's pace. This is the half that needs no clock, and it runs everywhere — so it
    // says which of the two possible causes is at work. If the burst is NOT on the fast tier, the
    // fault is in placement; if it IS, placement is fine and whatever paces the write sits
    // downstream of it. Either answer narrows the search, and neither depends on how fast the
    // machine happens to be.
    _RequireTheFastTierHasRoom(fast);

    using var pool = MountedPool.Create(members: 2, landingZones: 1, storageKinds: [fast, slow]);

    var size = (int)Math.Min(slow.MaxThroughput * 2, 16 * 1024 * 1024);
    var content = _Payload(size, 913);
    File.WriteAllBytes(pool.PathTo("lands.bin"), content);

    // read back through the mount first: a placement question is only interesting if the data is right
    File.ReadAllBytes(pool.PathTo("lands.bin")).Should().Equal(content, "the burst must round-trip");

    // where it physically is, RIGHT NOW — before the drainer has had time to move it down
    var onFastTier = File.Exists(Path.Combine(pool.MemberPaths[0], "lands.bin"));
    var onCapacity = File.Exists(Path.Combine(pool.MemberPaths[1], "lands.bin"));
    TestContext.Out.WriteLine(
      $"[tiering] {fast} over {slow}: {size / (1024 * 1024)} MiB landed — fast tier: {onFastTier}, capacity: {onCapacity}");

    onFastTier.Should().BeTrue(
      $"new data goes to the landing zone and drains down later (FR-LZ-DRAIN); finding it on the "
      + $"capacity tier instead would mean placement never used the fast tier at all."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [TestCaseSource(nameof(_TieringPairs))]
  [Category("EdgeCase")]
  [Description("Everything the fast tier absorbed reaches the slow capacity tier byte-for-byte when the drainer moves it down.")]
  public void Tiering_WhenTheBurstDrainsToTheSlowTier_ThenEveryByteArrivesIntact(StorageKind fast, StorageKind slow) {
    using var pool = MountedPool.Create(members: 2, landingZones: 1, storageKinds: [fast, slow]);

    // small on purpose: this scenario is about the bytes arriving, and the drain is rate-limited
    var content = _Payload(1024 * 1024, 904);
    File.WriteAllBytes(pool.PathTo("drained.bin"), content);

    var drained = MountedPool.WaitUntil(
      () => File.Exists(Path.Combine(pool.MemberPaths[1], "drained.bin"))
            || File.Exists(Path.Combine(pool.MemberPaths[1], "FOLDER.DUPLICATE.$DRIVEBENDER", "drained.bin")),
      TimeSpan.FromMinutes(3));

    pool.IsMountAlive.Should().BeTrue($"nothing drains if the mount has died.{Environment.NewLine}{pool.MountLog}");
    drained.Should().BeTrue(
      $"a settled file must reach capacity storage even when that storage is slow ({slow})."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    // compared where they LANDED: a cache could serve the right answer over a short or torn copy
    var onSlowTier = pool.PhysicalCopies("drained.bin")
      .Where(c => c.where.StartsWith(pool.MemberPaths[1], StringComparison.Ordinal))
      .ToArray();

    onSlowTier.Should().NotBeEmpty("the copy that just drained has to be readable where it drained to");
    foreach (var (where, bytes) in onSlowTier)
      bytes.Should().Equal(content, $"the drained copy at {where} must be byte-identical to what was written");

    File.ReadAllBytes(pool.PathTo("drained.bin")).Should().Equal(content, "and the mount must still serve it");
  }

  #endregion

  #region a slow member must not become the pool's speed

  [TestCaseSource(nameof(_TieringPairs))]
  [Category("Performance")]
  [Description("With one copy on fast storage and one on slow, reads are served at the fast copy's pace rather than the slow one's.")]
  public void Duplication_GivenOneCopyOnEachSpeed_ThenReadsAreNotHeldToTheSlowOne(StorageKind fast, StorageKind slow) {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
      storageKinds: [fast, slow]);

    var size = (int)(slow.MaxThroughput * 2);
    var content = _Payload(size, 905);
    File.WriteAllBytes(pool.PathTo("mirrored.bin"), content);

    MountedPool.WaitUntil(() => pool.PhysicalCopies("mirrored.bin").Count >= 2, TimeSpan.FromMinutes(3))
      .Should().BeTrue(
        $"both copies must exist for this to be about choosing between them."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    // the pool's own cache would answer from RAM and prove nothing about either member
    pool.Remount();

    var stopwatch = Stopwatch.StartNew();
    var read = File.ReadAllBytes(pool.PathTo("mirrored.bin"));
    stopwatch.Stop();

    read.Should().Equal(content, "correctness first: whichever copy was used, it must be the right bytes");

    var rate = size / stopwatch.Elapsed.TotalSeconds;
    rate.Should().BeGreaterThan(slow.MaxThroughput * 1.5,
      $"a healthy fast copy sits beside the slow one, and a read that ignores it makes duplication a "
      + $"performance LIABILITY rather than protection: {fast} + {slow} served "
      + $"{size / (1024 * 1024)} MiB at {rate / (1024 * 1024):F0} MiB/s against the slow copy's own "
      + $"{slow.MaxThroughput / (1024 * 1024)} MiB/s.{Environment.NewLine}{pool.MountLog}");
  }

  #endregion

}
