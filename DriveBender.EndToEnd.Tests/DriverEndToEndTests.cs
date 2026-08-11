using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The pool as the USER's applications see it: a mounted filesystem, driven through
/// <see cref="System.IO"/> against a drive letter (WinFsp/Dokan on Windows) or a mountpoint
/// (FUSE on Linux). Nothing here touches the engine — every operation crosses the OS boundary,
/// through the driver, into the adapter, into the engine, and back.
///
/// This is the tier that catches the things a unit suite structurally cannot: the driver not
/// installed correctly, the adapter mistranslating a callback, the mount refusing to come up,
/// duplication not reaching the member folders on disk. The engine's own 574 tests were green
/// while mounting a local pool was impossible, because all of them drive an in-memory fake.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable] // one mount at a time: they contend for drive letters and the driver
public class DriverEndToEndTests {

  private MountedPool _pool = null!;

  [OneTimeSetUp]
  public void MountPool() => this._pool = MountedPool.Create();

  [OneTimeTearDown]
  public void UnmountPool() => this._pool?.Dispose();

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  [Test]
  [Category("HappyPath")]
  public void Mount_WhenPoolIsMounted_ThenTheOsSeesAUsableFilesystem() {
    Directory.Exists(this._pool.MountPath).Should().BeTrue($"the mount target must exist.{Environment.NewLine}{this._pool.MountLog}");

    // enumerating through the OS is what proves the driver is actually serving, not just that a
    // path resolves
    var listing = () => Directory.EnumerateFileSystemEntries(this._pool.MountPath).ToArray();
    listing.Should().NotThrow("the mounted filesystem must be enumerable");
  }

  [Test]
  [Category("HappyPath")]
  public void WriteReadDelete_GivenAFileThroughTheOs_ThenItRoundTripsAndReachesEveryMember() {
    var path = this._pool.PathTo("roundtrip.bin");
    var content = _Payload(64 * 1024, 1);

    File.WriteAllBytes(path, content);
    File.ReadAllBytes(path).Should().Equal(content, "a file written through the driver must read back byte-identical");
    new FileInfo(path).Length.Should().Be(content.Length);

    // duplication is the product's whole point: the bytes must exist on the members' real folders,
    // not merely be served back through the same cache that accepted them
    var copies = this._pool.PhysicalCopies("roundtrip.bin");
    copies.Should().NotBeEmpty("the file must be present on at least one member's real folder");
    foreach (var (where, bytes) in copies)
      bytes.Should().Equal(content, $"the copy at '{where}' must match what was written");

    File.Delete(path);
    File.Exists(path).Should().BeFalse();
    this._pool.PhysicalCopies("roundtrip.bin").Should().BeEmpty("deleting through the driver must remove every physical copy");
  }

  [Test]
  [Category("HappyPath")]
  public void Directories_GivenATreeCreatedThroughTheOs_ThenItEnumeratesAndRemoves() {
    var branch = this._pool.PathTo("tree", "deep", "leaf");
    Directory.CreateDirectory(branch);
    Directory.Exists(branch).Should().BeTrue();

    File.WriteAllText(Path.Combine(branch, "a.txt"), "alpha");
    File.WriteAllText(Path.Combine(branch, "b.txt"), "beta");

    Directory.EnumerateFiles(branch).Select(Path.GetFileName).Should().BeEquivalentTo(["a.txt", "b.txt"]);
    Directory.EnumerateDirectories(this._pool.PathTo("tree")).Select(Path.GetFileName).Should().BeEquivalentTo(["deep"]);
    File.ReadAllText(Path.Combine(branch, "a.txt")).Should().Be("alpha");

    Directory.Delete(this._pool.PathTo("tree"), recursive: true);
    Directory.Exists(this._pool.PathTo("tree")).Should().BeFalse();
  }

  [Test]
  [Category("HappyPath")]
  public void Append_GivenRepeatedOpens_ThenTheFileGrowsMonotonically() {
    var path = this._pool.PathTo("append.log");
    var expected = new System.Text.StringBuilder();

    for (var line = 0; line < 20; ++line) {
      var text = $"line {line}{Environment.NewLine}";
      File.AppendAllText(path, text);
      expected.Append(text);
    }

    File.ReadAllText(path).Should().Be(expected.ToString(), "appends through the driver must accumulate in order");
  }

  [Test]
  [Category("EdgeCase")]
  public void Seek_GivenRandomAccessWrites_ThenTheFileReflectsEveryUpdate() {
    var path = this._pool.PathTo("random.bin");
    var content = _Payload(256 * 1024, 2);
    File.WriteAllBytes(path, content);

    // overwrite scattered windows, out of order, through a normal FileStream
    var patches = new[] { (offset: 200_000, seed: 11), (offset: 1024, seed: 12), (offset: 128 * 1024, seed: 13) };
    using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      foreach (var (offset, seed) in patches) {
        var patch = _Payload(4096, seed);
        stream.Seek(offset, SeekOrigin.Begin);
        stream.Write(patch, 0, patch.Length);
        patch.CopyTo(content.AsSpan(offset));
      }

      stream.Flush();
    }

    File.ReadAllBytes(path).Should().Equal(content, "random-access writes must land exactly where they were addressed");
  }

  [Test]
  [Category("EdgeCase")]
  public void Truncate_GivenSetLength_ThenTheFileShrinksAndGrowsZeroFilled() {
    var path = this._pool.PathTo("resize.bin");
    var content = _Payload(32 * 1024, 3);
    File.WriteAllBytes(path, content);

    using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      stream.SetLength(1024);

    File.ReadAllBytes(path).Should().Equal(content.AsSpan(0, 1024).ToArray(), "a shrink keeps the surviving prefix");

    using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
      stream.SetLength(4096);

    var grown = File.ReadAllBytes(path);
    grown.Length.Should().Be(4096);
    grown.AsSpan(0, 1024).ToArray().Should().Equal(content.AsSpan(0, 1024).ToArray());
    grown.AsSpan(1024).ToArray().Should().AllSatisfy(b => b.Should().Be(0), "a grow is zero-filled");
  }

  [Test]
  [Category("HappyPath")]
  public void Rename_GivenFilesAndFolders_ThenBothMoveAndKeepTheirContent() {
    var from = this._pool.PathTo("before.bin");
    var to = this._pool.PathTo("after.bin");
    var content = _Payload(8192, 4);
    File.WriteAllBytes(from, content);

    File.Move(from, to);
    File.Exists(from).Should().BeFalse();
    File.ReadAllBytes(to).Should().Equal(content);

    var folderFrom = this._pool.PathTo("folder-before");
    var folderTo = this._pool.PathTo("folder-after");
    Directory.CreateDirectory(folderFrom);
    File.WriteAllText(Path.Combine(folderFrom, "inside.txt"), "kept");

    Directory.Move(folderFrom, folderTo);
    Directory.Exists(folderFrom).Should().BeFalse();
    File.ReadAllText(Path.Combine(folderTo, "inside.txt")).Should().Be("kept", "a folder rename must carry its children");
  }

  [Test]
  [Category("EdgeCase")]
  public void LargeFile_GivenAMultiMegabyteStream_ThenItRoundTripsThroughTheDriver() {
    const int size = 24 * 1024 * 1024;
    var path = this._pool.PathTo("large.bin");
    var content = _Payload(size, 5);

    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
      stream.Write(content, 0, content.Length);

    new FileInfo(path).Length.Should().Be(size);

    // read back in blocks, the way a real consumer streams a large file
    var readBack = new byte[size];
    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)) {
      var filled = 0;
      while (filled < size) {
        var read = stream.Read(readBack, filled, Math.Min(1 << 16, size - filled));
        if (read <= 0)
          break;

        filled += read;
      }

      filled.Should().Be(size);
    }

    readBack.Should().Equal(content, "a large file must survive the round trip intact");
  }

  [Test]
  [Category("EdgeCase")]
  public void Concurrency_GivenManyReadersAndWritersThroughTheOs_ThenNoFileIsCorrupted() {
    const int files = 4;
    const int rounds = 15;
    const int length = 32 * 1024;

    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    var paths = Enumerable.Range(0, files).Select(f => this._pool.PathTo($"conc{f}.bin")).ToArray();
    foreach (var path in paths)
      File.WriteAllBytes(path, _Payload(length, 100));

    var writers = Enumerable.Range(0, files).Select(file => new Thread(() => {
      for (var round = 1; round <= rounds; ++round)
        try {
          File.WriteAllBytes(paths[file], _Payload(length, file * 1000 + round));
        } catch (IOException) {
          // a reader may hold the file briefly — a refusal is legal, corruption is not
        }
    }) { IsBackground = true }).ToArray();

    var readers = Enumerable.Range(0, files).Select(file => new Thread(() => {
      for (var round = 0; round < rounds * 3; ++round)
        try {
          var got = File.ReadAllBytes(paths[file]);
          if (got.Length is not (0 or length))
            failures.Add($"'{paths[file]}' read back {got.Length} bytes, which is neither empty nor the full {length}");
        } catch (IOException) {
          // legal under contention
        }
    }) { IsBackground = true }).ToArray();

    foreach (var thread in writers.Concat(readers))
      thread.Start();
    foreach (var thread in writers.Concat(readers))
      thread.Join(TimeSpan.FromMinutes(3)).Should().BeTrue("no filesystem operation may hang the driver");

    failures.Should().BeEmpty();

    // every file settles on a whole payload of the right size
    foreach (var path in paths)
      new FileInfo(path).Length.Should().Be(length);
  }

  [Test]
  [Category("EdgeCase")]
  public void FreeSpace_GivenTheMountedVolume_ThenTheOsReportsPlausibleCapacity() {
    // FR-STAT: the pool reports aggregate capacity, and the OS surfaces it — a zero here is what
    // makes an installer or a copy dialog refuse to write to the pool
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("DriveInfo reports the backing filesystem for a FUSE mountpoint path, not the pool");

    var drive = new DriveInfo(this._pool.MountPath);
    drive.TotalSize.Should().BeGreaterThan(0, "a mounted pool must report a total size");
    drive.AvailableFreeSpace.Should().BeGreaterThan(0, "a mounted pool must report free space");
  }

}
