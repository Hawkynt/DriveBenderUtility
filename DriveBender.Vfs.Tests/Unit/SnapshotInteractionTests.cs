using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Snapshots against the parts of the engine that came after them: stripe sessions and deferred
/// publishes, write-back copies still owed, handles already open when the snapshot is taken, and
/// operations that change a file without the verbs the first slices guarded (docs/Snapshots.md).
///
/// Every case asks the same question: after the snapshot, is what it promised still readable, byte
/// for byte, as it was at that instant?
/// </summary>
[TestFixture]
[Category("Unit")]
public class SnapshotInteractionTests {

  private static readonly Guid _pool = Guid.Parse("5a5a5a5a-1111-2222-3333-000000000001");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;
  private FakeVolumeIO _c = null!;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 22);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 22);
    this._c = new(Guid.NewGuid(), "c", "PHYS-C", capacity: 1L << 22);
  }

  private PoolFileSystem _Mounted(string config = """{ "duplication": 1, "trash": { "enabled": false } }""", bool threeMembers = false) {
    var cache = new CacheInstance("si" + Guid.NewGuid().ToString("N"),
      new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    EngineMember[] members = threeMembers ? [new(this._a), new(this._b), new(this._c)] : [new(this._a), new(this._b)];
    var fs = new PoolFileSystem(_pool, members, cache, ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static byte[] _ReadSnapshotFile(PoolFileSystem fs, Guid id, string path) {
    using var stream = fs.OpenSnapshotFile(id, path);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private static byte[] _ReadLive(PoolFileSystem fs, string path) {
    var length = (int)fs.GetAttributes(path).Length;
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var buffer = new byte[length];
      var read = fs.Read(handle, buffer, 0);
      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  private const string _PERFORMANCE = """{ "duplication": 1, "trash": { "enabled": false }, "write": { "policy": "performance" } }""";

  #region stripe sessions and deferred publishes

  [Test]
  [Category("EdgeCase")]
  public void Take_GivenAClosedFileWhosePublishIsStillPending_ThenTheSnapshotHoldsIt() {
    // Under the performance policy a striped file's close returns before it is published. The
    // application has closed it — as far as anyone can tell it is saved — but on disk it is still a
    // hidden temp, and a snapshot walked only the published names.
    using var fs = this._Mounted(_PERFORMANCE);
    _Write(fs, "late.bin", [5, 5, 5]);

    var taken = fs.TakeSnapshot("after-the-save");
    _Write(fs, "late.bin", [9]);

    fs.Snapshots.PathsIn(taken.Id).Should().Contain("late.bin", "a file its writer had closed belongs to the snapshot");
    _ReadSnapshotFile(fs, taken.Id, "late.bin").Should().Equal(new byte[] { 5, 5, 5 },
      "and what the snapshot promises is what was saved, not what replaced it");
  }

  [Test]
  [Category("EdgeCase")]
  public void Take_GivenAFileStillOpenForItsFirstWrite_ThenTheSnapshotLeavesItOut() {
    // the other side of the line: a file whose writer has not finished has no content to promise
    using var fs = this._Mounted(_PERFORMANCE);
    var handle = fs.Create("unfinished.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1, 2, 3], 0, WriteMode.Normal);

    var taken = fs.TakeSnapshot("mid-write");
    fs.Close(handle);

    fs.Snapshots.PathsIn(taken.Id).Should().NotContain("unfinished.bin");
    _ReadLive(fs, "unfinished.bin").Should().Equal(new byte[] { 1, 2, 3 }, "and the file itself is untouched by being left out");
  }

  [Test]
  [Category("EdgeCase")]
  public void Restore_GivenTheFileIsBeingWrittenAgainThroughAStripeSession_ThenTheRestoredContentWins() {
    // The file was in the snapshot, was deleted, and is being written anew — still open, its blocks
    // striped over both disks. A restore then creates over a name whose only file is a staged temp:
    // it must join that file, not start a second one beside it under the same staged name.
    using var fs = this._Mounted(_PERFORMANCE);
    _Write(fs, "report.doc", [1, 1, 1]);
    fs.PublishOneDeferredStripe();
    var taken = fs.TakeSnapshot("good");
    fs.Unlink("report.doc");

    var writer = fs.Create("report.doc", NodeKind.File, CreateFlags.None);
    fs.Write(writer, new byte[64], 0, WriteMode.Normal);

    fs.RestoreFromSnapshot(taken.Id, "report.doc");
    fs.Close(writer);
    while (fs.PublishOneDeferredStripe()) {
    }

    _ReadLive(fs, "report.doc").Should().Equal(new byte[] { 1, 1, 1 }, "the restore truncated and rewrote the one file at this name");
    new[] { this._a, this._b }.Count(m => m.FileExists("report.doc", false)).Should().Be(1,
      "one file, one primary — not a second file published beside the first under the same name");
    foreach (var member in new[] { this._a, this._b })
      member.FilePaths.Should().NotContain(p => p.EndsWith(".TEMP.$DRIVEBENDER", StringComparison.OrdinalIgnoreCase),
        $"no second staged file is left behind on '{member.DisplayName}'");
  }

  [Test]
  [Category("Exception")]
  public void Create_GivenExclusiveOverANameStillBeingWritten_ThenItIsRefusedAsExisting() {
    // the same collision without a snapshot: O_EXCL must see a file that exists only as a staged temp
    using var fs = this._Mounted(_PERFORMANCE);
    var writer = fs.Create("race.bin", NodeKind.File, CreateFlags.Exclusive);
    fs.Write(writer, [7], 0, WriteMode.Normal);

    var second = () => fs.Create("race.bin", NodeKind.File, CreateFlags.Exclusive);

    second.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
    fs.Close(writer);
  }

  #endregion

  #region handles and owed copies

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenAHandleOpenedBeforeTheSnapshot_ThenTheSnapshotStillHoldsTheOldContent() {
    // Preservation was keyed on opening for writing. A handle opened before the snapshot existed
    // never passed that door, so every write through it went straight into what the snapshot
    // promised — a log file or a database, open all day, is exactly this.
    using var fs = this._Mounted();
    _Write(fs, "app.log", [1, 1, 1, 1]);
    var handle = fs.Open("app.log", AccessMode.ReadWrite, ShareMode.Read);

    var taken = fs.TakeSnapshot("while-open");
    fs.Write(handle, [9], 0, WriteMode.Normal);
    fs.Close(handle);

    _ReadSnapshotFile(fs, taken.Id, "app.log").Should().Equal(new byte[] { 1, 1, 1, 1 });
    _ReadLive(fs, "app.log").Should().Equal(new byte[] { 9, 1, 1, 1 }, "and the write itself is not held back");
  }

  [Test]
  [Category("EdgeCase")]
  public void SetLength_GivenAHandleOpenedBeforeTheSnapshot_ThenTheSnapshotStillHoldsTheOldContent() {
    using var fs = this._Mounted();
    _Write(fs, "app.log", [1, 2, 3, 4]);
    var handle = fs.Open("app.log", AccessMode.ReadWrite, ShareMode.Read);

    var taken = fs.TakeSnapshot("while-open");
    fs.SetLength(handle, 1);
    fs.Close(handle);

    _ReadSnapshotFile(fs, taken.Id, "app.log").Should().Equal(new byte[] { 1, 2, 3, 4 });
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenAHandleOpenedBeforeTheSnapshot_WhenItWritesTwice_ThenOnlyOneVersionIsKept() {
    using var fs = this._Mounted();
    _Write(fs, "app.log", [1, 1]);
    var handle = fs.Open("app.log", AccessMode.ReadWrite, ShareMode.Read);

    var taken = fs.TakeSnapshot("while-open");
    fs.Write(handle, [2], 0, WriteMode.Normal);
    var afterFirst = fs.Snapshots.StoreBytes();
    fs.Write(handle, [3], 1, WriteMode.Normal);
    fs.Close(handle);

    fs.Snapshots.StoreBytes().Should().Be(afterFirst, "the content of the snapshot's instant is kept once, not once per write");
    _ReadSnapshotFile(fs, taken.Id, "app.log").Should().Equal(new byte[] { 1, 1 });
  }

  /// <summary>Three disks, three copies, two before the ack: one copy of every write is owed for a while.</summary>
  private const string _OWING = """{ "duplication": 3, "trash": { "enabled": false } }""";

  /// <summary>Writes one block into block 1 of a three-copy file so that the PRIMARY is the copy still owed it.</summary>
  private static void _WriteLeavingThePrimaryBehind(PoolFileSystem fs, string path) {
    _Write(fs, path, new byte[48]); // whole on all three copies once published
    var handle = fs.Open(path, AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(handle, [9, 9], 16, WriteMode.Normal); // block 1: the rotation acks on the two shadows
    fs.Close(handle);
    fs.WriteBuffer.IsDirty(path).Should().BeTrue("the premise: a copy is still owed the acknowledged write");
  }

  private static byte[] _Expected() {
    var expected = new byte[48];
    expected[16] = expected[17] = 9;
    return expected;
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenAnAcknowledgedWriteIsStillOwedToTheCopyThatIsSetAside_ThenTheVersionHoldsIt() {
    // The version is made from ONE copy, and a copy can be behind an acknowledged write for as long
    // as the write-back window. Setting that copy aside and then discarding what it was owed kept a
    // version the pool never held.
    using var fs = this._Mounted(_OWING, threeMembers: true);
    _WriteLeavingThePrimaryBehind(fs, "ledger.db");

    var taken = fs.TakeSnapshot("with-the-write");
    fs.Unlink("ledger.db");

    _ReadSnapshotFile(fs, taken.Id, "ledger.db").Should().Equal(_Expected(),
      "the snapshot was taken after the write was acknowledged, so it holds it");
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenAnAcknowledgedWriteIsStillOwed_ThenTheVersionHoldsIt() {
    using var fs = this._Mounted(_OWING, threeMembers: true);
    _WriteLeavingThePrimaryBehind(fs, "ledger.db");

    var taken = fs.TakeSnapshot("with-the-write");
    _Write(fs, "ledger.db", [1]);

    _ReadSnapshotFile(fs, taken.Id, "ledger.db").Should().Equal(_Expected());
  }

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenAnAcknowledgedWriteIsStillOwed_ThenTheVersionHoldsIt() {
    using var fs = this._Mounted(_OWING, threeMembers: true);
    _WriteLeavingThePrimaryBehind(fs, "ledger.db");

    var taken = fs.TakeSnapshot("with-the-write");
    fs.Rename("ledger.db", "ledger.old", RenameFlags.None);

    _ReadSnapshotFile(fs, taken.Id, "ledger.db").Should().Equal(_Expected());
  }

  #endregion

  #region other ways a file changes

  [Test]
  [Category("EdgeCase")]
  public void SetAttributes_GivenANewModificationTime_ThenTheSnapshotCanStillBeRead() {
    // A snapshot proves an untouched live file is still its content by the file's modification
    // time. `touch` moves that time without changing a byte, and the snapshot then refused to serve
    // the file at all ("preserved, and that copy is not available").
    using var fs = this._Mounted();
    _Write(fs, "photo.jpg", [4, 4, 4]);
    var taken = fs.TakeSnapshot("before-touch");

    fs.SetAttributes("photo.jpg", new(LastWriteTimeUtc: DateTime.UtcNow.AddHours(1)));

    _ReadSnapshotFile(fs, taken.Id, "photo.jpg").Should().Equal(new byte[] { 4, 4, 4 });
  }

  [Test]
  [Category("Exception")]
  public void Rename_GivenAPinnedTargetAndNoPermissionToReplaceIt_ThenNothingIsSetAside() {
    // the rename is refused, so nothing happened to either file — and a copy made before the refusal
    // is store space spent on nothing, with the target dropped from the pinned set on the way
    using var fs = this._Mounted();
    _Write(fs, "keep.txt", [1]);
    _Write(fs, "target.txt", [2, 2]);
    var taken = fs.TakeSnapshot("s");

    var rename = () => fs.Rename("keep.txt", "target.txt", RenameFlags.None);

    rename.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
    fs.Snapshots.StoreBytes().Should().Be(0, "a refused rename changes nothing, so nothing needed keeping");

    _Write(fs, "target.txt", [3]);
    _ReadSnapshotFile(fs, taken.Id, "target.txt").Should().Equal(new byte[] { 2, 2 },
      "and the target is still protected when something does overwrite it");
  }

  [Test]
  [Category("HappyPath")]
  public void Restore_GivenTheFileIsUntouchedSinceTheSnapshot_ThenNothingIsCopiedOrSetAside() {
    // the live file IS the snapshot's content; restoring it used to set it aside and write it back
    using var fs = this._Mounted();
    _Write(fs, "same.bin", [6, 6, 6]);
    var taken = fs.TakeSnapshot("s");

    fs.RestoreFromSnapshot(taken.Id, "same.bin");

    fs.Snapshots.StoreBytes().Should().Be(0);
    _ReadLive(fs, "same.bin").Should().Equal(new byte[] { 6, 6, 6 });
    _ReadSnapshotFile(fs, taken.Id, "same.bin").Should().Equal(new byte[] { 6, 6, 6 });
  }

  #endregion

}
