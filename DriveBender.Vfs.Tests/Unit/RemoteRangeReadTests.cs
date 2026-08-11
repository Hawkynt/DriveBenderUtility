using DivisonM.Backends;
using DivisonM.Vfs;
using FluentAssertions;
using Hawkynt.CloudStorage;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Provider-level range reads and streaming transfers (FR-REMOTE-READ).
///
/// The engine reads a file BLOCK BY BLOCK. Against a store that can only fetch whole objects,
/// every block miss moves the entire object — a cold 1 GiB read in 128 KiB blocks moved roughly
/// eight thousand times the file. A bounded object cache reduced that to one download per file,
/// which is linear again but still means transferring 1 GiB to serve 128 KiB, and holding it in
/// memory. A provider that serves ranges should transfer the blocks and nothing else.
///
/// These tests count what actually crosses the store boundary, because that is the only thing
/// that distinguishes the three cases.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RemoteRangeReadTests {

  /// <summary>Records exactly what the engine asked the store for, and how many bytes that moved.</summary>
  private sealed class AccountingStore(IWholeFileStore inner, StoreCaps caps) : IWholeFileStore {

    public int WholeDownloads;
    public int RangeReads;
    public int StreamUploads;
    public int ArrayUploads;
    public long BytesRead;

    public StoreCaps Caps => caps;
    public bool ThreadSafe => true;

    public void Connect() => inner.Connect();
    public bool Probe() => inner.Probe();

    public byte[] Download(string p) {
      ++this.WholeDownloads;
      var content = inner.Download(p);
      this.BytesRead += content.LongLength;
      return content;
    }

    public Stream OpenRead(string p) {
      ++this.WholeDownloads;
      var content = inner.Download(p);
      this.BytesRead += content.LongLength;
      return new MemoryStream(content, false);
    }

    public Stream OpenReadRange(string p, long offset, long count) {
      // an accounting store that does NOT declare RangeRead still has to answer correctly — the
      // interface default is what makes that true, and it is measured here as a whole download
      if ((caps & StoreCaps.RangeRead) == 0) {
        var whole = this.Download(p);
        if (offset >= whole.LongLength || count <= 0)
          return new MemoryStream([], false);

        return new MemoryStream(whole, (int)offset, (int)Math.Min(count, whole.LongLength - offset), false);
      }

      ++this.RangeReads;
      var stream = inner.OpenReadRange(p, offset, count);
      this.BytesRead += stream.Length;
      return stream;
    }

    public void Upload(string p, byte[] c) {
      ++this.ArrayUploads;
      inner.Upload(p, c);
    }

    public void Upload(string p, Stream c, long length = -1) {
      ++this.StreamUploads;
      inner.Upload(p, c, length);
    }

    public void DeleteFile(string p) => inner.DeleteFile(p);
    public StoreMeta? Stat(string p) => inner.Stat(p);
    public void CreateFolder(string p) => inner.CreateFolder(p);
    public void DeleteFolder(string p) => inner.DeleteFolder(p);
    public IEnumerable<StoreEntry> List(string p) => inner.List(p);
    public void Dispose() => inner.Dispose();
  }

  private string _root = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "dbrange-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
  }

  [TearDown]
  public void TearDown() {
    if (Directory.Exists(this._root))
      Directory.Delete(this._root, true);
  }

  private AccountingStore _Store(StoreCaps caps) => new(new DirectoryStore(this._root), caps);

  private static void _Write(WholeFileVolumeIO volume, string path, byte[] content) {
    using var stream = volume.OpenWrite(path, false, true);
    stream.Write(content, 0, content.Length);
    stream.Flush();
  }

  private static byte[] _ReadInBlocks(WholeFileVolumeIO volume, string path, int total, int blockSize) {
    var buffer = new byte[total];
    using var stream = volume.OpenRead(path, false);
    for (var offset = 0; offset < total; offset += blockSize) {
      stream.Seek(offset, SeekOrigin.Begin);
      var wanted = Math.Min(blockSize, total - offset);
      var filled = 0;
      while (filled < wanted) {
        var read = stream.Read(buffer, offset + filled, wanted - filled);
        if (read <= 0)
          break;

        filled += read;
      }
    }

    return buffer;
  }

  [Test]
  [Category("HappyPath")]
  public void BlockRead_GivenAProviderThatServesRanges_ThenOnlyTheBlocksMoveAndNoWholeObjectIsFetched() {
    const int size = 4 * 1024 * 1024;
    const int block = 128 * 1024;

    var store = this._Store(StoreCaps.RangeRead | StoreCaps.StreamingUpload | StoreCaps.StreamingDownload);
    using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "ranged", "REMOTE-R", store);

    var content = new byte[size];
    new Random(21).NextBytes(content);
    _Write(volume, "big.bin", content);

    store.BytesRead = 0;
    store.WholeDownloads = 0;
    store.RangeReads = 0;

    _ReadInBlocks(volume, "big.bin", size, block).Should().Equal(content, "a ranged read must still return the exact bytes");

    store.WholeDownloads.Should().Be(0, "a provider that serves ranges must never be asked for the whole object to satisfy a block");
    store.RangeReads.Should().BeGreaterThan(0, "the read must actually have gone through the range path");

    // the read window rounds requests out, so a little over the file is expected; transferring a
    // MULTIPLE of it is the regression this guards
    store.BytesRead.Should().BeLessThan(size * 2L,
      $"reading {size / 1024} KiB moved {store.BytesRead / 1024} KiB — the range path is fetching far more than it needs");
  }

  [Test]
  [Category("HappyPath")]
  public void PartialRead_GivenALargeObject_ThenRangesMoveAFractionOfWhatAWholeObjectFetchWould() {
    // The case that actually hurts users: opening a large file and reading a little of it — a
    // media player seeking, a thumbnailer reading a header, the engine checking one block. The
    // whole-object path has to move the entire file to answer; ranges move the window.
    const int size = 8 * 1024 * 1024;
    const int wanted = 64 * 1024;

    var content = new byte[size];
    new Random(25).NextBytes(content);

    long RangedBytes(StoreCaps caps) {
      var store = this._Store(caps);
      using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "member", "REMOTE-P", store);
      _Write(volume, "movie.bin", content);

      store.BytesRead = 0;
      using var stream = volume.OpenRead("movie.bin", false);
      stream.Seek(size / 2, SeekOrigin.Begin);
      var buffer = new byte[wanted];
      var filled = 0;
      while (filled < wanted) {
        var read = stream.Read(buffer, filled, wanted - filled);
        if (read <= 0)
          break;

        filled += read;
      }

      buffer.Should().Equal(content.AsSpan(size / 2, wanted).ToArray(), "the bytes must be right whichever path served them");
      return store.BytesRead;
    }

    var withRanges = RangedBytes(StoreCaps.RangeRead);
    var withoutRanges = RangedBytes(StoreCaps.None);

    withoutRanges.Should().BeGreaterThanOrEqualTo(size, "the whole-object path has to move the entire file to answer a 64 KiB read");
    withRanges.Should().BeLessThan(size / 4,
      $"a {wanted / 1024} KiB read from an {size / 1024 / 1024} MiB object moved {withRanges / 1024} KiB — ranges are not being used");
  }

  [Test]
  [Category("EdgeCase")]
  public void BlockRead_GivenAProviderWithoutRanges_ThenItStillReadsCorrectlyViaTheWholeObjectFallback() {
    const int size = 512 * 1024;
    const int block = 64 * 1024;

    var store = this._Store(StoreCaps.None);
    using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "whole", "REMOTE-W", store);

    var content = new byte[size];
    new Random(22).NextBytes(content);
    _Write(volume, "plain.bin", content);

    store.WholeDownloads = 0;
    _ReadInBlocks(volume, "plain.bin", size, block).Should().Equal(content,
      "a provider without range support must remain correct — the capability is about cost, not correctness");

    // the object cache is what keeps this at ONE download rather than one per block
    store.WholeDownloads.Should().BeLessThanOrEqualTo(1,
      "the whole-object path must still collapse a file's block reads into a single download");
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenARangedProvider_ThenSeekingBackwardsAndAcrossWindowsStaysCorrect() {
    const int size = 300 * 1024;
    var store = this._Store(StoreCaps.RangeRead);
    using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "ranged", "REMOTE-R", store);

    var content = new byte[size];
    new Random(23).NextBytes(content);
    _Write(volume, "seek.bin", content);

    using var stream = volume.OpenRead("seek.bin", false);
    stream.Length.Should().Be(size);

    // deliberately out of order and straddling the internal window boundary
    foreach (var offset in new[] { size - 100, 0, 200 * 1024, 1024, size - 1, 128 * 1024 }) {
      var wanted = Math.Min(4096, size - offset);
      var buffer = new byte[wanted];
      stream.Seek(offset, SeekOrigin.Begin);
      var filled = 0;
      while (filled < wanted) {
        var read = stream.Read(buffer, filled, wanted - filled);
        if (read <= 0)
          break;

        filled += read;
      }

      filled.Should().Be(wanted, $"a read of {wanted} bytes at {offset} must be satisfied in full");
      buffer.Should().Equal(content.AsSpan(offset, wanted).ToArray(), $"the bytes at {offset} must be correct after seeking");
    }

    stream.Seek(size, SeekOrigin.Begin);
    stream.Read(new byte[16], 0, 16).Should().Be(0, "a read at the end of the object yields nothing, as on a local file");
  }

  [Test]
  [Category("HappyPath")]
  public void Upload_GivenAStreamingProvider_ThenContentIsSentAsAStreamRatherThanAnArray() {
    var store = this._Store(StoreCaps.StreamingUpload);
    using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "streamed", "REMOTE-S", store);

    _Write(volume, "streamed.bin", new byte[64 * 1024]);

    store.StreamUploads.Should().BeGreaterThan(0, "the staging buffer must be handed over as a stream, not copied into a fresh array");
    store.ArrayUploads.Should().Be(0, "materialising the object again would hold it in memory twice");
  }

  [Test]
  [Category("EdgeCase")]
  public void Truncate_GivenARangedProvider_ThenOnlyTheSurvivingPrefixIsRead() {
    const int size = 256 * 1024;
    const int keep = 4 * 1024;

    var store = this._Store(StoreCaps.RangeRead | StoreCaps.StreamingUpload);
    using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "ranged", "REMOTE-R", store);

    var content = new byte[size];
    new Random(24).NextBytes(content);
    _Write(volume, "shrink.bin", content);

    store.BytesRead = 0;
    store.WholeDownloads = 0;
    volume.Truncate("shrink.bin", false, keep);

    volume.Stat("shrink.bin", false)!.Value.Length.Should().Be(keep);
    using (var check = volume.OpenRead("shrink.bin", false)) {
      var kept = new byte[keep];
      var filled = 0;
      while (filled < keep) {
        var read = check.Read(kept, filled, keep - filled);
        if (read <= 0)
          break;

        filled += read;
      }

      kept.Should().Equal(content.AsSpan(0, keep).ToArray(), "the surviving prefix must be exactly the original bytes");
    }

    store.WholeDownloads.Should().Be(0, "shrinking must not pull the pre-truncation object down in full");
  }

  [Test]
  [Category("EdgeCase")]
  public void RenameFolder_GivenASubtreeOfFiles_ThenEachFileIsMovedByStreamingRatherThanBuffering() {
    var store = this._Store(StoreCaps.RangeRead | StoreCaps.StreamingUpload | StoreCaps.StreamingDownload);
    using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "ranged", "REMOTE-R", store);

    volume.EnsureFolder("from", false);
    var payloads = new Dictionary<string, byte[]>();
    for (var i = 0; i < 4; ++i) {
      var content = new byte[32 * 1024];
      new Random(30 + i).NextBytes(content);
      payloads[$"from/f{i}.bin"] = content;
      _Write(volume, $"from/f{i}.bin", content);
    }

    store.ArrayUploads = 0;
    store.StreamUploads = 0;
    volume.RenameFolder("from", "to");

    foreach (var (path, content) in payloads) {
      var moved = path.Replace("from/", "to/");
      volume.FileExists(moved, false).Should().BeTrue($"'{moved}' must exist after the subtree move");
      using var stream = volume.OpenRead(moved, false);
      using var buffer = new MemoryStream();
      stream.CopyTo(buffer);
      buffer.ToArray().Should().Equal(content, $"'{moved}' must be byte-identical after the move");
    }

    store.StreamUploads.Should().Be(payloads.Count, "every moved file must be streamed to its target");
    store.ArrayUploads.Should().Be(0, "a subtree move must never materialise a file in memory — it does not need to see the bytes at all");
  }

}

/// <summary>
/// The one place an asynchronous SDK call becomes a synchronous one. The hazard is not slowness
/// but deadlock: blocking on a task whose continuations are posted back to the blocked thread's
/// own synchronization context can never complete.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SyncBridgeTests {

  /// <summary>A single-threaded context that only runs work when its message loop is pumped — like a UI thread's.</summary>
  private sealed class SingleThreadedContext : SynchronizationContext {

    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback callback, object? state)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => this._queue.Add((d, state));

    /// <summary>Deliberately never pumped by the test: work posted here would only run if the blocked thread were free.</summary>
    public int Pending => this._queue.Count;
  }

  [Test]
  [Category("Exception")]
  public void Run_GivenASynchronizationContextIsInstalled_ThenItCompletesInsteadOfDeadlocking() {
    var previous = SynchronizationContext.Current;
    var context = new SingleThreadedContext();
    try {
      SynchronizationContext.SetSynchronizationContext(context);

      // an SDK call that yields: with a naive GetAwaiter().GetResult() the continuation is posted
      // to this context, which only runs when THIS thread is free — and this thread is blocked
      // waiting for it. The bridge escapes the context first, so it completes.
      var completed = false;
      var worker = new Thread(() => {
        SynchronizationContext.SetSynchronizationContext(context);
        var answer = SyncBridge.Run(async () => {
          await Task.Delay(20);
          return 42;
        });

        completed = answer == 42;
      }) { IsBackground = true };

      worker.Start();
      worker.Join(TimeSpan.FromSeconds(15)).Should().BeTrue("blocking on an SDK call must not deadlock against a captured context");
      completed.Should().BeTrue();
    } finally {
      SynchronizationContext.SetSynchronizationContext(previous);
    }
  }

  [Test]
  [Category("Exception")]
  public void Run_GivenTheOperationFails_ThenTheProvidersOwnExceptionSurfaces() {
    // providers filter on their SDK's exception types (`catch (AmazonS3Exception e) when (...)`),
    // so an AggregateException wrapper would silently stop every one of those filters matching
    var throwing = () => SyncBridge.Run<int>(() => throw new CloudStorageException(CloudStorageError.NotFound, "gone"));

    throwing.Should().Throw<CloudStorageException>().Which.Error.Should().Be(CloudStorageError.NotFound);
  }

  [Test]
  [Category("HappyPath")]
  public void Run_GivenNoContext_ThenItReturnsTheResultDirectly() {
    SyncBridge.Run(async () => {
      await Task.Yield();
      return "ok";
    }).Should().Be("ok");

    var ran = false;
    SyncBridge.Run(async () => {
      await Task.Yield();
      ran = true;
    });

    ran.Should().BeTrue();
  }

}
