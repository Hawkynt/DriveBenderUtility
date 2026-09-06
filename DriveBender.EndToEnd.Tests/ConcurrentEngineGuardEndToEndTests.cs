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
  [Description("A verb the mount process cannot run refuses against a mounted pool, and says where to run it.")]
  public void Mounted_GivenAVerbTheMountCannotRun_ThenItRefusesAndExplains() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    File.WriteAllBytes(pool.PathTo("live.bin"), new byte[4096]);

    // restoring from the recycle bin has no handler inside the mount process, so there is nowhere
    // safe to relay it to — which must read as a clear refusal rather than a silent second engine
    var result = DbMount.Run(_CLI, "pool-trash-restore", pool.PoolName, "live.bin");

    result.Succeeded.Should().BeFalse(
      $"there is no safe way to run this against a mounted pool, so it must not run."
      + $"{Environment.NewLine}{result.Output}");

    result.Output.Should().Contain("mounted",
      $"refusing is only half of it: the operator has to be told why and what to do instead."
      + $"{Environment.NewLine}{result.Output}");
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
