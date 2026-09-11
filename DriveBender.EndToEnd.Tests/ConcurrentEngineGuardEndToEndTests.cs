using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Only one engine may write a pool's members at a time.
///
/// Mounting takes a cross-process lock for exactly this reason — "two engines over one member set
/// race and corrupt each other" — and a second mount is refused by it. The administrative verbs
/// open the same members and rewrite the same files, and they never went near that lock: run
/// `pool-restore` against a pool that is mounted and serving, and there are two engines relocating
/// copies, journalling to the same log and invalidating caches the other one cannot see.
///
/// The pool is mounted, so these must refuse and say where to run them instead. The daemon files
/// them through the mount process, which is the path that is safe.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class ConcurrentEngineGuardEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(2);

  [Test]
  [Category("EdgeCase")]
  [Description("An administrative verb run against a mounted pool is executed by the process that owns it, not by a second engine.")]
  public void Mounted_GivenRestoreIsRunFromTheCli_ThenItIsExecutedByTheOwningProcess() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    File.WriteAllBytes(pool.PathTo("live.bin"), new byte[4096]);

    var result = DbMount.Run(_CLI, "pool-restore", pool.PoolName);

    result.Succeeded.Should().BeTrue(
      $"refusing outright would be a dead end: restoring duplication is most wanted on a pool that "
      + $"is up and serving, and 'unmount your backup target first' is not an answer."
      + $"{Environment.NewLine}{result.Output}");

    result.Output.Should().Contain("inside the process that owns it",
      $"it must have been RELAYED rather than run here. Two engines over one member set race and "
      + $"corrupt each other, which is why mounting takes a lock — doing the work in this process "
      + $"would walk straight past it.{Environment.NewLine}{result.Output}");

    File.ReadAllBytes(pool.PathTo("live.bin")).Should().HaveCount(4096,
      $"and the running pool is untouched by it.{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("Exception")]
  [Description("A verb that changes the member set refuses against a mounted pool, and says where to run it.")]
  public void Mounted_GivenAVerbTheMountCannotRun_ThenItRefusesAndExplains() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    File.WriteAllBytes(pool.PathTo("live.bin"), new byte[4096]);

    // Changing the MEMBER SET is different in kind from the verbs that relay. Repairing bit rot or
    // restoring a file from the bin is work the running engine can do, and does, on request; taking
    // a disk out from under a live mount is not something to hand to the mount at all, so there is
    // no handler for it and there should not be. That has to read as a clear refusal rather than a
    // silent second engine writing the same members.
    var result = DbMount.Run(_CLI, "pool-remove-media", pool.PoolName, "--member", pool.MemberPaths[1]);

    result.Succeeded.Should().BeFalse(
      $"there is no safe way to run this against a mounted pool, so it must not run."
      + $"{Environment.NewLine}{result.Output}");

    result.Output.Should().Contain("mounted",
      $"refusing is only half of it: the operator has to be told why and what to do instead."
      + $"{Environment.NewLine}{result.Output}");

    // and the verbs that CAN be relayed are not caught by the same net — the guard exists to stop a
    // second engine, not to make a live pool unmanageable
    var relayed = DbMount.Run(_CLI, "pool-trash-list", pool.PoolName);
    relayed.Succeeded.Should().BeTrue(
      $"reading the recycle bin of a mounted pool has to work; it is the pool people actually use."
      + $"{Environment.NewLine}{relayed.Output}");
  }

  [Test]
  [Category("HappyPath")]
  [Description("The same verb runs normally once the pool is unmounted.")]
  public void Unmounted_GivenRestoreIsRunFromTheCli_ThenItProceeds() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    File.WriteAllBytes(pool.PathTo("live.bin"), new byte[4096]);

    pool.WhileUnmounted(() => {
      var result = DbMount.Run(_CLI, "pool-restore", pool.PoolName);
      result.Succeeded.Should().BeTrue(
        $"with nothing mounted there is only one engine, which is the whole point of the guard — it "
        + $"must not become a reason the verb can never be used.{Environment.NewLine}{result.Output}");
    });
  }

}
