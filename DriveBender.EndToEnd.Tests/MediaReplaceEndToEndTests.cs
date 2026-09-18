using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Swapping a disk for another one — the operation an operator reaches for when SMART starts
/// complaining and the disk is still readable.
///
/// It is the sibling of remove-media and it is NOT the same code: remove rebuilds the departing
/// member's contents from surviving copies elsewhere, while replace copies that member's files onto
/// the new disk one at a time and then swaps it into the manifest. So "remove is covered" says
/// nothing about this path, and the failure it can produce is worse — remove at least leaves the
/// data where it was, whereas replace ends by dropping the old disk from the pool. Anything it did
/// not carry across is gone at that moment, with no error and nothing to notice.
///
/// The recycle bin and the snapshot store live in the member's hidden tree, which is exactly the
/// thing a namespace walk is written to skip. That has already cost this pool two bugs on the remove
/// path — first the bin, then the versions.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class MediaReplaceEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);

  private const string _UTILITY = ".drivebenderutility";

  /// <summary>A pool whose deletes go to the recycle bin, so there is a bin to lose.</summary>
  private const string _TRASH_ON =
    """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "trash": { "enabled": true, "retention": "7d", "maxSize": "50%" } }""";

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  private static string _TakeSnapshot(MountedPool pool, string name) {
    var taken = DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-take", pool.PoolName, name);
    return taken.StandardOutput.Split(':').Last().Trim().Split('\n')[0].Trim();
  }

  /// <summary>A fresh empty folder beside the pool, standing in for the disk being swapped in.</summary>
  private static string _NewDisk(MountedPool pool, string name) {
    var path = Path.Combine(pool.Root, name);
    Directory.CreateDirectory(path);
    return path;
  }

  /// <summary>
  /// The first member index whose hidden tree holds a file matching <paramref name="pattern"/> under
  /// <paramref name="folder"/>, or -1.
  ///
  /// The pattern matters, and a first draft of the snapshot scenario below proved why: it looked for
  /// ANY file under <c>snapshots/</c>, found the member holding the index rather than the one holding
  /// the stored version, replaced that disk and passed — while the bytes it was supposed to be
  /// protecting sat untouched on the other member. A scenario that can pass without exercising the
  /// thing it names is worse than no scenario, because it reports coverage that does not exist.
  /// </summary>
  private static int _HolderOf(MountedPool pool, string folder, string pattern = "*") {
    for (var index = 0; index < pool.MemberPaths.Count; ++index) {
      var tree = Path.Combine(pool.MemberPaths[index], _UTILITY, folder);
      if (Directory.Exists(tree) && Directory.EnumerateFiles(tree, pattern, SearchOption.AllDirectories).Any())
        return index;
    }

    return -1;
  }

  [Test]
  [Category("HappyPath")]
  [Description("Replacing a disk carries the ordinary files across and the pool serves them from the new one.")]
  public void Replace_GivenAMemberHoldsFiles_ThenTheyAreServedFromTheReplacement() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(64 * 1024, 301);
    File.WriteAllBytes(pool.PathTo("ledger.db"), content);
    pool.WaitForPhysicalCopies("ledger.db", atLeast: 2, TimeSpan.FromMinutes(2));

    var replacement = _NewDisk(pool, "swap-in");
    pool.WhileUnmounted(() => DbMount.RunExpectingSuccess(_CLI,
      "pool-replace-media", pool.PoolName, "--old", pool.MemberPaths[0], "--new", replacement));

    File.ReadAllBytes(pool.PathTo("ledger.db")).Should().Equal(content,
      $"a swapped disk must carry its files onto the new one — the old disk leaves the manifest at "
      + $"the end of this, so anything not carried across is gone."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    Directory.EnumerateFiles(replacement, "*", SearchOption.AllDirectories).Should().NotBeEmpty(
      $"and the replacement is actually holding something, rather than the pool quietly serving "
      + $"everything from the other member.{Environment.NewLine}{pool.DescribeMembers()}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Replacing the disk that holds a snapshot's preserved versions: the snapshot is still restorable afterwards.")]
  public void Replace_GivenTheMemberHoldsSnapshotVersions_ThenTheyAreStillRecoverable() {
    // The same shape as the two bugs the REMOVE path already had, on the path that was never
    // checked. A snapshot whose versions can vanish because an operator swapped a disk is not a
    // snapshot — and replace is the one media operation that ends with the old disk gone.
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var original = _Payload(32 * 1024, 302);
    var path = pool.PathTo("quarterly.xlsx");
    File.WriteAllBytes(path, original);

    var id = _TakeSnapshot(pool, "before-the-swap");
    File.WriteAllBytes(path, _Payload(16 * 1024, 303)); // preserves the original into the store

    // the member holding the stored VERSION, not merely one holding the index
    var holder = _HolderOf(pool, "snapshots", "*.snapver");
    holder.Should().BeGreaterThanOrEqualTo(0,
      $"a version must have been stored before losing one can mean anything."
      + $"{Environment.NewLine}{pool.DescribeMembers()}");

    var replacement = _NewDisk(pool, "swap-in-snap");
    pool.WhileUnmounted(() => DbMount.RunExpectingSuccess(_CLI,
      "pool-replace-media", pool.PoolName, "--old", pool.MemberPaths[holder], "--new", replacement));

    DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-restore", pool.PoolName, id, "quarterly.xlsx");

    File.ReadAllBytes(path).Should().Equal(original,
      $"the preserved version was on the disk that was swapped out, and swapping a disk must not "
      + $"destroy what a snapshot promised."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Replacing the disk that holds the recycle bin: deleted files are still listed and still restorable.")]
  public void Replace_GivenTheMemberHoldsTheRecycleBin_ThenDeletedFilesAreStillRecoverable() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);
    var content = _Payload(24 * 1024, 304);
    File.WriteAllBytes(pool.PathTo("invoice.pdf"), content);
    pool.WaitForPhysicalCopies("invoice.pdf", atLeast: 1, TimeSpan.FromMinutes(2));

    File.Delete(pool.PathTo("invoice.pdf"));
    MountedPool.WaitUntil(() => _HolderOf(pool, "trash") >= 0, TimeSpan.FromMinutes(1))
      .Should().BeTrue($"the delete must reach the bin.{Environment.NewLine}{pool.DescribeMembers()}");

    var holder = _HolderOf(pool, "trash");
    var replacement = _NewDisk(pool, "swap-in-bin");
    pool.WhileUnmounted(() => DbMount.RunExpectingSuccess(_CLI,
      "pool-replace-media", pool.PoolName, "--old", pool.MemberPaths[holder], "--new", replacement));

    var listed = DbMount.RunExpectingSuccess(_CLI, "pool-trash-list", pool.PoolName, "--json");
    listed.StandardOutput.Should().Contain("invoice.pdf",
      $"a deleted file the user could have recovered a minute ago must not disappear because a disk "
      + $"was swapped.{Environment.NewLine}{listed.Output}{Environment.NewLine}{pool.MountLog}");

    DbMount.RunExpectingSuccess(_CLI, "pool-trash-restore", pool.PoolName, "invoice.pdf");
    File.ReadAllBytes(pool.PathTo("invoice.pdf")).Should().Equal(content,
      $"and it restores with the bytes it was deleted with."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("Exception")]
  [Description("Replacing a member that is not there is refused: its data would be abandoned rather than migrated.")]
  public void Replace_GivenTheOldMemberIsOffline_ThenItIsRefusedRatherThanAbandoningItsData() {
    // Replace reads the departing disk to copy it. A source that cannot be read migrates nothing,
    // and the operation would then "succeed" and drop it from the manifest — abandoning everything
    // on it. Refusing sends the operator to remove-media, which rebuilds from surviving copies.
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    File.WriteAllBytes(pool.PathTo("ledger.db"), _Payload(16 * 1024, 305));
    pool.WaitForPhysicalCopies("ledger.db", atLeast: 2, TimeSpan.FromMinutes(2));

    var gone = pool.MemberPaths[1];
    var replacement = _NewDisk(pool, "swap-in-offline");

    pool.Eject(1);
    try {
      var result = DbMount.Run(_CLI, "pool-replace-media", pool.PoolName, "--old", gone, "--new", replacement);
      result.Succeeded.Should().BeFalse(
        $"a disk that cannot be read cannot be migrated, and saying otherwise loses everything on it."
        + $"{Environment.NewLine}{result.Output}");
    } finally {
      pool.Restore(1);
    }

    File.ReadAllBytes(pool.PathTo("ledger.db")).Should().HaveCount(16 * 1024,
      $"and the refusal costs nothing — the pool is exactly as it was."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

}
