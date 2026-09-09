using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Snapshots as a place you can walk into: <c>.snapshots/&lt;name&gt;/&lt;path&gt;</c>, inside the pool's
/// own namespace.
///
/// The point is that recovering a file should not require the utility. A user who deleted the wrong
/// thing opens their file manager, goes to the folder, and copies it back with the tools they
/// already know — no command, no admin, no support call. That only works if three things hold at
/// once, and each of them is a test below: the tree resolves when you ask for it by name; it does
/// NOT appear in a directory listing (or every backup tool that walks the pool would faithfully copy
/// every version of every file it has ever held); and nothing under it can be changed, because a
/// record of the past that can be edited is not a record.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SnapshotTreeTests {

  private static readonly Guid _pool = Guid.Parse("eeeeeeee-1111-2222-3333-777777777777");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 22);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 22);
  }

  private PoolFileSystem _Mounted() {
    var cache = new CacheInstance("st" + Guid.NewGuid().ToString("N"),
      new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._a), new(this._b)], cache,
      ConfigResolver.ResolveEffective(null, """{ "duplication": 1, "trash": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  /// <summary>Reads a path THROUGH the tree — Open/Read/Close, exactly as a file manager would.</summary>
  private static byte[] _ReadThroughTree(PoolFileSystem fs, string path) {
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var length = (int)fs.GetAttributes(path).Length;
      var buffer = new byte[length];
      var read = fs.Read(handle, buffer, 0);
      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  [Test]
  [Category("HappyPath")]
  public void Tree_GivenASnapshotWasTaken_ThenItIsListedByName() {
    var fs = this._Mounted();
    _Write(fs, "notes.txt", [1, 2, 3]);
    fs.TakeSnapshot("monday");
    fs.TakeSnapshot("tuesday");

    fs.ReadDirectory(PoolFileSystem.SnapshotTreeName).Select(e => e.Name)
      .Should().BeEquivalentTo(["monday", "tuesday"],
        "the tree's top level is one folder per snapshot, named the way the user named it");
  }

  [Test]
  [Category("HappyPath")]
  public void Tree_GivenAFileWasOverwrittenAfterTheSnapshot_ThenTheOldContentReadsBackThroughTheTree() {
    var fs = this._Mounted();
    _Write(fs, "report.doc", [1, 1, 1, 1]);
    fs.TakeSnapshot("before-the-edit");
    _Write(fs, "report.doc", [9, 9]);

    _ReadThroughTree(fs, ".snapshots/before-the-edit/report.doc").Should().Equal(new byte[] { 1, 1, 1, 1 },
      "opening the snapshot's copy of the file must return what the pool held when it was taken");

    _ReadThroughTree(fs, "report.doc").Should().Equal(new byte[] { 9, 9 },
      "and the live file is untouched by having been read through the past");
  }

  [Test]
  [Category("HappyPath")]
  public void Tree_GivenAFileNothingHasTouched_ThenItStillReadsThroughTheTree() {
    // No version was ever preserved for this path — the live file IS what the snapshot saw. The
    // tree has to fall through to it, or a snapshot would appear to contain only the files that
    // happened to change afterwards, which is the opposite of what it means.
    var fs = this._Mounted();
    _Write(fs, "stable.bin", [7, 7, 7]);
    fs.TakeSnapshot("nightly");

    _ReadThroughTree(fs, ".snapshots/nightly/stable.bin").Should().Equal(new byte[] { 7, 7, 7 },
      "an untouched file is still part of the snapshot; nothing had to be copied for that to be true");
  }

  [Test]
  [Category("HappyPath")]
  public void Tree_GivenNestedPaths_ThenTheFoldersUnderASnapshotCanBeWalked() {
    var fs = this._Mounted();
    fs.MakeDir("work");
    fs.MakeDir("work/2026");
    _Write(fs, "work/2026/q1.xlsx", [4]);
    _Write(fs, "work/readme.md", [5]);
    fs.TakeSnapshot("archive");

    fs.ReadDirectory(".snapshots/archive").Select(e => e.Name).Should().Contain("work",
      "the folders a snapshot's paths imply have to be walkable, level by level");
    fs.ReadDirectory(".snapshots/archive/work").Select(e => e.Name)
      .Should().BeEquivalentTo(["2026", "readme.md"]);
    fs.ReadDirectory(".snapshots/archive/work/2026").Select(e => e.Name)
      .Should().BeEquivalentTo(["q1.xlsx"]);

    _ReadThroughTree(fs, ".snapshots/archive/work/2026/q1.xlsx").Should().Equal(new byte[] { 4 });
  }

  [Test]
  [Category("HappyPath")]
  public void Tree_GivenADeletedFile_ThenItIsStillThereUnderTheSnapshot() {
    var fs = this._Mounted();
    _Write(fs, "invoice.pdf", [3, 3, 3]);
    fs.TakeSnapshot("before-the-mistake");
    fs.Unlink("invoice.pdf");

    _ReadThroughTree(fs, ".snapshots/before-the-mistake/invoice.pdf").Should().Equal(new byte[] { 3, 3, 3 },
      "recovering a deleted file has to be a copy-paste in a file manager, not a support ticket");
  }

  [Test]
  [Category("EdgeCase")]
  public void Tree_GivenARootListing_ThenTheTreeIsNotInIt() {
    // The whole reason the tree is safe to leave switched on. A backup or sync tool walks the pool
    // by listing it; if the snapshot view were listed, the tool would recurse into it and copy every
    // version of every file the pool has ever held — and it would do that on every run.
    var fs = this._Mounted();
    _Write(fs, "one.bin", [1]);
    fs.TakeSnapshot("nightly");

    fs.ReadDirectory("").Select(e => e.Name).Should().NotContain(PoolFileSystem.SnapshotTreeName,
      "the snapshot view is navigable by name and invisible to a walk — anything else turns every "
      + "backup tool on the pool into a version-duplicating machine");
  }

  [Test]
  [Category("EdgeCase")]
  public void Tree_GivenNoSnapshotOfThatName_ThenTheAnswerIsNotFound() {
    var fs = this._Mounted();
    _Write(fs, "one.bin", [1]);

    var read = () => fs.ReadDirectory(".snapshots/never-taken");
    read.Should().Throw<PoolFsException>().Where(e => e.Error == PoolFsError.NotFound);

    var stat = () => fs.GetAttributes(".snapshots/nightly/one.bin");
    stat.Should().Throw<PoolFsException>().Where(e => e.Error == PoolFsError.NotFound);
  }

  [Test]
  [Category("EdgeCase")]
  public void Tree_GivenASnapshotThatDidNotHoldThePath_ThenThatPathIsNotFoundUnderIt() {
    var fs = this._Mounted();
    _Write(fs, "before.bin", [1]);
    fs.TakeSnapshot("early");
    _Write(fs, "after.bin", [2]);

    var stat = () => fs.GetAttributes(".snapshots/early/after.bin");
    stat.Should().Throw<PoolFsException>("a file created after the snapshot was never part of it")
      .Where(e => e.Error == PoolFsError.NotFound);
  }

  [Test]
  [Category("HappyPath")]
  public void Tree_GivenAStat_ThenTheLengthIsTheSnapshotsLengthNotTheLiveOne() {
    var fs = this._Mounted();
    _Write(fs, "grows.bin", new byte[10]);
    fs.TakeSnapshot("small");
    _Write(fs, "grows.bin", new byte[400]);

    fs.GetAttributes(".snapshots/small/grows.bin").Length.Should().Be(10,
      "a stat through the tree describes the file the snapshot holds; a caller that trusted the "
      + "live length would read 400 bytes out of a 10-byte file");
    fs.GetAttributes("grows.bin").Length.Should().Be(400);
  }

  [Test]
  [Category("Exception")]
  public void Tree_GivenAnAttemptToChangeIt_ThenEveryMutationIsRefused() {
    var fs = this._Mounted();
    _Write(fs, "report.doc", [1, 1]);
    fs.TakeSnapshot("monday");

    // Every door into the namespace, because refusing four of five is not read-only.
    var attempts = new (string What, Action Do)[] {
      ("create a file", () => fs.Create(".snapshots/monday/new.txt", NodeKind.File, CreateFlags.Truncate)),
      ("open for writing", () => fs.Open(".snapshots/monday/report.doc", AccessMode.Write, ShareMode.None)),
      ("delete a file", () => fs.Unlink(".snapshots/monday/report.doc")),
      ("rename out of it", () => fs.Rename(".snapshots/monday/report.doc", "escaped.doc", RenameFlags.None)),
      ("rename into it", () => fs.Rename("report.doc", ".snapshots/monday/planted.doc", RenameFlags.None)),
      ("make a folder", () => fs.MakeDir(".snapshots/monday/sub")),
      ("remove a folder", () => fs.RemoveDir(".snapshots/monday")),
      ("set attributes", () => fs.SetAttributes(".snapshots/monday/report.doc", new() { LastWriteTimeUtc = DateTime.UtcNow })),
    };

    foreach (var (what, act) in attempts) {
      var attempt = () => act();
      attempt.Should().Throw<PoolFsException>($"an attempt to {what} inside the snapshot view must be refused")
        .Where(e => e.Error == PoolFsError.AccessDenied,
          $"and refused as a permission problem, so the caller stops rather than retries — while trying to {what}");
    }

    _ReadThroughTree(fs, ".snapshots/monday/report.doc").Should().Equal(new byte[] { 1, 1 },
      "and after all that, the snapshot holds exactly what it held");
  }

  [Test]
  [Category("Exception")]
  public void Tree_GivenAWriteThroughAReadHandle_ThenItIsRefusedRatherThanSilentlyDropped() {
    var fs = this._Mounted();
    _Write(fs, "report.doc", [1, 1]);
    fs.TakeSnapshot("monday");

    var handle = fs.Open(".snapshots/monday/report.doc", AccessMode.Read, ShareMode.Read);
    try {
      var write = () => fs.Write(handle, new byte[] { 9 }, 0, WriteMode.Normal);
      write.Should().Throw<PoolFsException>().Where(e => e.Error == PoolFsError.AccessDenied);

      var truncate = () => fs.SetLength(handle, 0);
      truncate.Should().Throw<PoolFsException>().Where(e => e.Error == PoolFsError.AccessDenied);

      fs.Flush(handle); // nothing was owed; flushing a view of the past is a no-op, not an error
    } finally {
      fs.Close(handle);
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Tree_GivenTheSnapshotWasDeleted_ThenItsFolderIsGoneWithIt() {
    var fs = this._Mounted();
    _Write(fs, "report.doc", [1, 1]);
    var taken = fs.TakeSnapshot("monday");
    _Write(fs, "report.doc", [9]);

    fs.DeleteSnapshot(taken.Id);

    fs.ReadDirectory(PoolFileSystem.SnapshotTreeName).Should().BeEmpty(
      "deleting a snapshot removes it from the view too — a folder for a snapshot that no longer "
      + "exists is a promise the pool has stopped keeping");
  }

  [Test]
  [Category("EdgeCase")]
  public void Tree_GivenTwoOpenSnapshotHandles_ThenEachReadsItsOwnVersion() {
    var fs = this._Mounted();
    _Write(fs, "log.txt", [1]);
    fs.TakeSnapshot("first");
    _Write(fs, "log.txt", [2, 2]);
    fs.TakeSnapshot("second");
    _Write(fs, "log.txt", [3, 3, 3]);

    var early = fs.Open(".snapshots/first/log.txt", AccessMode.Read, ShareMode.Read);
    var late = fs.Open(".snapshots/second/log.txt", AccessMode.Read, ShareMode.Read);
    try {
      var one = new byte[8];
      var two = new byte[8];
      fs.Read(early, one, 0).Should().Be(1);
      fs.Read(late, two, 0).Should().Be(2);
      one[..1].Should().Equal(new byte[] { 1 });
      two[..2].Should().Equal(new byte[] { 2, 2 });
    } finally {
      fs.Close(early);
      fs.Close(late);
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Tree_GivenAPartialRead_ThenTheOffsetIsHonoured() {
    var fs = this._Mounted();
    _Write(fs, "big.bin", [10, 11, 12, 13, 14, 15]);
    fs.TakeSnapshot("whole");
    fs.Unlink("big.bin");

    var handle = fs.Open(".snapshots/whole/big.bin", AccessMode.Read, ShareMode.Read);
    try {
      var middle = new byte[2];
      fs.Read(handle, middle, 2).Should().Be(2);
      middle.Should().Equal(new byte[] { 12, 13 }, "a read at an offset must land at that offset");

      var past = new byte[4];
      fs.Read(handle, past, 6).Should().Be(0, "and a read past the end returns nothing, not garbage");
    } finally {
      fs.Close(handle);
    }
  }

  [Test]
  [Category("Exception")]
  public void Tree_GivenAClosedSnapshotHandle_ThenReadingThroughItIsRefused() {
    var fs = this._Mounted();
    _Write(fs, "one.bin", [1]);
    fs.TakeSnapshot("nightly");

    var handle = fs.Open(".snapshots/nightly/one.bin", AccessMode.Read, ShareMode.Read);
    fs.Close(handle);

    var read = () => fs.Read(handle, new byte[4], 0);
    read.Should().Throw<PoolFsException>("a closed handle is not a handle")
      .Where(e => e.Error == PoolFsError.StaleHandle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Tree_GivenAFolderInTheView_ThenOpeningItAsAFileIsRefused() {
    var fs = this._Mounted();
    fs.MakeDir("work");
    _Write(fs, "work/one.bin", [1]);
    fs.TakeSnapshot("nightly");

    fs.GetAttributes(".snapshots/nightly/work").IsDirectory.Should().BeTrue();

    var open = () => fs.Open(".snapshots/nightly/work", AccessMode.Read, ShareMode.Read);
    open.Should().Throw<PoolFsException>().Where(e => e.Error == PoolFsError.IsADirectory);
  }

  [Test]
  [Category("EdgeCase")]
  public void Tree_GivenNoSnapshotsAtAll_ThenTheViewIsAnEmptyFolderRatherThanAnError() {
    var fs = this._Mounted();
    _Write(fs, "one.bin", [1]);

    fs.GetAttributes(PoolFileSystem.SnapshotTreeName).IsDirectory.Should().BeTrue(
      "the view exists whether or not anything has been taken — a user checking for snapshots "
      + "should be told there are none, not that the folder is missing");
    fs.ReadDirectory(PoolFileSystem.SnapshotTreeName).Should().BeEmpty();
  }

}
