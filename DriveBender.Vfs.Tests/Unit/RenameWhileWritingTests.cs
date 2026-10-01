using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Renames that meet a file somebody is still writing must never cost data. A folder renamed under
/// an open file takes the file with it, and the writer's later writes and its close land under the
/// new name. A file renamed OVER a name still being written takes the name, and the file being
/// written is kept beside it ("… (displaced …)"): the writer keeps writing into it, and its close
/// publishes it there. On Windows the OS refuses most of these renames while the file is open; on
/// Linux they are ordinary.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RenameWhileWritingTests {

  private static readonly Guid _pool = Guid.Parse("4e0a3e00-0000-0000-0000-0000000000d1");

  private FakeVolumeIO _d1 = null!;
  private FakeVolumeIO _d2 = null!;
  private FakeVolumeIO _d3 = null!;

  [SetUp]
  public void SetUp() {
    this._d1 = new(Guid.NewGuid(), "d1", "PHYS-1", capacity: 1L << 25);
    this._d2 = new(Guid.NewGuid(), "d2", "PHYS-2", capacity: 1L << 25);
    this._d3 = new(Guid.NewGuid(), "d3", "PHYS-3", capacity: 1L << 25);
  }

  private PoolFileSystem _Engine(int duplication = 2) {
    var cache = new CacheInstance("rw" + Guid.NewGuid().ToString("N"),
      new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._d1), new(this._d2), new(this._d3)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": {{duplication}}, "readAhead": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  /// <summary>Several stripe units: the write spreads over the whole group, so the close has a fill to do.</summary>
  private static byte[] _Big(int seed) => [.. Enumerable.Range(0, (int)(StripeSession.MinimumStripeUnit * 3) + 100).Select(i => (byte)(i * 7 + seed))];

  private static byte[]? _Read(PoolFileSystem fs, string path) {
    NodeHandle handle;
    try {
      handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    } catch (PoolFsException) {
      return null;
    }

    try {
      var buffer = new byte[fs.GetAttributes(path).Length];
      var read = 0;
      while (read < buffer.Length && fs.Read(handle, buffer.AsSpan(read), read) is var got and > 0)
        read += got;

      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  private static void _WriteClosed(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private IEnumerable<string> _Temps() => new[] { this._d1, this._d2, this._d3 }.SelectMany(d => d.FilePaths).Where(p => p.Contains("TEMP.$DRIVEBENDER", StringComparison.OrdinalIgnoreCase));

  private static string[] _Displaced(PoolFileSystem fs, string folder = "")
    => [.. fs.ReadDirectory(folder).Where(e => e.Name.Contains("(displaced", StringComparison.Ordinal)).Select(e => folder.Length == 0 ? e.Name : $"{folder}/{e.Name}")];

  #region a folder renamed under an open file

  [Test]
  [Category("HappyPath")]
  public void RenameFolder_GivenAFileInItIsBeingWrittenForTheFirstTime_ThenEveryWriteAndTheCloseLandUnderTheNewName() {
    using var fs = this._Engine();
    fs.MakeDir("inbox");
    var content = _Big(1);
    var half = content.Length / 2;
    var handle = fs.Create("inbox/scan.raw", NodeKind.File, CreateFlags.None);
    fs.Write(handle, content.AsSpan(0, half), 0, WriteMode.Normal);

    fs.Rename("inbox", "archive", RenameFlags.None);
    fs.Write(handle, content.AsSpan(half), half, WriteMode.Normal);
    fs.Close(handle);

    _Read(fs, "archive/scan.raw").Should().Equal(content, "the open file moved with its folder and kept every write");
    _Read(fs, "inbox/scan.raw").Should().BeNull("nothing is left, or brought back, under the old name");
    this._Temps().Should().BeEmpty("the temp it was written to is the file now, not a leftover");
  }

  [Test]
  [Category("HappyPath")]
  public void RenameFolder_GivenAnExistingFileInItIsBeingEdited_ThenTheEditLandsUnderTheNewName() {
    using var fs = this._Engine();
    fs.MakeDir("work");
    _WriteClosed(fs, "work/db.bin", [1, 2, 3, 4]);
    var edit = fs.Open("work/db.bin", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(edit, [9], 0, WriteMode.Normal);

    fs.Rename("work", "done", RenameFlags.None);
    fs.Write(edit, [8], 3, WriteMode.Normal);
    fs.Close(edit);

    _Read(fs, "done/db.bin").Should().Equal(new byte[] { 9, 2, 3, 8 });
    _Read(fs, "work/db.bin").Should().BeNull();
  }

  #endregion

  #region a file renamed over a name still being written

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenItReplacesAFileBeingWrittenForTheFirstTime_ThenTheRenamedFileTakesTheNameAndTheWrittenOneIsKeptBesideIt() {
    using var fs = this._Engine();
    var written = _Big(2);
    var half = written.Length / 2;
    var writing = fs.Create("report.doc", NodeKind.File, CreateFlags.None);
    fs.Write(writing, written.AsSpan(0, half), 0, WriteMode.Normal);
    _WriteClosed(fs, "upload.tmp", [5, 5, 5]);

    fs.Rename("upload.tmp", "report.doc", RenameFlags.ReplaceExisting);
    fs.Write(writing, written.AsSpan(half), half, WriteMode.Normal); // the writer carries on, unaware
    fs.Close(writing);

    _Read(fs, "report.doc").Should().Equal(new byte[] { 5, 5, 5 }, "the rename was acknowledged: the name is the renamed file's");
    var displaced = _Displaced(fs);
    displaced.Should().ContainSingle("the file being written is kept, not discarded");
    displaced[0].Should().StartWith("report (displaced ").And.EndWith(").doc");
    _Read(fs, displaced[0]).Should().Equal(written, "with every write, those after the rename included");
    this._Temps().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenItReplacesAnExistingFileBeingEdited_ThenTheEditIsKeptBesideTheRenamedFileAndNeverWrittenIntoIt() {
    // the handle used to stay bound to the NAME: its next write went into the renamed file
    using var fs = this._Engine();
    _WriteClosed(fs, "notes.txt", [1, 1, 1, 1]);
    var edit = fs.Open("notes.txt", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(edit, [7], 0, WriteMode.Normal);
    _WriteClosed(fs, "notes.new", [3, 3]);

    fs.Rename("notes.new", "notes.txt", RenameFlags.ReplaceExisting);
    fs.Write(edit, [6], 3, WriteMode.Normal);
    fs.Close(edit);

    _Read(fs, "notes.txt").Should().Equal(new byte[] { 3, 3 }, "the renamed file is untouched by the other file's writer");
    var displaced = _Displaced(fs);
    displaced.Should().ContainSingle();
    displaced[0].Should().StartWith("notes (displaced ").And.EndWith(").txt");
    _Read(fs, displaced[0]).Should().Equal(new byte[] { 7, 1, 1, 6 });
  }

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenTwoFilesAreDisplacedFromOneNameInTheSameSecond_ThenBothAreKept() {
    using var fs = this._Engine(duplication: 1);
    var first = fs.Create("a.bin", NodeKind.File, CreateFlags.None);
    fs.Write(first, [1], 0, WriteMode.Normal);
    _WriteClosed(fs, "x.bin", [9]);
    fs.Rename("x.bin", "a.bin", RenameFlags.ReplaceExisting);

    var second = fs.Open("a.bin", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(second, [2], 0, WriteMode.Normal);
    _WriteClosed(fs, "y.bin", [8]);
    fs.Rename("y.bin", "a.bin", RenameFlags.ReplaceExisting);
    fs.Close(first);
    fs.Close(second);

    _Read(fs, "a.bin").Should().Equal(new byte[] { 8 });
    _Displaced(fs).Select(p => _Read(fs, p)![0]).Should().BeEquivalentTo(new byte[] { 1, 2 }, "a second displacement never lands on the first");
  }

  [Test]
  [Category("HappyPath")]
  public void Rename_GivenItReplacesAFileOnlyOpenForReading_ThenNothingIsDisplaced() {
    // the atomic-save pattern (write a temp, rename it over the original) while a viewer has the
    // original open: replacing is the point, and a reader has nothing that could be lost
    using var fs = this._Engine();
    _WriteClosed(fs, "photo.jpg", [1]);
    var viewer = fs.Open("photo.jpg", AccessMode.Read, ShareMode.Read | ShareMode.Write | ShareMode.Delete);
    _WriteClosed(fs, "photo.jpg.tmp", [2]);

    fs.Rename("photo.jpg.tmp", "photo.jpg", RenameFlags.ReplaceExisting);
    fs.Close(viewer);

    _Read(fs, "photo.jpg").Should().Equal(new byte[] { 2 });
    _Displaced(fs).Should().BeEmpty();
  }

  [Test]
  [Category("Exception")]
  public void Rename_GivenItWouldNotReplaceButTheNameIsAFileStillBeingWritten_ThenItIsRefusedAndNothingMoves() {
    using var fs = this._Engine();
    var writing = fs.Create("t.bin", NodeKind.File, CreateFlags.None);
    fs.Write(writing, [1, 1, 1], 0, WriteMode.Normal);
    _WriteClosed(fs, "x.bin", [2, 2, 2]);

    var rename = () => fs.Rename("x.bin", "t.bin", RenameFlags.None);

    rename.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
    fs.Close(writing);
    _Read(fs, "x.bin").Should().Equal(new byte[] { 2, 2, 2 });
    _Read(fs, "t.bin").Should().Equal(new byte[] { 1, 1, 1 });
    _Displaced(fs).Should().BeEmpty();
  }

  #endregion

  #region a file deleted while open

  [TestCase(false, TestName = "Delete_GivenAnExistingFileStillOpenForWriting_WhenANewFileTakesTheName_ThenTheOldHandleNeverWritesIntoIt")]
  [TestCase(true, TestName = "Delete_GivenAFileBeingWrittenForTheFirstTime_WhenANewFileTakesTheName_ThenTheOldHandleNeverWritesIntoIt")]
  [Category("EdgeCase")]
  public void Delete_GivenAFileStillOpenForWriting_WhenANewFileTakesTheName_ThenTheOldHandleNeverWritesIntoIt(bool firstWrite) {
    // the delete was meant, so what the old writer had is gone with it; but a handle bound to the
    // NAME would carry on writing into whatever file takes the name next
    using var fs = this._Engine();
    if (!firstWrite)
      _WriteClosed(fs, "log.txt", [1, 1]);
    var stale = firstWrite ? fs.Create("log.txt", NodeKind.File, CreateFlags.None) : fs.Open("log.txt", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(stale, [4], 0, WriteMode.Normal);

    fs.Unlink("log.txt");
    _WriteClosed(fs, "log.txt", [5, 5, 5]);
    try {
      fs.Write(stale, [6], 1, WriteMode.Normal);
    } catch (PoolFsException) {
      // refusing the stale handle is an acceptable answer; writing into the new file is not
    }

    try {
      fs.Close(stale);
    } catch (PoolFsException) {
    }

    _Read(fs, "log.txt").Should().Equal(new byte[] { 5, 5, 5 }, "the new file belongs to its own writer alone");
  }

  #endregion

}
