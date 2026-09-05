using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// What losing a disk COSTS, as opposed to whether it is survived.
///
/// <see cref="MemberLossEndToEndTests"/> proves the pool keeps the data when a member goes; these
/// scenarios ask the other half of the question — that it keeps serving it PROMPTLY. A pool that
/// answers correctly after a thirty-second pause has lost no data and is still unusable, and every
/// mechanism the engine has for this (the fault cooldown that sinks a failing member, the online
/// probe that drops a vanished one) exists to bound exactly that cost. None of it was measured end
/// to end.
///
/// The two failures are deliberately kept apart, because the engine handles them by different
/// means: a member that VANISHES is filtered out by the online probe and costs nothing after the
/// first miss, while a member that is still there and FAILS every request has to be routed around
/// one operation at a time — which is where an unbounded stall would come from.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class MemberFailureLatencyEndToEndTests {

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  private static MountedPool _DuplicatedPool() => MountedPool.Create(poolDefaults: MountedPool.DuplicatedOnOneDisk);

  /// <summary>
  /// Reads a file in chunks, reporting the worst SINGLE chunk — a stall hides in the maximum and is
  /// averaged away by the mean.
  ///
  /// <paramref name="pausePerChunk"/> paces the reader like an application that does something with
  /// each chunk. Without it a scenario that pulls a disk "mid-read" does not: a few megabytes off an
  /// NVMe are gone in milliseconds, and the disk would leave long after the last byte arrived, so
  /// the whole thing would pass while proving nothing. The pause is deliberately outside the
  /// stopwatch — it is the harness waiting, not the pool.
  /// </summary>
  private static (byte[] content, TimeSpan slowestChunk) _ReadWatchingForStalls(string path, int chunkSize,
    TimeSpan pausePerChunk = default) {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize);
    using var collected = new MemoryStream();
    var buffer = new byte[chunkSize];
    var slowest = TimeSpan.Zero;

    while (true) {
      var stopwatch = Stopwatch.StartNew();
      var read = stream.Read(buffer, 0, buffer.Length);
      stopwatch.Stop();
      if (stopwatch.Elapsed > slowest)
        slowest = stopwatch.Elapsed;

      if (read <= 0)
        break;

      collected.Write(buffer, 0, read);
      if (pausePerChunk > TimeSpan.Zero)
        Thread.Sleep(pausePerChunk);
    }

    return (collected.ToArray(), slowest);
  }

  [Test]
  [Category("Exception")]
  [Description("A pool unmounted immediately after it comes up really unmounts, rather than the verb reporting a pool it cannot find.")]
  public void Unmount_GivenItIsAskedForTheMomentThePoolIsUsable_ThenItSucceedsAndTheMountIsGone() {
    // `dbmount status` and `dbmount unmount` look the pool up in the cross-process mount registry,
    // and on Linux that entry is written by the background pump the first time it sees the mount in
    // /proc/mounts — so between "the filesystem works" and "the tooling knows it exists" there was a
    // window, and anything landing in it got "No mounted pool matches", a non-zero exit, and a pool
    // that stayed mounted with its process still serving. Mount-then-unmount is the shortest script
    // anyone writes, and it is exactly the shape that falls into the window.
    var pool = MountedPool.Create(members: 1);
    try {
      // no settling wait on purpose: Create returns as soon as the OS can use the mount, which is
      // the earliest moment a user's next command could run
      var status = DbMount.Run(TimeSpan.FromSeconds(30), "status");
      status.Output.Should().Contain(pool.PoolName,
        $"a pool the OS is already serving must be visible to the tooling that manages it."
        + $"{Environment.NewLine}{pool.MountLog}");

      var unmount = DbMount.Run(TimeSpan.FromSeconds(60), "unmount", pool.PoolName);
      unmount.Succeeded.Should().BeTrue(
        $"unmounting a pool that is plainly mounted must succeed: the verb answered "
        + $"'{unmount.Output.Trim()}'.{Environment.NewLine}{pool.MountLog}");

      // and the claim has to be true: reporting success while the filesystem stays attached is
      // worse than refusing, because the caller then goes on to eject the disk
      MountedPool.WaitUntil(() => !pool.IsMountAlive, TimeSpan.FromSeconds(30)).Should().BeTrue(
        $"a successful unmount must actually end the mount, not just say so."
        + $"{Environment.NewLine}{pool.MountLog}");
    } finally {
      pool.Dispose();
    }
  }

  [Test]
  [Category("Exception")]
  [Description("A pool already mounted refuses to be mounted a second time, because two engines over one member set corrupt each other.")]
  public void Mount_GivenThePoolIsAlreadyMounted_ThenASecondMountIsRefused() {
    // The engine's own words: "two engines over one member set race and corrupt each other, and the
    // registry-entry check alone is TOCTOU-racy". The guard is an OS-level file lock, and an
    // advisory lock that does not actually exclude would leave two mount processes writing the same
    // members through two independent caches, two write buffers and two journals — which is the
    // worst corruption this product could produce, and it would look fine until it did not.
    //
    // Nothing exercised it, and the lock behaves differently enough between platforms to be worth
    // asking rather than assuming.
    using var pool = MountedPool.Create(members: 2);
    var content = _Payload(256 * 1024, 640);
    File.WriteAllBytes(pool.PathTo("guarded.bin"), content);

    // a SECOND mount of the same pool, at a target of its own so nothing else can refuse it first
    var secondTarget = Path.Combine(pool.Root, "second-mount");
    Directory.CreateDirectory(secondTarget);

    var second = DbMount.Run(TimeSpan.FromSeconds(90), "mount", "--manifest", pool.PoolName,
      "-t", secondTarget, "--foreground");

    second.Succeeded.Should().BeFalse(
      $"a pool that is already mounted must refuse a second engine over the same members."
      + $"{Environment.NewLine}second mount said: {second.Output}");

    second.Output.Should().Contain("already mounted",
      "and it must say why, so the operator knows to unmount rather than go looking for a driver fault");

    // the FIRST mount is untouched by the attempt, and still serving
    pool.IsMountAlive.Should().BeTrue(
      $"a refused second mount must not disturb the one that holds the lock.{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("guarded.bin")).Should().Equal(content,
      "and the data behind it must be exactly as it was");

    // nothing of the refused attempt may be left attached
    Directory.EnumerateFileSystemEntries(secondTarget).Should().BeEmpty(
      "the refused mount must not have attached anything at its target");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A member pulled while a large read is streaming: every remaining chunk still arrives promptly and the content is whole.")]
  public void Eject_WhileALargeReadIsStreaming_ThenEveryRemainingChunkStillArrivesPromptly() {
    using var pool = _DuplicatedPool();
    const int size = 12 * 1024 * 1024;
    const int chunkSize = 128 * 1024;
    var content = _Payload(size, 601);

    File.WriteAllBytes(pool.PathTo("streamed.bin"), content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("streamed.bin").Count >= 2, TimeSpan.FromMinutes(2));

    // the pool's cache would serve the whole file from RAM and prove nothing about the disks
    _RemountCold(pool);

    var ejected = new ManualResetEventSlim();
    var puller = new Thread(() => {
      Thread.Sleep(1000); // let the read get well under way first
      pool.Eject(1);
      ejected.Set();
    }) { IsBackground = true };

    puller.Start();
    // 96 chunks paced 40ms apart spans about four seconds, so the disk genuinely leaves DURING the
    // read rather than after it
    var (read, slowestChunk) = _ReadWatchingForStalls(pool.PathTo("streamed.bin"), chunkSize,
      TimeSpan.FromMilliseconds(40));

    ejected.Wait(TimeSpan.FromMinutes(1));
    puller.Join(TimeSpan.FromMinutes(1));

    read.Should().Equal(content,
      $"a member leaving mid-read must not change a byte of the answer.{Environment.NewLine}{pool.MountLog}");

    // one chunk may pay for discovering the member is gone; none may pay for waiting on it
    slowestChunk.Should().BeLessThan(TimeSpan.FromSeconds(10),
      $"losing a member costs a bounded discovery, not an open-ended wait — the worst {chunkSize / 1024} KiB "
      + $"chunk took {slowestChunk.TotalSeconds:F2}s.{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("Exception")]
  [Description("A member that is still present but fails every operation is routed around: reads keep completing promptly from the healthy copy.")]
  public void Cripple_GivenAMemberFailsEveryOperationWithoutGoingOffline_ThenReadsStillCompletePromptly() {
    using var pool = _DuplicatedPool();
    const int files = 6;
    const int size = 1024 * 1024;

    var expected = new Dictionary<string, byte[]>();
    for (var file = 0; file < files; ++file) {
      var content = _Payload(size, 610 + file);
      expected[$"hurt{file}.bin"] = content;
      File.WriteAllBytes(pool.PathTo($"hurt{file}.bin"), content);
    }

    MountedPool.WaitUntil(() => expected.Keys.All(n => pool.PhysicalCopies(n).Count >= 2), TimeSpan.FromMinutes(2));

    // measure the healthy pool first, so the degraded number has something honest to be compared to
    _RemountCold(pool);
    var healthy = _TimeReadingAll(pool, expected);

    // A fresh mount with cold caches, and only THEN the disk starts failing. Crippling before the
    // remount would be crippling the mount itself — the pool would refuse to come up, and the
    // scenario is about a disk that dies under a RUNNING pool, not one that was already dead.
    _RemountCold(pool);
    if (!pool.Cripple(1))
      Assert.Ignore("This platform (or this filesystem) cannot be made to fail operations without going offline.");

    try {
      var degraded = _TimeReadingAll(pool, expected);

      foreach (var (name, content) in expected)
        File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content,
          $"'{name}' has a healthy copy and must be served from it.{Environment.NewLine}{pool.MountLog}");

      // The fault cooldown exists precisely so that one failing disk is paid for once rather than
      // once per block. Ten times the healthy time is a deliberately loose bar: the failure is
      // discovered by real errors and that is not free, but an order of magnitude is the boundary
      // between "degraded" and "unusable".
      degraded.Should().BeLessThan(healthy * 10 + TimeSpan.FromSeconds(5),
        $"a failing member must be routed around, not waited on: {healthy.TotalSeconds:F2}s healthy "
        + $"against {degraded.TotalSeconds:F2}s with one disk failing every request."
        + $"{Environment.NewLine}{pool.MountLog}");

      pool.IsMountAlive.Should().BeTrue(
        $"a disk failing every request must not take the mount down.{Environment.NewLine}{pool.MountLog}");
    } finally {
      pool.Uncripple(1);
    }
  }

  private static TimeSpan _TimeReadingAll(MountedPool pool, Dictionary<string, byte[]> expected) {
    var stopwatch = Stopwatch.StartNew();
    foreach (var name in expected.Keys)
      _ = File.ReadAllBytes(pool.PathTo(name));

    stopwatch.Stop();
    return stopwatch.Elapsed;
  }

  /// <summary>
  /// Comes back up with BOTH caches cold — the pool's, which the remount clears, and the host's,
  /// which has to be evicted by hand. Anything measured without the second one is a measurement of
  /// RAM wearing a disk's name.
  /// </summary>
  private static void _RemountCold(MountedPool pool) {
    if (!PageCache.CanDrop) {
      pool.Remount();
      return;
    }

    pool.WhileUnmounted(() => {
      foreach (var storage in pool.StoragePaths)
        PageCache.DropTree(storage);
    });
  }

  [Test]
  [Category("EdgeCase")]
  [Description("The capacity disk is pulled while the drainer is moving a file down to it: the file is on one tier or the other, never on neither, and comes back whole.")]
  public void Eject_WhileTheDrainerIsMovingAFileDown_ThenTheFileIsNeverLost() {
    using var pool = MountedPool.CreateTiered();
    const int size = 8 * 1024 * 1024;
    var content = _Payload(size, 620);

    File.WriteAllBytes(pool.PathTo("interrupted.bin"), content);

    // catch the drainer in the act: it starts on its own once the file settles, so the disk is
    // pulled while that whole-file copy is most likely to be in flight
    Thread.Sleep(1500);
    pool.Eject(1);

    // A tiered pool stores ONE copy, so a file whose drain had already finished went away with the
    // disk — correctly, and that is not what this scenario is about. The claim being pinned is the
    // one a move can genuinely break: the file must exist on the landing zone or on the capacity
    // disk, and never on NEITHER, which is what deleting the source before the destination was
    // durable would produce. Both are inspected directly, including the detached disk, because the
    // mount can only see the half that is still attached.
    var onLandingZone = pool.PhysicalCopies("interrupted.bin");
    var onDetachedCapacity = pool.CopiesOnDetachedStorage(1, "interrupted.bin");

    (onLandingZone.Count + onDetachedCapacity.Count).Should().BeGreaterThan(0,
      $"an interrupted move must leave the file on one tier or the other — on neither is the move "
      + $"having destroyed it.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    foreach (var (where, bytes) in onLandingZone)
      bytes.Should().Equal(content, $"the copy left at {where} must be whole, not half-moved");

    foreach (var bytes in onDetachedCapacity)
      bytes.Should().Equal(content, "the copy that reached the capacity disk must be whole, not half-written");

    pool.IsMountAlive.Should().BeTrue(
      $"losing the drain target must not take the mount down.{Environment.NewLine}{pool.MountLog}");

    // And when capacity comes back the file becomes readable again — waited on by READING it, not
    // by asking whether it exists. Those two answers disagree for a while here: the returning disk
    // is not live until the member poll notices it, and in that window a stat is answered from
    // cached metadata while an open finds no online copy and fails. Waiting on the stat therefore
    // returns immediately and proves nothing; the bytes are the claim.
    pool.Restore(1);
    byte[]? recovered = null;
    var shortAnswers = new List<int>();
    MountedPool.WaitUntil(() => {
      try {
        var got = File.ReadAllBytes(pool.PathTo("interrupted.bin"));
        if (got.Length != content.Length) {
          // A read that SUCCEEDS with fewer bytes than the file holds is the dangerous answer, and
          // it is worth catching separately: an error tells the application something is wrong,
          // whereas a short success tells it the file is simply that small. The disk coming back
          // used to produce exactly that — a stat answered from a remembered length of zero, so a
          // whole-file read returned nothing at all and reported no problem.
          shortAnswers.Add(got.Length);
          return false;
        }

        recovered = got;
        return true;
      } catch (IOException) {
        return false; // an honest refusal while the member is not live yet
      } catch (UnauthorizedAccessException) {
        return false;
      }
    }, TimeSpan.FromMinutes(2));

    shortAnswers.Should().BeEmpty(
      $"a read may fail while a returning disk is not live yet, but it must never SUCCEED with "
      + $"fewer bytes than the file holds — it answered with {string.Join(", ", shortAnswers)} "
      + $"against {content.Length}.{Environment.NewLine}{pool.DescribeMembers()}");

    recovered.Should().NotBeNull(
      $"the file must become readable again once the drain target returned."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    recovered.Should().Equal(content,
      $"and it must be byte-identical — an interrupted move may cost availability, never content."
      + $"{Environment.NewLine}{pool.DescribeMembers()}");

    // no half-copied staging file may be left showing in the pool as if it were the user's
    Directory.EnumerateFileSystemEntries(pool.MountPath, "*", SearchOption.AllDirectories)
      .Where(entry => entry.Contains("$DRIVEBENDER", StringComparison.OrdinalIgnoreCase))
      .Should().BeEmpty("an interrupted drain must not expose its staging file through the mount");
  }

  [Test]
  [Category("Exception")]
  [Description("An UNDUPLICATED pool whose placement target goes read-only still takes new files, by putting them on a member that can accept them.")]
  public void ReadOnly_GivenTheChosenMemberRefusesWrites_ThenNewFilesGoToOneThatDoesNot() {
    // The failure a real filesystem produces: ext4 hits a write error and remounts itself
    // read-only. Every read still answers correctly and quickly, every write is refused, and the
    // member is neither gone (the online probe keeps it) nor failing everything (the fault cooldown
    // never parks it). Creating a file used to commit to whichever member placement named first and
    // fail outright if it would not take it, so roughly half of all new files died with a bare
    // "access denied" while a perfectly writable member sat beside them.
    using var pool = MountedPool.Create(members: 2); // duplication 1: one copy, so one member suffices
    File.WriteAllBytes(pool.PathTo("before.bin"), _Payload(64 * 1024, 670));

    pool.Remount();
    if (!pool.MakeReadOnly(1))
      Assert.Ignore("This platform (or this member's filesystem) cannot be made read-only without going offline.");

    try {
      // enough files that placement would have chosen the read-only member for some of them
      for (var file = 0; file < 8; ++file) {
        var content = _Payload(64 * 1024, 680 + file);
        var writing = () => File.WriteAllBytes(pool.PathTo($"ro{file}.bin"), content);
        writing.Should().NotThrow(
          $"'ro{file}.bin' had a writable member available; a pool that refuses a file because ONE "
          + $"of its storages went read-only has turned a degraded disk into an outage."
          + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

        File.ReadAllBytes(pool.PathTo($"ro{file}.bin")).Should().Equal(content,
          $"'ro{file}.bin' must read back exactly what was written");
      }

      pool.IsMountAlive.Should().BeTrue(
        $"a member going read-only must not take the mount down.{Environment.NewLine}{pool.MountLog}");
    } finally {
      pool.Uncripple(1);
    }
  }

  [Test]
  [Category("Exception")]
  [Description("A DUPLICATED pool whose member goes read-only keeps serving every stored byte promptly, which is the half that must never regress.")]
  public void ReadOnly_GivenADuplicatedPool_ThenEverythingStoredIsStillServedPromptly() {
    using var pool = _DuplicatedPool();
    const int files = 4;

    var expected = new Dictionary<string, byte[]>();
    for (var file = 0; file < files; ++file) {
      var content = _Payload(128 * 1024, 660 + file);
      expected[$"ro{file}.bin"] = content;
      File.WriteAllBytes(pool.PathTo($"ro{file}.bin"), content);
    }

    MountedPool.WaitUntil(() => expected.Keys.All(n => pool.PhysicalCopies(n).Count >= 2), TimeSpan.FromMinutes(2))
      .Should().BeTrue($"both members must hold a copy first.{Environment.NewLine}{pool.DescribeMembers()}");

    pool.Remount(); // nothing held open from before the member turned read-only
    if (!pool.MakeReadOnly(1))
      Assert.Ignore("This platform (or this member's filesystem) cannot be made read-only without going offline.");

    try {
      foreach (var (name, content) in expected) {
        var stopwatch = Stopwatch.StartNew();
        var read = File.ReadAllBytes(pool.PathTo(name));
        stopwatch.Stop();

        read.Should().Equal(content,
          $"'{name}' sits on a member that can still READ; going read-only must not cost its content."
          + $"{Environment.NewLine}{pool.MountLog}");

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
          $"a read-only member still serves reads at its normal speed.{Environment.NewLine}{pool.MountLog}");
      }

      // NEW files are a different matter, and the current answer is deliberate rather than accidental:
      // the pool owes every file two durable copies, one member cannot take a write, and the degraded
      // path that would accept the shortfall keys on a member being UNREACHABLE — which this one is
      // not. So the write is refused rather than silently dropping to a single copy. Pinned as it
      // stands, because the alternative is a durability decision and not a bug fix; see docs/Issues.md.
      var writing = () => File.WriteAllBytes(pool.PathTo("fresh.bin"), _Payload(64 * 1024, 700));
      writing.Should().Throw<Exception>(
        "a duplicated pool cannot make two copies when one member refuses writes, and it says so "
        + "rather than quietly storing one");

      pool.IsMountAlive.Should().BeTrue(
        $"and refusing must not take the mount down.{Environment.NewLine}{pool.MountLog}");
    } finally {
      pool.Uncripple(1);
    }
  }

  [Test]
  [Category("Exception")]
  [Description("Every member goes away and one comes back: its content is served again rather than the pool staying dark.")]
  public void Eject_GivenEveryMemberGoesAndOneComesBack_ThenItsContentIsServedAgain() {
    using var pool = _DuplicatedPool();
    var content = _Payload(256 * 1024, 630);
    File.WriteAllBytes(pool.PathTo("blackout.bin"), content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("blackout.bin").Count >= 2, TimeSpan.FromMinutes(2));

    pool.Eject(0);
    pool.Eject(1); // total blackout: nothing is reachable

    pool.IsMountAlive.Should().BeTrue(
      $"a pool with no reachable storage must report failures, not die.{Environment.NewLine}{pool.MountLog}");

    pool.Restore(1); // the lights come back on one disk

    var served = MountedPool.WaitUntil(() => {
      try {
        return File.ReadAllBytes(pool.PathTo("blackout.bin")).SequenceEqual(content);
      } catch (IOException) {
        return false;
      } catch (UnauthorizedAccessException) {
        return false;
      }
    }, TimeSpan.FromMinutes(2));

    served.Should().BeTrue(
      $"a returning disk that holds the data must be served from again."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

}
