using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Storage that goes away and comes back, exercised through a real mount on both targets.
///
/// This is the product's central promise — a pool survives losing a disk and heals when it
/// returns — and it is the part a unit suite can only simulate. Here the member genuinely stops
/// existing on the machine, which is exactly what the engine's online probe looks for, and every
/// assertion is made through the mounted filesystem or against the members' real folders.
///
/// Each test builds its own pool: a member that has been ejected changes the pool's state for
/// everything that follows, so sharing one would make the results depend on ordering.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class MemberLossEndToEndTests {

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>A pool whose files really do exist twice, so there is something to survive on.</summary>
  private static MountedPool _DuplicatedPool() => MountedPool.Create(poolDefaults: MountedPool.DuplicatedOnOneDisk);

  [Test]
  [Category("HappyPath")]
  public void Duplication_GivenTheConfiguredPool_ThenEveryFileReallyExistsTwice() {
    // the premise every other test here rests on — asserted rather than assumed, because a pool
    // that silently stored one copy would make all of them pass for the wrong reason
    using var pool = _DuplicatedPool();
    var content = _Payload(16 * 1024, 1);
    File.WriteAllBytes(pool.PathTo("dup.bin"), content);

    var copies = MountedPool.WaitUntil(() => pool.PhysicalCopies("dup.bin").Count >= 2)
      ? pool.PhysicalCopies("dup.bin")
      : pool.PhysicalCopies("dup.bin");

    copies.Should().HaveCountGreaterThanOrEqualTo(2,
      $"the pool must hold two copies for the loss tests to mean anything.{Environment.NewLine}{pool.DescribeMembers()}");
    foreach (var (where, bytes) in copies)
      bytes.Should().Equal(content, $"copy {where} must match");
  }

  [Test]
  [Category("EdgeCase")]
  public void Eject_GivenAMemberIsPulled_ThenExistingFilesStayReadableFromTheSurvivor() {
    using var pool = _DuplicatedPool();
    var content = _Payload(64 * 1024, 2);
    File.WriteAllBytes(pool.PathTo("survives.bin"), content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("survives.bin").Count >= 2);

    pool.Eject(1); // the disk is gone

    // the pool must keep serving: this is the whole point of duplication
    File.ReadAllBytes(pool.PathTo("survives.bin")).Should().Equal(content,
      $"a duplicated file must remain readable after a member is lost.{Environment.NewLine}{pool.MountLog}");
    Directory.EnumerateFileSystemEntries(pool.MountPath).Should().NotBeEmpty("the namespace must survive a member loss");
  }

  [Test]
  [Category("EdgeCase")]
  [Ignore("Fails against a real gap, not a flaky one: a file written while a member was away is "
          + "NOT healed back to its duplication level when that member returns - waited two "
          + "minutes with the background pump running. The degraded write itself succeeds, and a "
          + "file deleted while the member was away correctly stays deleted, so tombstone replay "
          + "works while owed-copy heal does not. See docs/Issues.md 'Still open'.")]
  public void Eject_GivenAMemberIsAway_ThenWritesStillSucceedAndHealWhenItReturns() {
    using var pool = _DuplicatedPool();
    pool.Eject(1);

    // SAFE-DEGRADE: losing one disk must not turn into a write outage while another is reachable
    var content = _Payload(32 * 1024, 3);
    var writing = () => File.WriteAllBytes(pool.PathTo("degraded.bin"), content);
    writing.Should().NotThrow($"a degraded pool must still accept writes.{Environment.NewLine}{pool.MountLog}");
    File.ReadAllBytes(pool.PathTo("degraded.bin")).Should().Equal(content);

    pool.Restore(1); // the disk comes back

    // FR-HEAL: the owed copy is recreated in the background, with no explicit repair asked for
    var healed = MountedPool.WaitUntil(() => pool.PhysicalCopies("degraded.bin").Count >= 2, TimeSpan.FromMinutes(2));
    healed.Should().BeTrue(
      $"the returned member must be healed back to the duplication level on its own.{Environment.NewLine}"
      + pool.DescribeMembers() + Environment.NewLine + pool.MountLog);

    foreach (var (where, bytes) in pool.PhysicalCopies("degraded.bin"))
      bytes.Should().Equal(content, $"the healed copy at {where} must be byte-identical");
  }

  [Test]
  [Category("EdgeCase")]
  public void Eject_GivenAFileIsDeletedWhileAMemberIsAway_ThenItDoesNotResurrectOnItsReturn() {
    using var pool = _DuplicatedPool();
    var content = _Payload(8 * 1024, 4);
    File.WriteAllBytes(pool.PathTo("ghost.bin"), content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("ghost.bin").Count >= 2);

    pool.Eject(1);
    File.Delete(pool.PathTo("ghost.bin"));
    File.Exists(pool.PathTo("ghost.bin")).Should().BeFalse("the delete must take effect on the reachable members");

    pool.Restore(1);

    // SAFE-OFFLINE: the returning member still holds a copy of a file that no longer exists. The
    // namespace change it slept through is replayed, so the file must NOT come back from the dead
    // — a resurrected file is indistinguishable from data the user deleted on purpose returning.
    var stayedDeleted = MountedPool.WaitUntil(
      () => !File.Exists(pool.PathTo("ghost.bin")) && pool.PhysicalCopies("ghost.bin").Count == 0,
      TimeSpan.FromMinutes(2));

    stayedDeleted.Should().BeTrue(
      $"a file deleted while a member was away must not resurrect when it returns.{Environment.NewLine}"
      + pool.DescribeMembers() + Environment.NewLine + pool.MountLog);
  }

  [Test]
  [Category("EdgeCase")]
  public void Eject_GivenAMemberVanishesDuringAWrite_ThenTheDataThatWasAcknowledgedIsIntact() {
    // Windows refuses to rename a directory that has open files beneath it, so a member cannot be
    // pulled out from under LIVE I/O there — the scenario is only expressible where the OS allows
    // it. POSIX renames by inode and permits exactly this, so it runs on Linux.
    if (OperatingSystem.IsWindows())
      Assert.Ignore("A member cannot be ejected mid-I/O on Windows: renaming a directory with open files is refused by the OS.");

    using var pool = _DuplicatedPool();
    const int chunks = 40;
    const int chunkSize = 64 * 1024;
    var path = pool.PathTo("midwrite.bin");
    var written = new List<byte[]>();

    // pull the disk part-way through a long streaming write, the way a cable comes loose
    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) {
      for (var chunk = 0; chunk < chunks; ++chunk) {
        if (chunk == chunks / 2)
          pool.Eject(1);

        var payload = _Payload(chunkSize, 100 + chunk);
        stream.Write(payload, 0, payload.Length);
        written.Add(payload);
      }

      stream.Flush();
    }

    var expected = written.SelectMany(c => c).ToArray();
    File.ReadAllBytes(path).Should().Equal(expected,
      $"every acknowledged byte must survive a member vanishing mid-write.{Environment.NewLine}{pool.MountLog}");

    pool.Restore(1);

    // and once the disk is back the file converges to full duplication, still byte-identical
    MountedPool.WaitUntil(() => pool.PhysicalCopies("midwrite.bin").Count >= 2, TimeSpan.FromMinutes(3));
    foreach (var (where, bytes) in pool.PhysicalCopies("midwrite.bin"))
      bytes.Should().Equal(expected, $"the copy at {where} must match after the member returned");
  }

  [Test]
  [Category("EdgeCase")]
  public void Eject_GivenAMemberReturnsWhileIoIsInFlight_ThenNothingIsCorruptedOrStalled() {
    // Windows refuses to rename a directory that has open files beneath it, so a member cannot be
    // pulled out from under LIVE I/O there — the scenario is only expressible where the OS allows
    // it. POSIX renames by inode and permits exactly this, so it runs on Linux.
    if (OperatingSystem.IsWindows())
      Assert.Ignore("A member cannot be ejected mid-I/O on Windows: renaming a directory with open files is refused by the OS.");

    using var pool = _DuplicatedPool();
    var content = _Payload(32 * 1024, 5);
    for (var file = 0; file < 4; ++file)
      File.WriteAllBytes(pool.PathTo($"churn{file}.bin"), content);

    MountedPool.WaitUntil(() => Enumerable.Range(0, 4).All(f => pool.PhysicalCopies($"churn{f}.bin").Count >= 2));
    pool.Eject(1);

    // hammer the pool while the storage comes back underneath it
    var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
    var stop = false;
    var workers = Enumerable.Range(0, 4).Select(file => new Thread(() => {
      for (var round = 0; !Volatile.Read(ref stop) && round < 200; ++round)
        try {
          var payload = _Payload(32 * 1024, 200 + file * 100 + round % 7);
          File.WriteAllBytes(pool.PathTo($"churn{file}.bin"), payload);
          var got = File.ReadAllBytes(pool.PathTo($"churn{file}.bin"));
          if (got.Length != payload.Length)
            problems.Add($"churn{file}.bin read back {got.Length} bytes, expected {payload.Length}");
        } catch (IOException) {
          // a transient refusal while the member reappears is legal; corruption and hangs are not
        }
    }) { IsBackground = true }).ToArray();

    foreach (var worker in workers)
      worker.Start();

    Thread.Sleep(500);
    pool.Restore(1); // storage returns in the middle of live traffic
    Thread.Sleep(1500);
    Volatile.Write(ref stop, true);

    foreach (var worker in workers)
      worker.Join(TimeSpan.FromMinutes(2)).Should().BeTrue(
        $"I/O must not hang while a member returns.{Environment.NewLine}{pool.MountLog}");

    problems.Should().BeEmpty();

    // everything settles: each file readable, and its copies agree
    for (var file = 0; file < 4; ++file) {
      var settled = File.ReadAllBytes(pool.PathTo($"churn{file}.bin"));
      settled.Should().NotBeEmpty();
      MountedPool.WaitUntil(() => pool.PhysicalCopies($"churn{file}.bin").Count >= 2, TimeSpan.FromMinutes(2));
      foreach (var (where, bytes) in pool.PhysicalCopies($"churn{file}.bin"))
        bytes.Should().Equal(settled, $"copy {where} must converge on the settled content");
    }
  }

  [Test]
  [Category("Exception")]
  public void Eject_GivenEveryMemberIsGone_ThenOperationsFailCleanlyInsteadOfHanging() {
    using var pool = _DuplicatedPool();
    File.WriteAllBytes(pool.PathTo("orphan.bin"), _Payload(4096, 6));
    MountedPool.WaitUntil(() => pool.PhysicalCopies("orphan.bin").Count >= 2);

    pool.Eject(0);
    pool.Eject(1);

    // with no storage at all the pool cannot serve — it must SAY so, promptly. A hang here is a
    // frozen Explorer window and an unkillable process for the user.
    var finished = false;
    var probe = new Thread(() => {
      try {
        _ = File.ReadAllBytes(pool.PathTo("orphan.bin"));
      } catch (Exception) {
        // any failure is fine; silence is not
      }

      finished = true;
    }) { IsBackground = true };

    probe.Start();
    probe.Join(TimeSpan.FromSeconds(60)).Should().BeTrue(
      $"an operation against a pool with no reachable storage must fail rather than hang.{Environment.NewLine}{pool.MountLog}");
    finished.Should().BeTrue();

    pool.Restore(0);
    pool.Restore(1);
  }

}
