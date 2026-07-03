using DivisonM.Vfs;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The pooled-handle positional I/O layer over a real (temp) directory: correctness under
/// concurrency, and pool invalidation around delete/replace/rename (NFR-THROUGHPUT).
/// </summary>
[TestFixture]
[Category("Unit")]
public class LocalVolumeIOTests {

  private string _root = null!;
  private LocalVolumeIO _volume = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "dbu-lvio-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
    this._volume = new(Guid.NewGuid(), "test", this._root, "PHYS-TEST");
  }

  [TearDown]
  public void TearDown() {
    try {
      Directory.Delete(this._root, true);
    } catch (IOException) {
      // best-effort cleanup
    }
  }

  private void _Write(string path, byte[] content) {
    using var stream = this._volume.OpenWrite(path, false, true);
    stream.Write(content, 0, content.Length);
    stream.Flush();
  }

  private byte[] _Read(string path) {
    using var stream = this._volume.OpenRead(path, false);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  [Test]
  [Category("HappyPath")]
  public void OpenWrite_GivenPooledHandles_WhenRoundTripped_ThenContentExact() {
    var content = new byte[70_000];
    new Random(3).NextBytes(content);

    this._Write("docs/a.bin", content);

    this._Read("docs/a.bin").Should().Equal(content);
    this._volume.Stat("docs/a.bin", false)!.Value.Length.Should().Be(content.Length, "the pooled write handle reports the live length");
  }

  [Test]
  [Category("HappyPath")]
  public void OpenRead_GivenManyConcurrentPositionalReaders_WhenSlicing_ThenEverySliceExact() {
    var content = new byte[256 * 1024];
    new Random(9).NextBytes(content);
    this._Write("big.bin", content);

    // 16 threads, each reading a distinct slice through its own stream over the SAME pooled handle
    Parallel.For(0, 16, worker => {
      var offset = worker * (content.Length / 16);
      var slice = new byte[content.Length / 16];
      using var stream = this._volume.OpenRead("big.bin", false);
      stream.Seek(offset, SeekOrigin.Begin);
      var got = 0;
      while (got < slice.Length) {
        var n = stream.Read(slice, got, slice.Length - got);
        if (n == 0) break;
        got += n;
      }

      slice.Should().Equal(content.AsSpan(offset, slice.Length).ToArray(), $"worker {worker} must see its exact slice");
    });
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenAFileJustWritten_WhenDeleted_ThenPooledHandleDoesNotBlockIt() {
    this._Write("gone.bin", [1, 2, 3]);

    this._volume.Delete("gone.bin", false);

    this._volume.FileExists("gone.bin", false).Should().BeFalse();
  }

  [Test]
  [Category("EdgeCase")]
  public void RenameFolder_GivenFilesJustWritten_WhenRenamed_ThenPooledHandlesDoNotBlockIt() {
    this._Write("old/f1.bin", [1]);
    this._Write("old/f2.bin", [2]);

    this._volume.RenameFolder("old", "new");

    this._volume.FolderExists("new", false).Should().BeTrue();
    this._volume.FolderExists("old", false).Should().BeFalse();
    this._Read("new/f1.bin").Should().Equal(new byte[] { 1 });
  }

  [Test]
  [Category("HappyPath")]
  public void AtomicReplace_GivenStagedContent_WhenPublished_ThenVisibleAndPoolConsistent() {
    this._Write("target.bin", [9, 9, 9]);
    this._Write("staged.tmp", [1, 2]);

    this._volume.AtomicReplace("staged.tmp", "target.bin", false);

    this._Read("target.bin").Should().Equal(new byte[] { 1, 2 });
    this._volume.FileExists("staged.tmp", false).Should().BeFalse();
  }

  [Test]
  [Category("Exception")]
  public void OpenRead_GivenMissingFile_WhenOpened_ThenNotFound() {
    var act = () => this._volume.OpenRead("nope.bin", false);

    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("HappyPath")]
  public void Truncate_GivenPooledHandle_WhenShrunkAndGrown_ThenLengthTracks() {
    this._Write("size.bin", new byte[100]);

    this._volume.Truncate("size.bin", false, 40);
    this._volume.Stat("size.bin", false)!.Value.Length.Should().Be(40);

    this._volume.Truncate("size.bin", false, 200);
    this._volume.Stat("size.bin", false)!.Value.Length.Should().Be(200, "growing zero-fills");
    this._Read("size.bin").Skip(40).Should().OnlyContain(b => b == 0);
  }

}
