using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Browsing snapshots from inside the mounted pool, with nothing but the ordinary file APIs.
///
/// The engine suite proves the tree resolves; this proves the DRIVER carries it — that a
/// <c>.snapshots/&lt;name&gt;/&lt;path&gt;</c> lookup survives the kernel, that the refusals arrive as EACCES
/// rather than EIO, and above all that a walk of the pool does not descend into it. The last one is
/// the reason the tree can be left switched on: if it showed up in a listing, every backup and sync
/// tool pointed at the pool would copy every version of every file it has ever held, on every run.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class SnapshotBrowsingEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  private static void _TakeSnapshot(MountedPool pool, string name)
    => DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-take", pool.PoolName, name);

  /// <summary>The snapshot view of a path, as a host path a file manager would open.</summary>
  private static string _InSnapshot(MountedPool pool, string snapshot, string path)
    => pool.PathTo(Path.Combine(".snapshots", snapshot, path));

  [Test]
  [Category("HappyPath")]
  [Description("An overwritten file's old content reads back through .snapshots with File.ReadAllBytes, no tooling involved.")]
  public void Browse_GivenAFileWasOverwritten_ThenTheOldOneOpensFromTheSnapshotFolder() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var original = _Payload(96 * 1024, 91);
    var live = pool.PathTo("books/ledger.db");
    Directory.CreateDirectory(Path.GetDirectoryName(live)!);
    File.WriteAllBytes(live, original);

    _TakeSnapshot(pool, "before-the-edit");
    File.WriteAllBytes(live, _Payload(8 * 1024, 92));

    var past = _InSnapshot(pool, "before-the-edit", "books/ledger.db");
    File.Exists(past).Should().BeTrue(
      $"the snapshot's copy has to be reachable by path, or recovering a file needs an administrator."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    new FileInfo(past).Length.Should().Be(original.Length,
      "and it has to stat as the file the snapshot holds, not as the live one — a caller that "
      + "trusted the live length would read a truncated file and never know");

    File.ReadAllBytes(past).Should().Equal(original,
      $"reading it returns exactly what the pool held at that instant."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    // and copying it back is the whole point: a drag-and-drop in a file manager, nothing else
    var recovered = pool.PathTo("books/ledger.recovered.db");
    File.Copy(past, recovered);
    File.ReadAllBytes(recovered).Should().Equal(original, "a plain copy out of the view restores the file");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A walk of the whole pool never descends into the snapshot view.")]
  public void Browse_GivenARecursiveWalk_ThenTheSnapshotViewIsNotPartOfIt() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    File.WriteAllBytes(pool.PathTo("one.bin"), _Payload(4096, 93));
    _TakeSnapshot(pool, "nightly");
    File.WriteAllBytes(pool.PathTo("one.bin"), _Payload(2048, 94));

    // exactly what a backup tool does
    var walked = Directory.EnumerateFileSystemEntries(pool.MountPath, "*", SearchOption.AllDirectories).ToList();

    walked.Should().NotContain(entry => entry.Contains(".snapshots", StringComparison.Ordinal),
      $"a walk must not find the view — every version of every file would otherwise be backed up "
      + $"alongside the live pool, on every run."
      + $"{Environment.NewLine}{string.Join(Environment.NewLine, walked)}{Environment.NewLine}{pool.MountLog}");

    Directory.EnumerateFileSystemEntries(pool.MountPath).Select(Path.GetFileName)
      .Should().NotContain(".snapshots", "not at the root either, which is where a tool starts");

    // invisible, but not absent: asked for by name, it is there
    Directory.Exists(_InSnapshot(pool, "nightly", "")).Should().BeTrue(
      $"the view is hidden from a walk, not switched off."
      + $"{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("HappyPath")]
  [Description("The snapshot view can be walked by hand: snapshots, then folders, then files.")]
  public void Browse_GivenNestedContent_ThenEachLevelOfTheViewListsWhatIsUnderIt() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    Directory.CreateDirectory(pool.PathTo("work/2026"));
    File.WriteAllBytes(pool.PathTo("work/2026/q1.xlsx"), _Payload(1024, 95));
    File.WriteAllBytes(pool.PathTo("work/notes.md"), _Payload(512, 96));

    _TakeSnapshot(pool, "monday");
    _TakeSnapshot(pool, "tuesday");

    Directory.EnumerateFileSystemEntries(pool.PathTo(".snapshots")).Select(Path.GetFileName)
      .Should().BeEquivalentTo(["monday", "tuesday"],
        $"the top of the view is one folder per snapshot.{Environment.NewLine}{pool.MountLog}");

    Directory.EnumerateFileSystemEntries(_InSnapshot(pool, "monday", "work")).Select(Path.GetFileName)
      .Should().BeEquivalentTo(["2026", "notes.md"],
        $"and the folders under it walk like any other.{Environment.NewLine}{pool.MountLog}");

    File.Exists(_InSnapshot(pool, "monday", "work/2026/q1.xlsx")).Should().BeTrue();
  }

  [Test]
  [Category("Exception")]
  [Description("Nothing under the snapshot view can be written, deleted or renamed — and the refusal is a permission error, not an I/O error.")]
  public void Browse_GivenAnAttemptToChangeTheView_ThenItIsRefusedAsAPermissionProblem() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(4096, 97);
    File.WriteAllBytes(pool.PathTo("report.doc"), content);
    _TakeSnapshot(pool, "monday");
    File.WriteAllBytes(pool.PathTo("report.doc"), _Payload(64, 98));

    var past = _InSnapshot(pool, "monday", "report.doc");

    // UnauthorizedAccessException is what EACCES becomes; IOException would mean the driver returned
    // EIO, which tells the application nothing and invites it to retry forever.
    var overwrite = () => File.WriteAllBytes(past, [1, 2, 3]);
    overwrite.Should().Throw<UnauthorizedAccessException>(
      $"history cannot be edited, and saying so plainly is the only honest answer."
      + $"{Environment.NewLine}{pool.MountLog}");

    var delete = () => File.Delete(past);
    delete.Should().Throw<UnauthorizedAccessException>("nor deleted");

    var rename = () => File.Move(past, pool.PathTo("stolen.doc"));
    rename.Should().Throw<Exception>("nor moved out of the view");

    var plant = () => File.WriteAllBytes(_InSnapshot(pool, "monday", "planted.doc"), [1]);
    plant.Should().Throw<UnauthorizedAccessException>("nor added to");

    var makeDir = () => Directory.CreateDirectory(_InSnapshot(pool, "monday", "sub"));
    makeDir.Should().Throw<UnauthorizedAccessException>("nor given new folders");

    File.ReadAllBytes(past).Should().Equal(content,
      $"and after every one of those, the snapshot holds exactly what it held."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A snapshot that no longer exists has no folder in the view, and one that never existed reads as not found.")]
  public void Browse_GivenAMissingSnapshot_ThenTheViewSaysNotFoundRatherThanFailing() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    File.WriteAllBytes(pool.PathTo("one.bin"), _Payload(4096, 99));

    Directory.Exists(pool.PathTo(".snapshots/never-taken")).Should().BeFalse(
      $"a snapshot nobody took is simply not there.{Environment.NewLine}{pool.MountLog}");

    var read = () => File.ReadAllBytes(_InSnapshot(pool, "never-taken", "one.bin"));
    read.Should().Throw<Exception>("and reading through it fails rather than returning the live file");

    Directory.Exists(pool.PathTo(".snapshots")).Should().BeTrue(
      $"while the view itself exists whether or not anything has been taken — a user checking for "
      + $"snapshots should be told there are none, not that the folder is missing."
      + $"{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file deleted after the snapshot is still openable through the view, and copying it back is the whole recovery.")]
  public void Browse_GivenADeletedFile_ThenRecoveryIsACopyOutOfTheView() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(128 * 1024, 100);
    File.WriteAllBytes(pool.PathTo("invoice.pdf"), content);

    _TakeSnapshot(pool, "before-the-mistake");
    File.Delete(pool.PathTo("invoice.pdf"));
    File.Exists(pool.PathTo("invoice.pdf")).Should().BeFalse("the delete really did take it out of the namespace");

    File.Copy(_InSnapshot(pool, "before-the-mistake", "invoice.pdf"), pool.PathTo("invoice.pdf"));

    File.ReadAllBytes(pool.PathTo("invoice.pdf")).Should().Equal(content,
      $"which is the promise: the user who deleted the wrong thing gets it back with the tools they "
      + $"already have.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A large file reads back through the view whole, in the many-chunk reads the kernel actually issues.")]
  public void Browse_GivenALargeFile_ThenItStreamsBackWholeThroughTheView() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(6 * 1024 * 1024, 101);
    File.WriteAllBytes(pool.PathTo("big.bin"), content);

    _TakeSnapshot(pool, "whole");
    File.WriteAllBytes(pool.PathTo("big.bin"), _Payload(1024, 102));

    // Six megabytes is dozens of kernel reads at whatever the mount's read size is, each landing at
    // its own offset — the case where an offset bug shows up as a file that is the right length and
    // the wrong content, which a small payload cannot catch.
    File.ReadAllBytes(_InSnapshot(pool, "whole", "big.bin")).Should().Equal(content,
      $"every chunk has to come from the offset it asked for."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

}
