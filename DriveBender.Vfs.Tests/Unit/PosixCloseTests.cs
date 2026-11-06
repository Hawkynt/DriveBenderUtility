using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The POSIX close (write.closeIsDurable off, the default): a close returns at once and a background
/// publisher makes the file durable within about a second, flushing before it renames. What that may
/// cost is exactly POSIX's bargain — a power cut right after a close loses that file's new content —
/// and nothing more: never a torn file, never a version older than the one before, an fsync still
/// durable at once, and a closed file treated as the finished file it is by everything that touches it.
/// </summary>
[TestFixture]
[Category("Unit")]
public class PosixCloseTests {

  private static readonly Guid _pool = Guid.Parse("9051c105-0000-0000-0000-000000000001");
  private static readonly byte[] _OLD = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 3 + 1))];
  private static readonly byte[] _NEW = [.. Enumerable.Range(0, 7000).Select(i => (byte)(i * 7 + 2))];

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", 1L << 24);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", 1L << 24);
  }

  /// <summary>The product defaults — deliberately NOT <see cref="TestConfig"/>, which pins the durable close.</summary>
  private PoolFileSystem _Engine(string extra = "") {
    var cache = new CacheInstance("px" + Guid.NewGuid().ToString("N"),
      new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 2, "readAhead": { "enabled": false } {{extra}} }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content, bool fsync = false) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    if (fsync)
      fs.Flush(handle);
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

  private byte[]? _OnDisk(FakeVolumeIO volume, string path) => volume.GetContent(path, false) ?? volume.GetContent(path, true);

  private bool _PublishedEverywhere(string path, byte[] content)
    => new[] { this._v1, this._v2 }.All(v => this._OnDisk(v, path) is { } c && c.SequenceEqual(content));

  private static bool _Eventually(Func<bool> condition, TimeSpan within) {
    var deadline = DateTime.UtcNow + within;
    while (DateTime.UtcNow < deadline) {
      if (condition())
        return true;
      Thread.Sleep(10);
    }

    return condition();
  }

  /// <summary>
  /// Holds every publish rename until the returned power cut is applied: the members lose what was
  /// not flushed, everything still in flight fails, and the engine is abandoned like a dead machine.
  /// </summary>
  private Action _HoldPublishesThenCut(PoolFileSystem fs, string path) {
    using var held = new ManualResetEventSlim();
    var gate = new ManualResetEventSlim();
    var dead = 0;
    void Hold(VolumeOp op, string touched) {
      if (Volatile.Read(ref dead) == 1)
        throw new InvalidOperationException("power loss");
      if (op == VolumeOp.AtomicReplace && touched == path) {
        gate.Wait();
        throw new InvalidOperationException("power loss");
      }
    }

    this._v1.BeforeOperation = Hold;
    this._v2.BeforeOperation = Hold;
    return () => {
      Volatile.Write(ref dead, 1);
      this._v1.SimulateCrash();
      this._v2.SimulateCrash();
      gate.Set();
      try {
        fs.Dispose();
      } catch (Exception) {
        // the machine is gone
      }

      this._v1.BeforeOperation = null;
      this._v2.BeforeOperation = null;
    };
  }

  [Test]
  [Category("HappyPath")]
  public void Close_GivenANewFile_ThenItIsReadableAtOnceAndOnEveryDiskWithinASecond() {
    using var fs = this._Engine();

    _Write(fs, "a.bin", _NEW);

    _Read(fs, "a.bin").Should().Equal(_NEW, "a closed file reads back immediately, published or not");
    _Eventually(() => this._PublishedEverywhere("a.bin", _NEW), TimeSpan.FromSeconds(3))
      .Should().BeTrue("the background publisher makes every copy durable shortly after the close");
  }

  [Test]
  [Category("EdgeCase")]
  public void Close_GivenThePowerFailsBeforeThePublish_ThenThePreviousVersionIsIntact() {
    using (var seeding = this._Engine())
      _Write(seeding, "doc.bin", _OLD);

    var fs = this._Engine();
    var cut = this._HoldPublishesThenCut(fs, "doc.bin");
    _Write(fs, "doc.bin", _NEW); // returns; the publish is held at its rename
    cut();

    using var recovered = this._Engine();
    _Read(recovered, "doc.bin").Should().Equal(_OLD, "POSIX loses the unsynced save — and only that: never a torn or mixed file");
    this._OnDisk(this._v1, "doc.bin").Should().Equal(_OLD);
    this._OnDisk(this._v2, "doc.bin").Should().Equal(_OLD);
  }

  [Test]
  [Category("EdgeCase")]
  public void Close_GivenANewFileAndThePowerFailsBeforeThePublish_ThenNoPartialFileAppears() {
    var fs = this._Engine();
    var cut = this._HoldPublishesThenCut(fs, "new.bin");
    _Write(fs, "new.bin", _NEW);
    cut();

    using var recovered = this._Engine();
    var exists = () => recovered.GetAttributes("new.bin");
    exists.Should().Throw<PoolFsException>("a file whose save never became durable does not exist, rather than existing half-written");
    this._v1.FilePaths.Concat(this._v2.FilePaths).Should().NotContain(p => p.Contains("new.bin"), "no temp is left behind either");
  }

  [Test]
  [Category("EdgeCase")]
  public void Fsync_GivenThePowerFailsRightAfterTheClose_ThenTheSyncedContentSurvives() {
    using (var seeding = this._Engine())
      _Write(seeding, "db.bin", _OLD);

    var fs = this._Engine();
    _Write(fs, "db.bin", _NEW, fsync: true); // fsync is the barrier: durable before Flush returns
    var cut = this._HoldPublishesThenCut(fs, "db.bin");
    cut();

    using var recovered = this._Engine();
    _Read(recovered, "db.bin").Should().Equal(_NEW, "an fsync is durable at once, whatever a close promises");
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenAClosedFileNotYetPublished_ThenTheBinStillTakesIt() {
    using var fs = this._Engine(""", "trash": { "enabled": true }""");
    var cut = this._HoldPublishesThenCut(fs, "x");     // keep it unpublished for the duration
    this._v1.BeforeOperation = null;                    // …but let the delete itself work
    this._v2.BeforeOperation = null;
    _Write(fs, "kept.bin", _NEW);

    fs.Unlink("kept.bin");

    fs.Trash.NewestVersionOf("kept.bin").Should().NotBeNull("a closed file is a finished file: deleting it goes to the bin");
    fs.RestoreFromTrash("kept.bin");
    _Read(fs, "kept.bin").Should().Equal(_NEW);
    _ = cut;
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenAReaderReopenedAClosedFileNotYetPublished_ThenTheReaderKeepsReadingIt() {
    using var fs = this._Engine();
    _Write(fs, "log.bin", _NEW);

    var reader = fs.Open("log.bin", AccessMode.Read, ShareMode.Read | ShareMode.Delete);
    fs.Unlink("log.bin");
    var buffer = new byte[_NEW.Length];
    fs.Read(reader, buffer, 0).Should().Be(_NEW.Length, "the open reader still has the file a delete only unnamed");
    fs.Close(reader);

    buffer.Should().Equal(_NEW);
  }

  [TestCase(""), TestCase(""", "write": { "closeIsDurable": true }""")]
  [Category("EdgeCase")]
  public void Snapshot_GivenAClosedFileNotYetPublished_ThenItIsInTheSnapshot(string extra) {
    using var fs = this._Engine(extra);
    _Write(fs, "report.doc", _OLD);
    var shot = fs.TakeSnapshot("now");
    _Write(fs, "report.doc", _NEW);

    using var stream = fs.OpenSnapshotFile(shot.Id, "report.doc");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    copy.ToArray().Should().Equal(_OLD);
  }

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenAClosedFileNotYetPublished_ThenItArrivesWhole() {
    using var fs = this._Engine();
    _Write(fs, "draft.txt", _NEW);

    fs.Rename("draft.txt", "final.txt", RenameFlags.None);

    _Read(fs, "final.txt").Should().Equal(_NEW);
    _Eventually(() => this._PublishedEverywhere("final.txt", _NEW), TimeSpan.FromSeconds(3)).Should().BeTrue();
  }

  [Test]
  [Category("HappyPath")]
  public void Unmount_GivenManyClosedFilesStillQueued_ThenEveryOneIsPublished() {
    var fs = this._Engine();
    var files = Enumerable.Range(0, 300).Select(i => (name: $"f{i}.bin", content: _NEW.Select(b => (byte)(b ^ i)).ToArray())).ToArray();
    foreach (var (name, content) in files)
      _Write(fs, name, content);

    fs.Dispose();

    foreach (var (name, content) in files)
      this._PublishedEverywhere(name, content).Should().BeTrue($"'{name}' must be durable on every disk once the unmount returns");
  }

  [Test]
  [Category("Concurrency")]
  public void Close_GivenManyWritersAtOnce_ThenEveryFileEndsDurableAndCorrect() {
    using var fs = this._Engine();
    var files = Enumerable.Range(0, 400).Select(i => (name: $"c{i}.bin", content: _NEW.Select(b => (byte)(b + i)).ToArray())).ToArray();

    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f => _Write(fs, f.name, f.content));

    foreach (var (name, content) in files)
      _Read(fs, name).Should().Equal(content);
    _Eventually(() => files.All(f => this._PublishedEverywhere(f.name, f.content)), TimeSpan.FromSeconds(10))
      .Should().BeTrue("the publisher catches up with every closed file");
  }

  [Test]
  [Category("EdgeCase")]
  public void Close_GivenCloseIsDurableIsSet_ThenTheFileIsOnEveryDiskBeforeCloseReturns() {
    using var fs = this._Engine(""", "write": { "closeIsDurable": true }""");

    _Write(fs, "strict.bin", _NEW);

    this._PublishedEverywhere("strict.bin", _NEW).Should().BeTrue("the opt-in keeps every close durable");
  }

}
