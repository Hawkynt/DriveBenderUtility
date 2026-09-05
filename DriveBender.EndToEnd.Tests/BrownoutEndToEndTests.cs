using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// A member that goes SLOW instead of going away — the failure real storage actually produces.
///
/// Everything else in this suite breaks a member cleanly: the disk vanishes, or it errors on every
/// request. Both are easy for a pool to handle well, because both are unambiguous — the member is
/// out, and the engine routes around it once. A drive that is merely DYING does neither. It answers
/// every request correctly, eventually: a failing SSD whose controller is retrying internally, a
/// USB link that renegotiated to a slower mode, a NAS behind a saturated uplink, a disk being
/// scrubbed by something else. Nothing is wrong that a health check can see, and the only symptom is
/// that it takes a hundred times longer.
///
/// The question is what that costs the POOL. A pool that holds a second copy on healthy storage
/// should serve from it and stay fast; a pool that keeps waiting on the sick member turns one bad
/// disk into a bad pool, and duplication into a liability. The engine's own defences here are
/// load-aware placement and a readiness order that weighs measured latency — this asks whether they
/// actually bite, through a real mount.
///
/// The brownout is applied LIVE, without remounting, because that is how it happens.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class BrownoutEndToEndTests {

  private const long _MIB = 1024 * 1024;

  /// <summary>What a collapsed member is held to: slow enough that serving from it is unmistakable.</summary>
  private const long _COLLAPSED_THROUGHPUT = 1 * _MIB;

  private const int _COLLAPSED_IOPS = 12;

  /// <summary>
  /// A small pool cache, so a read of the working set actually reaches the members.
  ///
  /// The default global cache is 4 GiB and would answer the whole scenario out of RAM, which would
  /// make a collapsed member look free — the pool would be fast, and for a reason that has nothing
  /// to do with the question.
  /// </summary>
  private const string _SMALL_CACHE_DUPLICATED =
    """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "caches": { "global": { "size": "16MiB" } } }""";

  private const string _SMALL_CACHE_SPREAD =
    """{ "caches": { "global": { "size": "16MiB" } } }""";

  /// <summary>Comfortably larger than the cache above, so a read of it is served by the storage.</summary>
  private const int _WORKING_SET = 48 * 1024 * 1024;

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>
  /// Which member holds the PRIMARY copy of a file — the one the read path tries first.
  ///
  /// Collapsing the member holding the shadow would prove nothing: the read would never have gone
  /// there anyway, and the scenario would pass while measuring an untouched pool. The primary sits
  /// at the member's top level; a shadow lives inside a FOLDER.DUPLICATE.$DRIVEBENDER container.
  /// </summary>
  private static int _MemberHoldingPrimary(MountedPool pool, string name) {
    for (var index = 0; index < pool.MemberPaths.Count; ++index)
      if (File.Exists(Path.Combine(pool.MemberPaths[index], name)))
        return index;

    throw new InvalidOperationException(
      $"No member holds a primary copy of '{name}':{Environment.NewLine}{pool.DescribeMembers()}");
  }

  /// <summary>Collapses a member's rate to a crawl, live, and waits for the running pool to pick it up.</summary>
  private static void _Collapse(MountedPool pool, int memberIndex) {
    DbMount.SetMemberLimits(pool.PoolName, pool.MemberPaths[memberIndex], _COLLAPSED_IOPS, _COLLAPSED_THROUGHPUT);
    DbMount.RequestLiveReload(pool.PoolName);
    Thread.Sleep(2500); // the pump consumes the request on its next tick
  }

  /// <summary>Gives a collapsed member its speed back, live.</summary>
  private static void _Recover(MountedPool pool, int memberIndex) {
    DbMount.SetMemberLimits(pool.PoolName, pool.MemberPaths[memberIndex], 0, 0);
    DbMount.RequestLiveReload(pool.PoolName);
    Thread.Sleep(2500);
  }

  private static (byte[] content, TimeSpan elapsed) _TimedRead(MountedPool pool, string name) {
    var stopwatch = Stopwatch.StartNew();
    var content = File.ReadAllBytes(pool.PathTo(name));
    stopwatch.Stop();
    return (content, stopwatch.Elapsed);
  }

  #region the premise: a limit applied to a RUNNING pool actually takes effect

  [Test]
  [Category("Exception")]
  [Description("A rate limit lowered on a mounted pool takes effect without a remount, rather than being ignored until the next mount.")]
  public void Brownout_WhenAMembersLimitIsLoweredLive_ThenItTakesEffectWithoutARemount() {
    // A live reload re-read the config, the member roles and the duplication level, and silently
    // did NOT re-read the rate limits — they were applied once, when the engine built its members.
    // So the one setting whose whole purpose is the situation you cannot unmount for ("the pool is
    // taking too much of that disk, ease off") was the one that required a remount. Every scenario
    // below depends on this working, and would pass while measuring an unlimited disk if it did not.
    using var pool = MountedPool.Create(members: 1, poolDefaults: _SMALL_CACHE_SPREAD);

    var size = (int)(_COLLAPSED_THROUGHPUT * 4);
    var before = Stopwatch.StartNew();
    File.WriteAllBytes(pool.PathTo("before.bin"), _Payload(size, 2001));
    before.Stop();

    _Collapse(pool, 0);

    var after = Stopwatch.StartNew();
    File.WriteAllBytes(pool.PathTo("after.bin"), _Payload(size, 2002));
    after.Stop();

    // one second of the limit is bucket credit, so about three seconds of the four are chargeable
    after.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5),
      $"a limit lowered under a mounted pool must bind: the same {size / _MIB} MiB took "
      + $"{before.Elapsed.TotalSeconds:F2}s unlimited and {after.Elapsed.TotalSeconds:F2}s after the "
      + $"member was collapsed to {_COLLAPSED_THROUGHPUT / _MIB} MiB/s.{Environment.NewLine}{pool.MountLog}");

    after.Elapsed.Should().BeGreaterThan(before.Elapsed,
      "and it must be slower than it was, which is the whole claim");

    File.ReadAllBytes(pool.PathTo("after.bin")).Length.Should().Be(size, "being throttled is not permission to lose bytes");
  }

  #endregion

  #region reads: does a healthy copy rescue us, or do we go down with the sick member?

  [Test]
  [Category("Performance")]
  [Description("The member holding the primary copy collapses to a crawl: reads must be served from the healthy copy instead of crawling with it.")]
  public void Brownout_GivenThePrimaryCopysMemberCollapses_ThenReadsAreServedFromTheHealthyCopy() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _SMALL_CACHE_DUPLICATED);

    // two separate files so both measurements are equally cold — re-reading one file would compare
    // a cold read against a partly cached one
    var healthy = _Payload(_WORKING_SET, 2100);
    var underBrownout = _Payload(_WORKING_SET, 2101);
    File.WriteAllBytes(pool.PathTo("healthy.bin"), healthy);
    File.WriteAllBytes(pool.PathTo("brownout.bin"), underBrownout);

    MountedPool.WaitUntil(
      () => pool.PhysicalCopies("healthy.bin").Count >= 2 && pool.PhysicalCopies("brownout.bin").Count >= 2,
      TimeSpan.FromMinutes(3)).Should().BeTrue(
      $"both files must be genuinely duplicated, or there is no healthy copy to rescue anything."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    var (baselineContent, baseline) = _TimedRead(pool, "healthy.bin");
    baselineContent.Should().Equal(healthy, "the baseline read must be correct before it is used as a baseline");

    // collapse the member the read path would try FIRST; collapsing the other one would leave the
    // read untouched and the scenario would pass having measured nothing
    var primary = _MemberHoldingPrimary(pool, "brownout.bin");
    _Collapse(pool, primary);

    var (content, degraded) = _TimedRead(pool, "brownout.bin");
    content.Should().Equal(underBrownout,
      $"correctness first: whichever copy served it, the bytes must be right."
      + $"{Environment.NewLine}{pool.MountLog}");

    var floorIfServedFromTheSickMember = TimeSpan.FromSeconds(_WORKING_SET / (double)_COLLAPSED_THROUGHPUT);
    TestContext.Out.WriteLine(
      $"[brownout] {_WORKING_SET / _MIB} MiB read: {baseline.TotalSeconds:F2}s healthy, "
      + $"{degraded.TotalSeconds:F2}s with the primary copy's member at {_COLLAPSED_THROUGHPUT / _MIB} MiB/s "
      + $"(serving from the sick member alone would take about {floorIfServedFromTheSickMember.TotalSeconds:F0}s).");

    degraded.Should().BeLessThan(floorIfServedFromTheSickMember,
      $"a healthy copy sits on the other member, and a read that keeps going to the sick one turns "
      + $"one dying disk into a dying pool — {_WORKING_SET / _MIB} MiB took {degraded.TotalSeconds:F1}s "
      + $"against a baseline of {baseline.TotalSeconds:F2}s, where serving it entirely from the "
      + $"collapsed member would take about {floorIfServedFromTheSickMember.TotalSeconds:F0}s."
      + $"{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("Performance")]
  [Description("When the collapsed member recovers, the pool's throughput comes back rather than staying degraded.")]
  public void Brownout_WhenTheMemberRecovers_ThenThroughputComesBack() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _SMALL_CACHE_DUPLICATED);

    var content = _Payload(_WORKING_SET, 2200);
    File.WriteAllBytes(pool.PathTo("recovers.bin"), content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("recovers.bin").Count >= 2, TimeSpan.FromMinutes(3))
      .Should().BeTrue($"the file must be duplicated.{Environment.NewLine}{pool.DescribeMembers()}");

    var primary = _MemberHoldingPrimary(pool, "recovers.bin");
    _Collapse(pool, primary);
    var (_, degraded) = _TimedRead(pool, "recovers.bin");

    _Recover(pool, primary);
    var (recoveredContent, recovered) = _TimedRead(pool, "recovers.bin");

    recoveredContent.Should().Equal(content, "recovery must not change the bytes");

    TestContext.Out.WriteLine(
      $"[brownout] recovery: {degraded.TotalSeconds:F2}s while collapsed, {recovered.TotalSeconds:F2}s after.");

    // A pool that stays slow after the disk is healthy again would mean the degradation was latched
    // — a member parked at the back of the readiness order for good, or a cached decision nothing
    // revisits. The bar is loose because the read is fast either way; what it refuses is a pool that
    // never comes back.
    recovered.Should().BeLessThan(TimeSpan.FromSeconds(_WORKING_SET / (double)_COLLAPSED_THROUGHPUT),
      $"a recovered member must stop costing anything.{Environment.NewLine}{pool.MountLog}");
  }

  #endregion

  #region writes and placement: does new work avoid the sick member?

  [Test]
  [Category("Performance")]
  [Description("With two capacity members and one collapsed, new files are placed on the healthy one rather than spread evenly into the wall.")]
  public void Brownout_GivenOneOfTwoCapacityMembersCollapses_ThenNewFilesGoToTheHealthyOne() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _SMALL_CACHE_SPREAD);

    // ODD, so the two members cannot come back tied. The assertion is a strict inequality — the
    // healthy member must take MORE — and with an even count a dead heat fails it while actually
    // showing placement doing nothing wrong. Windows CI produced exactly that: 6 / 6.
    const int files = 13;
    const int size = 2 * 1024 * 1024;

    // Established with a CONCURRENT burst, because that is the only time placement's load term does
    // anything: files written one after another leave nothing in flight to break the tie, so free
    // space decides and every one of them can legitimately land on the same member. Asserting a
    // split after sequential writes asked the design for a guarantee it does not make — and on a
    // runner whose two members have identical free space it duly came back 12 / 0.
    //
    // The degree of parallelism is stated rather than inherited. Parallel.For sizes itself from the
    // core count, so on a two-core runner barely two files are ever in flight and the load term has
    // next to nothing to weigh — the burst this scenario depends on quietly became a trickle, and
    // the result drifted with the shape of the host rather than the behaviour under test.
    var burst = new ParallelOptions { MaxDegreeOfParallelism = files };
    Parallel.For(0, files, burst, file => File.WriteAllBytes(pool.PathTo($"before{file}.bin"), _Payload(size, 2300 + file)));

    var beforeSplit = _CountPerMember(pool, "before", files);
    beforeSplit.Should().OnlyContain(count => count > 0,
      $"with both members healthy the pool must use both, or this scenario cannot detect a shift. "
      + $"Split was {string.Join(" / ", beforeSplit)}.{Environment.NewLine}{pool.DescribeMembers()}");

    // the busier member of the two is the one to collapse: shifting work away from it is the harder
    // direction, since placement already favoured it
    var victim = beforeSplit[0] >= beforeSplit[1] ? 0 : 1;
    _Collapse(pool, victim);

    var stopwatch = Stopwatch.StartNew();
    Parallel.For(0, files, burst, file => File.WriteAllBytes(pool.PathTo($"after{file}.bin"), _Payload(size, 2400 + file)));
    stopwatch.Stop();

    var afterSplit = _CountPerMember(pool, "after", files);
    var healthy = victim == 0 ? 1 : 0;

    TestContext.Out.WriteLine(
      $"[brownout] placement: {files} files before the brownout split {string.Join(" / ", beforeSplit)}, "
      + $"{files} after split {string.Join(" / ", afterSplit)} (member {victim} collapsed to "
      + $"{_COLLAPSED_THROUGHPUT / _MIB} MiB/s), written in {stopwatch.Elapsed.TotalSeconds:F1}s.");

    // The SHIFT, not an absolute majority. What placement promises is that a member accumulating
    // outstanding work stops being chosen as often — it says nothing about where the baseline was,
    // and the baseline is a property of the host: free space, and which member happened to win the
    // first few ties. A runner that opened 12 / 1 cannot reach a majority on the other member
    // inside thirteen files however well the load term works, so demanding one measures the disk
    // the runner was given. It duly failed there at 7 / 6 — having moved five files off the sick
    // member, which is the effect this scenario exists to see.
    var shift = afterSplit[healthy] - beforeSplit[healthy];
    shift.Should().BeGreaterThanOrEqualTo(3,
      $"placement weighs how much work a member already has outstanding, and a collapsed member "
      + $"accumulates it — new files must therefore move TOWARDS the healthy member. It held "
      + $"{beforeSplit[healthy]} of {files} before the brownout and {afterSplit[healthy]} after, a "
      + $"shift of {shift}. Split was {string.Join(" / ", beforeSplit)} then "
      + $"{string.Join(" / ", afterSplit)}, with member {victim} collapsed."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    // and nothing was lost on the way
    for (var file = 0; file < files; ++file)
      File.ReadAllBytes(pool.PathTo($"after{file}.bin")).Should()
        .Equal(_Payload(size, 2400 + file), $"'after{file}.bin' must be exactly what was written");
  }

  private static int[] _CountPerMember(MountedPool pool, string prefix, int files) {
    var counts = new int[pool.MemberPaths.Count];
    for (var member = 0; member < pool.MemberPaths.Count; ++member)
      for (var file = 0; file < files; ++file)
        if (File.Exists(Path.Combine(pool.MemberPaths[member], $"{prefix}{file}.bin")))
          ++counts[member];

    return counts;
  }

  /// <summary>Writes a fresh file with one member collapsed, and reports what it cost.</summary>
  private static (TimeSpan elapsed, byte[] content) _WriteWithOneMemberCollapsed(MountedPool pool, int size, int seed) {
    File.WriteAllBytes(pool.PathTo("warm.bin"), _Payload(size, seed));
    MountedPool.WaitUntil(() => pool.PhysicalCopies("warm.bin").Count >= 2, TimeSpan.FromMinutes(2))
      .Should().BeTrue($"the pool must be duplicating before this means anything.{pool.DescribeMembers()}");

    _Collapse(pool, _MemberHoldingPrimary(pool, "warm.bin"));

    var content = _Payload(size, seed + 1);
    var stopwatch = Stopwatch.StartNew();
    File.WriteAllBytes(pool.PathTo("during.bin"), content);
    stopwatch.Stop();

    File.ReadAllBytes(pool.PathTo("during.bin")).Should().Equal(content,
      $"a write taken during a brownout must still be the user's bytes.{Environment.NewLine}{pool.MountLog}");

    return (stopwatch.Elapsed, content);
  }

  [Test]
  [Category("Performance")]
  [Description("Under the default ack policy a duplicated write IS paced by its slowest copy — the durability promise costs exactly that.")]
  public void Brownout_GivenTheDefaultAckPolicy_ThenADuplicatedWriteIsPacedByTheSickCopy() {
    // Not a defect, and worth pinning precisely because it looks like one. `minCopiesBeforeAck`
    // defaults to 2, so with duplication 2 an acknowledgement means the bytes are on BOTH disks
    // (SAFE-NOLOSS). If one of them has collapsed to a crawl, the application waits for the crawl —
    // that is the promise being kept, not broken. The scenario below shows the way out, and it is a
    // durability decision rather than a tuning one.
    using var pool = MountedPool.Create(members: 2, poolDefaults: _SMALL_CACHE_DUPLICATED);

    var size = (int)(_COLLAPSED_THROUGHPUT * 8);
    var (elapsed, content) = _WriteWithOneMemberCollapsed(pool, size, 2500);

    // one second of the limit is bucket credit, so the floor is the remainder at the sick rate
    var chargeable = TimeSpan.FromSeconds((size - _COLLAPSED_THROUGHPUT) / (double)_COLLAPSED_THROUGHPUT);
    TestContext.Out.WriteLine(
      $"[brownout] duplicated write of {size / _MIB} MiB took {elapsed.TotalSeconds:F2}s with one member at "
      + $"{_COLLAPSED_THROUGHPUT / _MIB} MiB/s and the DEFAULT two-copy ack (its own rate implies "
      + $"{chargeable.TotalSeconds:F0}s).");

    elapsed.Should().BeGreaterThan(chargeable * 0.7,
      $"the default ack waits for every copy, so a collapsed copy paces the write — if this got "
      + $"FASTER than the sick member can absorb, an acknowledgement stopped meaning the bytes are "
      + $"on both disks.{Environment.NewLine}{pool.MountLog}");

    foreach (var (where, bytes) in pool.PhysicalCopies("during.bin"))
      bytes.Should().Equal(content, $"the copy at {where} was waited for, so it must be complete");
  }

  [Test]
  [Category("Exception")]
  [Description("Weakening a duplicated write's ack floor to one copy is refused outright, so the pacing above cannot be configured away by accident.")]
  public void Brownout_GivenSomeoneTriesToWeakenTheAckFloor_ThenThePoolRefusesToMount() {
    // The obvious reaction to the scenario above is to lower `minCopiesBeforeAck` — and the product
    // does not allow it for duplicated folders, which is worth a guard of its own. It means the
    // pacing is not an oversight anyone can quietly configure around: acknowledging a duplicated
    // write from one copy would make duplication a lie for the window before the second lands.
    var weakened =
      """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "write": { "minCopiesBeforeAck": 1 } }""";

    var mounting = () => MountedPool.Create(members: 2, poolDefaults: weakened);
    mounting.Should().Throw<Exception>("a duplicated pool must refuse an ack floor below its duplication level")
      .Which.Message.Should().Contain("minCopiesBeforeAck",
        "and it must say which setting it refused, so the operator can act on it");
  }

  [Test]
  [Category("Performance")]
  [Description("The RAM-ack opt-in is the sanctioned way out: the write is taken at memory speed and both copies converge behind it.")]
  public void Brownout_GivenTheVolatileAckOptIn_ThenTheWriteIsNotPacedByTheSickCopy() {
    // The escape hatch the product DOES offer (SAFE-RAM): an explicit per-folder opt-in where the
    // acknowledgement may come from memory. It trades durability for latency knowingly, which is the
    // difference between this and lowering the ack floor.
    const string volatileAck =
      """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "caches": { "global": { "size": "16MiB" } }, "write": { "policy": "performance", "acceptVolatileAck": true } }""";

    using var pool = MountedPool.Create(members: 2, poolDefaults: volatileAck);

    var size = (int)(_COLLAPSED_THROUGHPUT * 8);
    var (elapsed, content) = _WriteWithOneMemberCollapsed(pool, size, 2600);

    var ifPacedBySickCopy = TimeSpan.FromSeconds((size - _COLLAPSED_THROUGHPUT) / (double)_COLLAPSED_THROUGHPUT);
    TestContext.Out.WriteLine(
      $"[brownout] the same write with the RAM-ack opt-in took {elapsed.TotalSeconds:F2}s "
      + $"(against {ifPacedBySickCopy.TotalSeconds:F0}s when every copy must be durable first).");

    elapsed.Should().BeLessThan(ifPacedBySickCopy * 0.5,
      $"when the acknowledgement may come from memory, a collapsed member must not pace the "
      + $"application — that is the whole point of the opt-in.{Environment.NewLine}{pool.MountLog}");

    // and the owed copy still converges rather than being abandoned: slower redundancy, not less of it
    MountedPool.WaitUntil(() => {
      var copies = pool.PhysicalCopies("during.bin");
      return copies.Count >= 2 && copies.All(c => c.content.SequenceEqual(content));
    }, TimeSpan.FromMinutes(4)).Should().BeTrue(
      $"the copy owed to the collapsed member must catch up, slowly, rather than being dropped."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  #endregion

}
