using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// What a file carries BESIDES its bytes, through everything the pool does to it.
///
/// A pool that returns the right bytes under the wrong metadata is not a backup, it is a copy. A
/// restore is judged on whether the files came back as they were — and a modification time is not
/// decoration: it is what every incremental backup, every build system and every sync tool uses to
/// decide whether a file changed. The pool moves data on its own schedule (a drain down a tier, a
/// duplication heal, a media replace), and none of that is something the user did to the file.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class MetadataFidelityEndToEndTests {

  /// <summary>Distinctive, in the past, and not on a boundary — so a reset to "now" is unmistakable.</summary>
  private static readonly DateTime _STAMP = new(2019, 3, 14, 15, 9, 26, DateTimeKind.Utc);

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  private static IReadOnlyList<int> _MembersHolding(MountedPool pool, string name) {
    var holders = new List<int>();
    for (var index = 0; index < pool.MemberPaths.Count; ++index)
      if (File.Exists(Path.Combine(pool.MemberPaths[index], name)))
        holders.Add(index);

    return holders;
  }

  [Test]
  [Category("HappyPath")]
  [Description("A file's modification time survives a write and read back through the mount.")]
  public void Timestamps_GivenAFileIsStamped_ThenTheMountReportsItBack() {
    using var pool = MountedPool.Create(members: 1);
    var path = pool.PathTo("stamped.bin");

    File.WriteAllBytes(path, _Payload(4096, 71));
    File.SetLastWriteTimeUtc(path, _STAMP);

    File.GetLastWriteTimeUtc(path).Should().BeCloseTo(_STAMP, TimeSpan.FromSeconds(1),
      $"a filesystem that cannot hold a timestamp cannot be restored from."
      + $"{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("The drainer moving a file down a tier does not change its modification time.")]
  public void Timestamps_WhenTheDrainerMovesTheFile_ThenItsModificationTimeIsUnchanged() {
    using var pool = MountedPool.CreateTieredAlwaysLanding();
    var path = pool.PathTo("aged.bin");

    File.WriteAllBytes(path, _Payload(2 * 1024 * 1024, 72));
    File.SetLastWriteTimeUtc(path, _STAMP);
    pool.RequireLandedOnFastTier("aged.bin");

    MountedPool.WaitUntil(() => _MembersHolding(pool, "aged.bin").Contains(1), TimeSpan.FromMinutes(2))
      .Should().BeTrue($"the drainer must move it down for this to test anything."
        + $"{Environment.NewLine}{pool.DescribeMembers()}");

    File.GetLastWriteTimeUtc(path).Should().BeCloseTo(_STAMP, TimeSpan.FromSeconds(1),
      $"the pool relocated this file on its own schedule; the user did not touch it. A backup whose "
      + $"timestamps change when the storage rebalances cannot answer 'what changed since last "
      + $"time', which is the question every incremental backup asks."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A duplication heal creating a second copy does not change the file's modification time.")]
  public void Timestamps_WhenTheHealerDuplicatesTheFile_ThenItsModificationTimeIsUnchanged() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var path = pool.PathTo("healed.bin");

    File.WriteAllBytes(path, _Payload(1024 * 1024, 73));
    MountedPool.WaitUntil(() => pool.PhysicalCopies("healed.bin").Count >= 2, TimeSpan.FromMinutes(2))
      .Should().BeTrue($"the file must start duplicated.{Environment.NewLine}{pool.DescribeMembers()}");

    File.SetLastWriteTimeUtc(path, _STAMP);

    // drop one copy behind the pool's back and let it heal the file back to duplication
    pool.Eject(1);
    foreach (var candidate in Directory.EnumerateFiles(pool.StoragePaths[1], "healed.bin", SearchOption.AllDirectories))
      File.Delete(candidate);

    pool.Restore(1);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("healed.bin").Count >= 2, TimeSpan.FromMinutes(3))
      .Should().BeTrue($"the healer must restore the second copy."
        + $"{Environment.NewLine}{pool.DescribeMembers()}");

    File.GetLastWriteTimeUtc(path).Should().BeCloseTo(_STAMP, TimeSpan.FromSeconds(1),
      $"restoring redundancy is the pool's own housekeeping and must be invisible in the file's "
      + $"metadata.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  /// <summary>
  /// A tree with the shapes a real backup is full of and a synthetic test usually skips: nothing,
  /// almost nothing, exactly a block, an empty folder, a deep path, a name that is not ASCII.
  ///
  /// Sizes sit ON the block boundary and either side of it, because that is where an off-by-one in
  /// the block layer shows up and nowhere else.
  /// </summary>
  private static readonly (string path, int size)[] _TREE = [
    ("empty.bin", 0),
    ("one-byte.bin", 1),
    ("block-under.bin", 4095),
    ("block-exact.bin", 4096),
    ("block-over.bin", 4097),
    ("a file with spaces.txt", 300),
    ("ünïcøde-ファイル.bin", 1200),
    ("nested/one/two/three/four/deep.bin", 8192),
    ("nested/one/sibling.bin", 64),
    ("nested/empty-inside/kept.bin", 7),
  ];

  private static readonly string[] _EMPTY_FOLDERS = ["empty-folder", "nested/one/two/empty-too"];

  private static Dictionary<string, byte[]> _BuildTree(MountedPool pool) {
    var written = new Dictionary<string, byte[]>();
    foreach (var folder in _EMPTY_FOLDERS)
      Directory.CreateDirectory(pool.PathTo(folder));

    foreach (var (relative, size) in _TREE) {
      var full = pool.PathTo(relative);
      Directory.CreateDirectory(Path.GetDirectoryName(full)!);
      var content = _Payload(size, size + relative.Length);
      File.WriteAllBytes(full, content);
      File.SetLastWriteTimeUtc(full, _STAMP.AddSeconds(size));
      written[relative] = content;
    }

    return written;
  }

  private static void _VerifyTree(MountedPool pool, Dictionary<string, byte[]> written, string when) {
    foreach (var folder in _EMPTY_FOLDERS)
      Directory.Exists(pool.PathTo(folder)).Should().BeTrue(
        $"the empty folder '{folder}' must still exist {when}. An empty directory carries no bytes, "
        + $"which is exactly why a pool that keys everything off files can lose it — and a restore "
        + $"that silently drops empty folders is not a restore."
        + $"{Environment.NewLine}{pool.DescribeMembers()}");

    foreach (var (relative, content) in written) {
      var full = pool.PathTo(relative);
      File.Exists(full).Should().BeTrue($"'{relative}' must exist {when}.{Environment.NewLine}{pool.DescribeMembers()}");
      File.ReadAllBytes(full).Should().Equal(content, $"'{relative}' must be byte-for-byte identical {when}");
      File.GetLastWriteTimeUtc(full).Should().BeCloseTo(_STAMP.AddSeconds(content.Length), TimeSpan.FromSeconds(1),
        $"'{relative}' must keep the time it was stamped with {when}");
    }

    // and nothing the pool uses for its own bookkeeping shows through
    var visible = Directory.EnumerateFileSystemEntries(pool.MountPath, "*", SearchOption.AllDirectories)
      .Select(Path.GetFileName)
      .ToList();

    visible.Should().NotContain(n => n!.Contains("$DRIVEBENDER", StringComparison.OrdinalIgnoreCase),
      $"the pool's own on-disk names must never appear in the namespace {when}");
  }

  [Test]
  [Category("HappyPath")]
  [Description("A whole tree — empty files, empty folders, block-boundary sizes, deep paths, non-ASCII names — survives a remount and the pool's own rebalancing byte-for-byte and stamp-for-stamp.")]
  public void Tree_GivenAWholeTreeIsWritten_ThenItSurvivesRemountAndRebalancingIntact() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);

    var written = _BuildTree(pool);
    _VerifyTree(pool, written, "as written");

    // 1. a clean unmount and remount: what the pool believes is entirely reloaded from the members
    pool.Remount();
    _VerifyTree(pool, written, "after a remount");

    // 2. the pool's own housekeeping: a member is lost and rebuilt, so every file is copied by the
    //    healer rather than by the user
    pool.Eject(1);
    foreach (var candidate in Directory.EnumerateFiles(pool.StoragePaths[1], "*", SearchOption.AllDirectories))
      try {
        File.Delete(candidate);
      } catch (IOException) {
        // the member's own bookkeeping may be held open; the data files are what matter here
      }

    pool.Restore(1);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("block-exact.bin").Count >= 2, TimeSpan.FromMinutes(3))
      .Should().BeTrue($"the healer must rebuild the emptied member."
        + $"{Environment.NewLine}{pool.DescribeMembers()}");

    _VerifyTree(pool, written, "after the pool rebuilt a member");
  }

}
