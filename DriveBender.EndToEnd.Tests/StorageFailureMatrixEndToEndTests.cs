using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Every way a member can fail, against every kind of storage it can fail on.
///
/// The resilience scenarios elsewhere each pick one failure and one pool shape. That leaves the
/// interesting question unasked, because the failures the product has to survive are not
/// independent of the storage underneath them: a disk pulled from a fast tier and a disk pulled from
/// a rate-limited one are different events, the second one having far more work still in flight when
/// it goes. This is the cross product, driven through a real mount.
///
/// The invariant is the same in every cell and does not depend on the speeds: <b>nothing the pool
/// acknowledged is lost, the mount survives, and the cost of the failure is bounded.</b> The timings
/// are reported rather than merely asserted, because "what happens when a disk goes" is a question
/// with a number for an answer, and a green tick does not give it.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class StorageFailureMatrixEndToEndTests {

  /// <summary>Small on purpose: this matrix is about failure, not throughput, and some cells are rate-limited to 6 MiB/s.</summary>
  private const int _FILE_SIZE = 256 * 1024;

  private const int _FILES = 4;

  /// <summary>
  /// The cost a single operation may pay for a member failing under it.
  ///
  /// Generous by design — discovering that a disk is gone is not free, and the engine's own fault
  /// cooldown is five seconds. What this refuses is the unbounded case: an operation that waits on
  /// storage which is never coming back.
  /// </summary>
  private static readonly TimeSpan _BOUND = TimeSpan.FromSeconds(20);

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>
  /// The storage pairings every failure below is run against — simulated first, so the matrix is
  /// identical on every machine and in CI, then whatever REAL devices this host happens to offer.
  /// </summary>
  private static IEnumerable<TestCaseData> _Pairings() {
    yield return new TestCaseData(StorageKind.Ram, StorageKind.Ram).SetName("{m}(RAM + RAM)");
    yield return new TestCaseData(StorageKind.Ram, StorageKind.SimulatedSdCard).SetName("{m}(RAM + SD card)");
    yield return new TestCaseData(StorageKind.SimulatedSsd, StorageKind.SimulatedHardDisk).SetName("{m}(SSD + HDD)");
    yield return new TestCaseData(StorageKind.SimulatedSsd, StorageKind.SimulatedCloud).SetName("{m}(SSD + cloud)");

    // real hardware, when there is any: the honest half, and the half that cannot be relied on
    var real = StorageKind.RealDevices;
    for (var index = 1; index < real.Count; ++index)
      yield return new TestCaseData(real[0], real[index]).SetName($"{{m}}(real: {real[0].Name} + {real[index].Name})");
  }

  /// <summary>A pool whose two members really do hold a copy each, across the given storage.</summary>
  private static MountedPool _MirroredAcross(StorageKind first, StorageKind second)
    => MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk, storageKinds: [first, second]);

  /// <summary>Writes the working set and waits until both members really hold it.</summary>
  private static Dictionary<string, byte[]> _Seed(MountedPool pool, int seed) {
    var expected = new Dictionary<string, byte[]>();
    for (var file = 0; file < _FILES; ++file) {
      var content = _Payload(_FILE_SIZE, seed + file);
      expected[$"m{file}.bin"] = content;
      File.WriteAllBytes(pool.PathTo($"m{file}.bin"), content);
    }

    MountedPool.WaitUntil(() => expected.Keys.All(n => pool.PhysicalCopies(n).Count >= 2), TimeSpan.FromMinutes(3))
      .Should().BeTrue(
        $"the matrix is about surviving a failure, so there has to be a survivor: both members must "
        + $"hold a copy before anything is broken.{Environment.NewLine}{pool.DescribeMembers()}"
        + $"{Environment.NewLine}{pool.MountLog}");

    return expected;
  }

  /// <summary>Reads every file back through the mount, returning the worst single read.</summary>
  private static TimeSpan _ReadAllAndAssert(MountedPool pool, Dictionary<string, byte[]> expected, string when) {
    var slowest = TimeSpan.Zero;
    foreach (var (name, content) in expected) {
      var stopwatch = Stopwatch.StartNew();
      var read = File.ReadAllBytes(pool.PathTo(name));
      stopwatch.Stop();
      if (stopwatch.Elapsed > slowest)
        slowest = stopwatch.Elapsed;

      read.Should().Equal(content, $"'{name}' must be whole {when}.{Environment.NewLine}{pool.MountLog}");
    }

    return slowest;
  }

  private static void _Report(string cell, string what, TimeSpan cost)
    => TestContext.Out.WriteLine($"[matrix] {cell}: {what} — worst single operation {cost.TotalMilliseconds:F0} ms");

  [TestCaseSource(nameof(_Pairings))]
  [Category("EdgeCase")]
  [Description("The second member is pulled and stays gone: every file is still served whole from the survivor, promptly.")]
  public void Removed_GivenAMemberIsPulledAndStaysGone_ThenEveryFileIsStillServedPromptly(
    StorageKind first, StorageKind second) {
    using var pool = _MirroredAcross(first, second);
    var expected = _Seed(pool, 1000);

    pool.Eject(1);
    var worst = _ReadAllAndAssert(pool, expected, "with the second member pulled");

    pool.IsMountAlive.Should().BeTrue($"one disk going must not take the mount down.{Environment.NewLine}{pool.MountLog}");
    worst.Should().BeLessThan(_BOUND,
      $"reads must stay bounded with a member gone — the worst took {worst.TotalSeconds:F1}s."
      + $"{Environment.NewLine}{pool.MountLog}");

    _Report($"{first} + {second}", "member pulled, reads from survivor", worst);
  }

  [TestCaseSource(nameof(_Pairings))]
  [Category("EdgeCase")]
  [Description("A member vanishes in the middle of a streaming write: every acknowledged byte is still there afterwards.")]
  public void Removed_GivenAMemberVanishesMidWrite_ThenEveryAcknowledgedByteSurvives(
    StorageKind first, StorageKind second) {
    using var pool = _MirroredAcross(first, second);
    _Seed(pool, 1100);

    const int chunks = 16;
    const int chunkSize = 32 * 1024;
    var written = new List<byte[]>();
    var worstChunk = TimeSpan.Zero;

    using (var stream = new FileStream(pool.PathTo("midwrite.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) {
      for (var chunk = 0; chunk < chunks; ++chunk) {
        if (chunk == chunks / 2)
          pool.Eject(1); // the disk goes, mid-stream

        var payload = _Payload(chunkSize, 1200 + chunk);
        var stopwatch = Stopwatch.StartNew();
        stream.Write(payload, 0, payload.Length);
        stopwatch.Stop();
        if (stopwatch.Elapsed > worstChunk)
          worstChunk = stopwatch.Elapsed;

        written.Add(payload);
      }

      stream.Flush();
    }

    var whole = written.SelectMany(c => c).ToArray();
    File.ReadAllBytes(pool.PathTo("midwrite.bin")).Should().Equal(whole,
      $"every byte the pool accepted before and after the disk went must still be there."
      + $"{Environment.NewLine}{pool.MountLog}");

    worstChunk.Should().BeLessThan(_BOUND,
      $"losing a member mid-write must cost a bounded stall, not an open-ended one — the worst "
      + $"{chunkSize / 1024} KiB chunk took {worstChunk.TotalSeconds:F1}s.{Environment.NewLine}{pool.MountLog}");

    _Report($"{first} + {second}", "member pulled mid-write", worstChunk);
  }

  [TestCaseSource(nameof(_Pairings))]
  [Category("EdgeCase")]
  [Description("A member is pulled, work continues without it, and when it returns the pool converges with every copy agreeing.")]
  public void Removed_GivenTheMemberReturns_ThenThePoolConvergesWithEveryCopyAgreeing(
    StorageKind first, StorageKind second) {
    using var pool = _MirroredAcross(first, second);
    var expected = _Seed(pool, 1300);

    pool.Eject(1);

    // the pool keeps taking work while a disk is away — that is the point of it
    foreach (var name in expected.Keys.ToArray()) {
      var updated = _Payload(_FILE_SIZE, 1400 + name.GetHashCode() % 100);
      File.WriteAllBytes(pool.PathTo(name), updated);
      expected[name] = updated;
    }

    var newFile = _Payload(_FILE_SIZE, 1500);
    File.WriteAllBytes(pool.PathTo("while-away.bin"), newFile);
    expected["while-away.bin"] = newFile;

    pool.Restore(1);

    // Everything converges to full duplication AND to the current content. Waiting on the COUNT
    // alone would prove nothing here and would look like it did: the returning disk still carries
    // its old copy of every file it knew about, so two copies exist from the instant it is plugged
    // back in — one of them stale. What has to be waited for is the copies AGREEING.
    foreach (var (name, content) in expected) {
      var converged = MountedPool.WaitUntil(() => {
        var copies = pool.PhysicalCopies(name);
        return copies.Count >= 2 && copies.All(c => c.content.SequenceEqual(content));
      }, TimeSpan.FromMinutes(3));

      converged.Should().BeTrue(
        $"'{name}' must end up duplicated AND with both copies holding what was last written — a "
        + $"returning disk may not keep serving what it remembered from before it left. Copies now: "
        + $"{string.Join(", ", pool.PhysicalCopies(name).Select(c => $"{c.where} ({c.content.Length} bytes)"))}"
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content, $"'{name}' must read back as last written");
    }

    _Report($"{first} + {second}", "member returned, pool reconverged", TimeSpan.Zero);
  }

  [TestCaseSource(nameof(_Pairings))]
  [Category("Exception")]
  [Description("A member that is still present but fails every operation is routed around rather than waited on.")]
  public void Failing_GivenAMemberErrorsOnEveryOperation_ThenTheHealthyCopyStillServesPromptly(
    StorageKind first, StorageKind second) {
    using var pool = _MirroredAcross(first, second);
    var expected = _Seed(pool, 1600);

    // a fresh mount, so nothing is held open from before the failure
    pool.Remount();
    if (!pool.Cripple(1))
      Assert.Ignore("This platform (or this member's filesystem) cannot be made to fail without going offline.");

    try {
      var worst = _ReadAllAndAssert(pool, expected, "with the second member failing every request");

      pool.IsMountAlive.Should().BeTrue(
        $"a disk failing every request must not take the mount down.{Environment.NewLine}{pool.MountLog}");

      worst.Should().BeLessThan(_BOUND,
        $"a failing member must be routed around, not waited on — the worst read took "
        + $"{worst.TotalSeconds:F1}s.{Environment.NewLine}{pool.MountLog}");

      _Report($"{first} + {second}", "member failing every request", worst);
    } finally {
      pool.Uncripple(1);
    }
  }

  [TestCaseSource(nameof(_Pairings))]
  [Category("Exception")]
  [Description("The power is cut with the pool live: everything written and closed beforehand is still there when it comes back.")]
  public void PowerCut_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvives(StorageKind first, StorageKind second) {
    using var pool = _MirroredAcross(first, second);
    var expected = _Seed(pool, 1700);

    pool.CrashAndRemount(); // no unmount, no flush, no shutdown hook — the lights go off

    var worst = _ReadAllAndAssert(pool, expected, "after the power was cut");
    pool.IsMountAlive.Should().BeTrue($"the pool must come back up.{Environment.NewLine}{pool.MountLog}");

    _Report($"{first} + {second}", "power cut, pool restarted", worst);
  }

  [TestCaseSource(nameof(_Pairings))]
  [Category("Exception")]
  [Description("The power is cut AND a member is missing when the pool comes back: whatever the survivor holds is still served.")]
  public void PowerCut_GivenAMemberIsAlsoMissingAfterwards_ThenTheSurvivorStillServes(
    StorageKind first, StorageKind second) {
    using var pool = _MirroredAcross(first, second);
    var expected = _Seed(pool, 1800);

    pool.Eject(1);
    pool.CrashAndRemount(); // two failures at once, which is when storage products actually break

    var worst = _ReadAllAndAssert(pool, expected, "after a power cut with a member also missing");
    pool.IsMountAlive.Should().BeTrue(
      $"a pool must start with a member missing rather than refusing.{Environment.NewLine}{pool.MountLog}");

    worst.Should().BeLessThan(_BOUND,
      $"serving from the survivor after a crash must stay bounded — worst read {worst.TotalSeconds:F1}s."
      + $"{Environment.NewLine}{pool.MountLog}");

    _Report($"{first} + {second}", "power cut with a member missing", worst);
  }

}
