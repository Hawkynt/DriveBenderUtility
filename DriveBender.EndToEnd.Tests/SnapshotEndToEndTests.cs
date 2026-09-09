using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Snapshots through the shipped binary, on a real mounted pool (docs/Snapshots.md).
///
/// A snapshot records the pool at an instant and then stops the pool destroying what it recorded.
/// Everything here is driven the way an operator drives it — the CLI against a live mount — because
/// the engine keeps the pinned set in memory and the interesting failures are the ones where that
/// memory and the disks disagree.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class SnapshotEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  private static string _TakeSnapshot(MountedPool pool, string name) {
    var taken = DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-take", pool.PoolName, name);
    var id = taken.StandardOutput.Split(':').Last().Trim().Split('\n')[0].Trim();
    Guid.TryParse(id, out _).Should().BeTrue($"the verb must print the snapshot id: {taken.Output}");
    return id;
  }

  [Test]
  [Category("HappyPath")]
  [Description("A file overwritten after a snapshot is still recoverable from it, through the shipped CLI.")]
  public void Snapshot_GivenAFileIsOverwritten_ThenItCanBeRestoredFromTheSnapshot() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var original = _Payload(64 * 1024, 81);
    var path = pool.PathTo("books/ledger.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, original);

    var id = _TakeSnapshot(pool, "before-the-edit");

    File.WriteAllBytes(path, _Payload(32 * 1024, 82));
    File.ReadAllBytes(path).Should().NotEqual(original, "the live file really was replaced");

    var listed = DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-list", pool.PoolName, "--json");
    listed.StandardOutput.Should().Contain("before-the-edit", $"the snapshot must be listed: {listed.Output}");

    DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-restore", pool.PoolName, id, "books/ledger.db");

    File.ReadAllBytes(path).Should().Equal(original,
      $"restoring puts back exactly what the pool held when the snapshot was taken."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file deleted after a snapshot is still recoverable from it.")]
  public void Snapshot_GivenAFileIsDeleted_ThenItCanBeRestoredFromTheSnapshot() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(48 * 1024, 83);
    var path = pool.PathTo("gone.bin");
    File.WriteAllBytes(path, content);

    var id = _TakeSnapshot(pool, "before-the-delete");
    File.Delete(path);
    File.Exists(path).Should().BeFalse("the delete must take it out of the namespace");

    DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-restore", pool.PoolName, id, "gone.bin");

    File.Exists(path).Should().BeTrue(
      $"a snapshot that named the file must be able to bring it back."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
    File.ReadAllBytes(path).Should().Equal(content, "with the bytes it had at that instant");
  }

  [Test]
  [Category("Exception")]
  [Description("Removing a member from the pool takes its snapshot store with it, rather than discarding preserved versions.")]
  public void RemoveMedia_GivenTheMemberHoldsSnapshotVersions_ThenTheyAreStillRecoverable() {
    // The same shape as the recycle-bin bug: the scatter walks the visible namespace and skips the
    // pool's hidden tree, so a preserved version was leaving with the disk. A snapshot whose
    // versions can vanish when a disk is retired is not a snapshot.
    using var pool = MountedPool.Create(members: 3, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var original = _Payload(32 * 1024, 84);
    var path = pool.PathTo("quarterly.xlsx");
    File.WriteAllBytes(path, original);

    var id = _TakeSnapshot(pool, "before-the-swap");
    File.WriteAllBytes(path, _Payload(16 * 1024, 85));   // preserves the original somewhere

    var holder = Enumerable.Range(0, pool.MemberPaths.Count).First(i =>
      Directory.Exists(Path.Combine(pool.MemberPaths[i], ".drivebenderutility", "snapshots", "versions")));

    pool.WhileUnmounted(() => DbMount.RunExpectingSuccess(_CLI,
      "pool-remove-media", pool.PoolName, "--member", pool.MemberPaths[holder]));

    DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-restore", pool.PoolName, id, "quarterly.xlsx");

    File.ReadAllBytes(path).Should().Equal(original,
      $"the version was on the member that left, and retiring a disk must not destroy what a "
      + $"snapshot promised.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("Exception")]
  [Description("Snapshot verbs refuse clearly when the pool is not mounted, rather than writing state the running engine would not know about.")]
  public void Snapshot_GivenThePoolIsNotMounted_ThenTheVerbRefusesAndExplains() {
    using var pool = MountedPool.Create(members: 1);

    pool.WhileUnmounted(() => {
      var result = DbMount.Run(_CLI, "pool-snapshot-take", pool.PoolName, "offline");
      result.Succeeded.Should().BeFalse(
        $"the engine tracks pinned paths in memory; an index written behind its back names versions "
        + $"the next write would destroy.{Environment.NewLine}{result.Output}");
      result.Output.Should().Contain("not mounted", $"and the reason has to be legible: {result.Output}");
    });
  }

}
