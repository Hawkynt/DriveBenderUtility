using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Snapshots (docs/Snapshots.md): a recorded instant that the pool may not destroy afterwards.
///
/// The promise is narrow and worth stating exactly. A snapshot does not copy anything when taken,
/// and it does not protect against losing the disks — it shares the pool's failure domains. What it
/// promises is that content the pool held at that instant is still readable after the live file has
/// been overwritten, replaced or deleted.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SnapshotTests {

  private static readonly Guid _pool = Guid.Parse("eeeeeeee-1111-2222-3333-666666666666");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 22);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 22);
  }

  private PoolFileSystem _Mounted(string config = """{ "duplication": 1, "trash": { "enabled": false } }""") {
    var cache = new CacheInstance("sn" + Guid.NewGuid().ToString("N"),
      new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._a), new(this._b)], cache,
      ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static byte[] _ReadVersion(PoolFileSystem fs, Guid snapshot, string path) {
    var located = fs.Snapshots.Resolve(snapshot, path);
    located.Should().NotBeNull($"'{path}' must be resolvable as of the snapshot");
    using var stream = located!.Value.member.OpenRead(located.Value.versionPath, false);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  [Test]
  [Category("HappyPath")]
  public void Take_GivenAPoolWithFiles_ThenItRecordsTheNamespaceWithoutCopyingAnything() {
    var fs = this._Mounted();
    _Write(fs, "one.bin", [1, 1, 1]);
    _Write(fs, "two.bin", [2, 2]);

    var before = (this._a.BytesTotal - this._a.BytesFree) + (this._b.BytesTotal - this._b.BytesFree);
    var taken = fs.TakeSnapshot("nightly");
    var after = (this._a.BytesTotal - this._a.BytesFree) + (this._b.BytesTotal - this._b.BytesFree);

    taken.Files.Should().Be(2, "the snapshot names every file the pool held");
    fs.ListSnapshots().Should().ContainSingle(s => s.Id == taken.Id && s.Name == "nightly");
    (after - before).Should().BeLessThan(4096,
      "taking a snapshot copies NO data — what it costs is an index, not the pool's contents");
  }

  [Test]
  [Category("HappyPath")]
  public void Overwrite_GivenASnapshotNamesTheFile_ThenTheOldContentIsStillReadable() {
    var fs = this._Mounted();
    _Write(fs, "report.doc", [1, 1, 1, 1]);
    var taken = fs.TakeSnapshot("before-the-edit");

    _Write(fs, "report.doc", [9, 9]);

    _ReadVersion(fs, taken.Id, "report.doc").Should().Equal(new byte[] { 1, 1, 1, 1 },
      "the snapshot promised the content of that instant, and an overwrite is exactly what it exists "
      + "to survive");

    // and the live file is the new content, unaffected
    var handle = fs.Open("report.doc", AccessMode.Read, ShareMode.Read);
    var live = new byte[2];
    fs.Read(handle, live, 0);
    fs.Close(handle);
    live.Should().Equal(new byte[] { 9, 9 }, "the pool serves the current file, not the snapshot");
  }

  [Test]
  [Category("HappyPath")]
  public void Delete_GivenASnapshotNamesTheFile_ThenTheContentIsStillReadable() {
    var fs = this._Mounted();
    _Write(fs, "invoice.pdf", [7, 7, 7]);
    var taken = fs.TakeSnapshot("before-the-delete");

    fs.Unlink("invoice.pdf");

    _ReadVersion(fs, taken.Id, "invoice.pdf").Should().Equal(new byte[] { 7, 7, 7 },
      "deleting a file a snapshot names must not destroy what the snapshot promised");
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenTheFileIsUntouchedSinceTheSnapshot_ThenNothingIsStored() {
    var fs = this._Mounted();
    _Write(fs, "quiet.bin", [4, 4, 4]);
    var taken = fs.TakeSnapshot("quiet");

    fs.Snapshots.Resolve(taken.Id, "quiet.bin").Should().BeNull(
      "a file nobody has touched since the snapshot IS the snapshot's copy — storing a second one "
      + "would make a snapshot of an idle pool cost as much as the pool");
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenTwoWritesAfterOneSnapshot_ThenOnlyTheFirstIsPreserved() {
    var fs = this._Mounted();
    _Write(fs, "busy.bin", [1]);
    var taken = fs.TakeSnapshot("once");

    _Write(fs, "busy.bin", [2]);
    _Write(fs, "busy.bin", [3]);

    _ReadVersion(fs, taken.Id, "busy.bin").Should().Equal(new byte[] { 1 },
      "the snapshot wants the content of its own instant, not of every edit since");

    fs.Snapshots.PinnedPaths().Should().NotContain("busy.bin",
      "once preserved, the path costs nothing further until the next snapshot — otherwise a file "
      + "written in a loop would fill the store with versions no snapshot names");
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenTheSnapshotIsForgotten_ThenTheVersionItHeldIsReleased() {
    var fs = this._Mounted();
    _Write(fs, "temp.bin", [5, 5, 5]);
    var taken = fs.TakeSnapshot("droppable");
    _Write(fs, "temp.bin", [6]);

    fs.Snapshots.Resolve(taken.Id, "temp.bin").Should().NotBeNull("the version exists while the snapshot does");

    fs.DeleteSnapshot(taken.Id).Should().Be(1, "the version only this snapshot held is released with it");
    fs.ListSnapshots().Should().BeEmpty("and the snapshot is gone");
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenTwoSnapshotsShareAVersion_ThenItSurvivesTheFirstDeletion() {
    var fs = this._Mounted();
    _Write(fs, "shared.bin", [8, 8]);
    var first = fs.TakeSnapshot("first");
    var second = fs.TakeSnapshot("second");

    _Write(fs, "shared.bin", [0]);

    fs.DeleteSnapshot(first.Id).Should().Be(0,
      "the version is pinned by both, so forgetting one must release nothing");
    _ReadVersion(fs, second.Id, "shared.bin").Should().Equal(new byte[] { 8, 8 },
      "the surviving snapshot still promises that content");
  }

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenASnapshotNamesTheFile_ThenTheOldPathStillResolves() {
    // The quiet one. A rename does not destroy the bytes, so it is easy to think a snapshot is
    // unaffected — but the snapshot recorded a PATH, and after the rename that path names nothing.
    // Without preserving, resolving it would fall through to "the live file", which is gone.
    var fs = this._Mounted();
    _Write(fs, "draft.txt", [3, 3, 3]);
    var taken = fs.TakeSnapshot("before-the-rename");

    fs.Rename("draft.txt", "final.txt", RenameFlags.None);

    _ReadVersion(fs, taken.Id, "draft.txt").Should().Equal(new byte[] { 3, 3, 3 },
      "the snapshot recorded this path holding this content, and a rename is not permission to "
      + "forget that");
  }

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenItOverwritesAPinnedTarget_ThenTheTargetsContentSurvives() {
    // The other end of the same operation: renaming ONTO a file destroys that file, which is a
    // delete wearing a different verb.
    var fs = this._Mounted();
    _Write(fs, "keep.txt", [1]);
    _Write(fs, "overwrite-me.txt", [2, 2]);
    var taken = fs.TakeSnapshot("before-the-clobber");

    fs.Rename("keep.txt", "overwrite-me.txt", RenameFlags.ReplaceExisting);

    _ReadVersion(fs, taken.Id, "overwrite-me.txt").Should().Equal(new byte[] { 2, 2 },
      "the file that was overwritten by the rename is content the snapshot promised");
  }

}
