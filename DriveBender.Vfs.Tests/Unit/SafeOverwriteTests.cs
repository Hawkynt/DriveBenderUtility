using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Overwriting an existing file (truncate it, then write it again: what File.WriteAllBytes, a shell
/// redirect and most "save" commands do) must never leave the file as neither version. The classic
/// way a filesystem loses a file it was asked to UPDATE is to truncate it, fail to write the new
/// content (no room, a disk that drops), and keep the empty or half-written result. The new content
/// is written beside the old instead, on the same disks, and replaces it only at the writer's close,
/// by an atomic rename per copy; if any write failed, the old file is what remains.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SafeOverwriteTests {

  private static readonly Guid _pool = Guid.Parse("5afe0e00-0000-0000-0000-0000000000f1");
  private static readonly byte[] _OLD = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 3 + 1))];

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  private void _Disks(long capacity) {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity);
  }

  [SetUp]
  public void SetUp() => this._Disks(1L << 24);

  private PoolFileSystem _Engine(string extra = "") {
    var cache = new CacheInstance("ow" + Guid.NewGuid().ToString("N"),
      new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 2, "readAhead": { "enabled": false } {{extra}} }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Seed(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static byte[] _Read(PoolFileSystem fs, string path) {
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
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

  private byte[]?[] _Copies(string path) => [this._v1.GetContent(path, false) ?? this._v1.GetContent(path, true), this._v2.GetContent(path, false) ?? this._v2.GetContent(path, true)];

  private IEnumerable<string> _Temps() => this._v1.FilePaths.Concat(this._v2.FilePaths).Where(p => p.Contains("TEMP.$DRIVEBENDER", StringComparison.OrdinalIgnoreCase));

  /// <summary>Opens the file for the overwrite the way each platform does it.</summary>
  private static NodeHandle _BeginOverwrite(PoolFileSystem fs, string path, bool viaTruncate) {
    if (!viaTruncate)
      return fs.Create(path, NodeKind.File, CreateFlags.Truncate); // Windows CREATE_ALWAYS, File.WriteAllBytes

    var handle = fs.Open(path, AccessMode.ReadWrite, ShareMode.Read); // Linux open(O_TRUNC): open, then truncate to 0
    fs.SetLength(handle, 0);
    return handle;
  }

  [TestCase(false, TestName = "Overwrite_GivenItSucceeds_ThenEveryCopyHoldsTheNewContentAndNoTempRemains")]
  [TestCase(true, TestName = "Overwrite_GivenItSucceedsAfterATruncateThroughAnOpenHandle_ThenEveryCopyHoldsTheNewContent")]
  [Category("HappyPath")]
  public void Overwrite_GivenItSucceeds_ThenTheNewContentReplacesTheOld(bool viaTruncate) {
    using var fs = this._Engine();
    _Seed(fs, "doc.bin", _OLD);
    var fresh = new byte[] { 9, 8, 7 };

    var handle = _BeginOverwrite(fs, "doc.bin", viaTruncate);
    fs.Write(handle, fresh, 0, WriteMode.Normal);
    fs.Close(handle);

    _Read(fs, "doc.bin").Should().Equal(fresh);
    this._Copies("doc.bin").Should().AllSatisfy(c => c.Should().Equal(fresh), "both copies are the new file");
    this._Temps().Should().BeEmpty();
  }

  [TestCase(false, TestName = "Overwrite_GivenTheNewContentDoesNotFit_ThenTheOldFileSurvivesWhole")]
  [TestCase(true, TestName = "Overwrite_GivenTheNewContentDoesNotFitAfterATruncateThroughAnOpenHandle_ThenTheOldFileSurvivesWhole")]
  [Category("Exception")]
  public void Overwrite_GivenAWriteFails_ThenTheOldFileSurvives(bool viaTruncate) {
    // room for the old file and a little more, not for the new one
    this._Disks(_OLD.Length + 64 * 1024);
    using var fs = this._Engine();
    _Seed(fs, "doc.bin", _OLD);

    var handle = _BeginOverwrite(fs, "doc.bin", viaTruncate);
    var write = () => fs.Write(handle, new byte[256 * 1024], 0, WriteMode.Normal);
    write.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
    fs.Close(handle);

    _Read(fs, "doc.bin").Should().Equal(_OLD, "a failed update leaves the old version, never neither");
    this._Copies("doc.bin").Should().AllSatisfy(c => c.Should().Equal(_OLD));
    this._Temps().Should().BeEmpty("the new content that did not fit is discarded");
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenAFailedWriteIsRetriedSuccessfullyAfterRoomIsMade_ThenTheNewContentIsPublished() {
    // a failure the writer recovered from is not a reason to throw its save away
    this._Disks(_OLD.Length * 2 + 300 * 1024);
    using var fs = this._Engine();
    _Seed(fs, "doc.bin", _OLD);
    _Seed(fs, "ballast.bin", new byte[200 * 1024]);
    var fresh = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i * 13)).ToArray();

    var handle = _BeginOverwrite(fs, "doc.bin", viaTruncate: false);
    var tooBig = () => fs.Write(handle, fresh, 0, WriteMode.Normal);
    tooBig.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
    fs.Unlink("ballast.bin"); // the writer makes room
    fs.Write(handle, fresh, 0, WriteMode.Normal); // and writes the same range again
    fs.Close(handle);

    _Read(fs, "doc.bin").Should().Equal(fresh, "every byte that failed was written again, so the file is whole");
    this._Temps().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenOnlyPartOfAFailedRangeIsWrittenAgain_ThenTheOldFileIsKept() {
    this._Disks(_OLD.Length * 2 + 300 * 1024);
    using var fs = this._Engine();
    _Seed(fs, "doc.bin", _OLD);
    _Seed(fs, "ballast.bin", new byte[200 * 1024]);

    var handle = _BeginOverwrite(fs, "doc.bin", viaTruncate: false);
    var tooBig = () => fs.Write(handle, new byte[256 * 1024], 0, WriteMode.Normal);
    tooBig.Should().Throw<PoolFsException>();
    fs.Unlink("ballast.bin");
    fs.Write(handle, new byte[1024], 0, WriteMode.Normal); // the start again, not the rest
    fs.SetLength(handle, 256 * 1024);
    fs.Close(handle);

    _Read(fs, "doc.bin").Should().Equal(_OLD, "bytes that never arrived must not be published as zeros");
  }

  [Test]
  [Category("Exception")]
  public void NewFile_GivenAWriteFailedAndWasNeverWrittenAgain_ThenItIsNeverPublishedHalfWritten() {
    this._Disks(64 * 1024);
    using var fs = this._Engine();
    var handle = fs.Create("new.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1, 2, 3], 0, WriteMode.Normal);
    var tooBig = () => fs.Write(handle, new byte[256 * 1024], 3, WriteMode.Normal);
    tooBig.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
    fs.Close(handle);

    fs.ReadDirectory("").Should().NotContain(e => e.Name == "new.bin", "a file that could not be written whole never appears");
    this._Temps().Should().BeEmpty();
  }

  [Test]
  [Category("Exception")]
  public void Append_GivenItFailedForLackOfRoom_ThenTheFileBeingWrittenIsNotPublishedWithAHole() {
    this._Disks(64 * 1024);
    using var fs = this._Engine();
    var handle = fs.Create("log.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1, 2, 3], 0, WriteMode.Normal);
    var tooBig = () => fs.Write(handle, new byte[256 * 1024], 0, WriteMode.Append);
    tooBig.Should().Throw<PoolFsException>();
    fs.SetLength(handle, 3 + 256 * 1024);
    fs.Close(handle);

    fs.ReadDirectory("").Should().NotContain(e => e.Name == "log.bin");
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenTheFileIsDeletedBeforeTheWriterCloses_ThenNeitherVersionComesBack() {
    using var fs = this._Engine();
    _Seed(fs, "doc.bin", _OLD);
    var handle = _BeginOverwrite(fs, "doc.bin", viaTruncate: false);
    fs.Write(handle, [1, 2], 0, WriteMode.Normal);

    fs.Unlink("doc.bin");
    try {
      fs.Close(handle);
    } catch (PoolFsException) {
      // a handle on a deleted file may be refused; what matters is what is left
    }

    fs.ReadDirectory("").Should().NotContain(e => e.Name == "doc.bin", "a delete is a delete: the old version must not resurface");
    this._Copies("doc.bin").Should().AllSatisfy(c => c.Should().BeNull());
    this._Temps().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenTheFileIsRenamedBeforeTheWriterCloses_ThenTheNewContentArrivesUnderTheNewName() {
    using var fs = this._Engine();
    _Seed(fs, "doc.bin", _OLD);
    var handle = _BeginOverwrite(fs, "doc.bin", viaTruncate: false);
    fs.Write(handle, [5, 5], 0, WriteMode.Normal);

    fs.Rename("doc.bin", "renamed.bin", RenameFlags.None);
    fs.Write(handle, [6], 2, WriteMode.Normal);
    fs.Close(handle);

    _Read(fs, "renamed.bin").Should().Equal(new byte[] { 5, 5, 6 });
    fs.ReadDirectory("").Should().NotContain(e => e.Name == "doc.bin");
    this._Temps().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenTheOldFileHasAnotherReaderOpen_ThenItSeesTheFileAsTruncatedAndThenTheNewContent() {
    // a truncate is visible at once, as it is on any filesystem; it is only kept reversible
    using var fs = this._Engine();
    _Seed(fs, "doc.bin", _OLD);
    var reader = fs.Open("doc.bin", AccessMode.Read, ShareMode.Read | ShareMode.Write);

    var handle = _BeginOverwrite(fs, "doc.bin", viaTruncate: true);
    fs.GetAttributes("doc.bin").Length.Should().Be(0);
    fs.Write(handle, [4, 4, 4], 0, WriteMode.Normal);
    fs.Close(handle);

    var buffer = new byte[3];
    fs.Read(reader, buffer, 0).Should().Be(3);
    buffer.Should().Equal(4, 4, 4);
    fs.Close(reader);
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenThePowerIsCutAtEveryStep_ThenTheFileIsTheOldOrTheNewVersionWholeOnEveryCopy() {
    var fresh = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
    for (var step = 1; ; ++step) {
      this.SetUp();
      using (var seeding = this._Engine())
        _Seed(seeding, "doc.bin", _OLD);

      var fired = 0;
      var remaining = step;
      void Cut(VolumeOp op, string path) {
        if (Volatile.Read(ref fired) == 1 || Interlocked.Decrement(ref remaining) == 0) {
          Interlocked.Exchange(ref fired, 1);
          throw new InvalidOperationException("power loss");
        }
      }

      var fs = this._Engine();
      this._v1.BeforeOperation = Cut;
      this._v2.BeforeOperation = Cut;
      try {
        var handle = fs.Create("doc.bin", NodeKind.File, CreateFlags.Truncate);
        fs.Write(handle, fresh, 0, WriteMode.Normal);
        fs.Close(handle);
      } catch (Exception) {
        // the machine went down part-way
      }

      try {
        fs.Dispose();
      } catch (Exception) {
      }

      this._v1.BeforeOperation = null;
      this._v2.BeforeOperation = null;
      if (fired == 0) {
        step.Should().BeGreaterThan(4, "the overwrite is several steps, each one cut");
        break;
      }

      this._v1.SimulateCrash();
      this._v2.SimulateCrash();
      using var recovered = this._Engine();
      var found = _Read(recovered, "doc.bin");
      (found.SequenceEqual(_OLD) || found.SequenceEqual(fresh)).Should().BeTrue(
        $"power cut at step {step}: the file must be the old or the new version whole, not {found.Length} bytes of something else");
      var copies = this._Copies("doc.bin").Where(c => c != null).ToArray();
      copies.Should().AllSatisfy(c => c.Should().Equal(found), $"power cut at step {step}: every copy agrees");
    }
  }

}
