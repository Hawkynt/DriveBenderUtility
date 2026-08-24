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

    // Duplication is the product's whole point: the bytes must exist on the members' real folders,
    // not merely be served back through the same cache that accepted them. Waited for rather than
    // sampled — the owed copies converge in the background by design, and a FUSE release is
    // asynchronous, so an immediate check measures the harness rather than the pool.
    var copies = this._pool.WaitForPhysicalCopies("roundtrip.bin");
    copies.Should().NotBeEmpty(
      "the file must reach at least one member's real folder. What is actually on the members:"
      + Environment.NewLine + this._pool.DescribeMembers());
    foreach (var (where, bytes) in copies)
      bytes.Should().Equal(content, $"the copy at '{where}' must match what was written");

    File.Delete(path);
    File.Exists(path).Should().BeFalse();
    this._pool.WaitForNoPhysicalCopies("roundtrip.bin")
      .Should().BeEmpty("deleting through the driver must remove every physical copy");
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

    // a DISTINCT seed per file, including for the initial content: the block-ownership check below
    // identifies a file by its bytes, and seeding every file's first version identically would make
    // four files indistinguishable and report their own data as somebody else's
    for (var file = 0; file < files; ++file)
      File.WriteAllBytes(paths[file], _Payload(length, file * 1000));

    var writers = Enumerable.Range(0, files).Select(file => new Thread(() => {
      for (var round = 1; round <= rounds; ++round)
        try {
          File.WriteAllBytes(paths[file], _Payload(length, file * 1000 + round));
        } catch (IOException) {
          // a reader may hold the file briefly — a refusal is legal, corruption is not
        }
    }) { IsBackground = true }).ToArray();

    // What an in-place overwrite does NOT promise, measured rather than argued.
    //
    // `File.WriteAllBytes` truncates and then writes, and a whole-file `read` is not atomic against
    // that. This scenario used to require every read to be empty or exactly `length`, and no
    // filesystem provides it: the same four writers and four readers against a plain tmpfs
    // directory produce 1-6 short reads per run, and against btrfs 2-4 — the pool is not doing
    // anything the bare kernel does not. It "passed" on Windows only because share violations there
    // turn the same race into the IOException below instead of a short read. Content is not
    // promised either: a plain filesystem occasionally hands back a full-length BLEND of two
    // versions, because the reader read some pages before the overwrite and some after.
    //
    // What is never legal on any filesystem, and is entirely the pool's own to get right, is a byte
    // that belongs to a DIFFERENT FILE. That is the failure a pool can produce and a plain
    // filesystem cannot — a block cache keyed so two paths collide, a placement resolved to the
    // wrong member, a handle reused across names. The payloads are random per file and per round,
    // so a block fingerprint identifies its owner beyond coincidence.
    var versions = Enumerable.Range(0, files)
      .Select(file => Enumerable.Range(0, rounds + 1).Select(round => _Payload(length, file * 1000 + round)).ToArray())
      .ToArray();

    const int fingerprintBlock = 4096;
    var owners = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var file = 0; file < files; ++file)
      foreach (var version in versions[file])
        for (var at = 0; at + 16 <= version.Length; at += fingerprintBlock)
          owners[Convert.ToHexString(version, at, 16)] = file;

    var readers = Enumerable.Range(0, files).Select(file => new Thread(() => {
      for (var round = 0; round < rounds * 3; ++round)
        try {
          var got = File.ReadAllBytes(paths[file]);
          if (got.Length > length) {
            failures.Add($"'{paths[file]}' read back {got.Length} bytes, longer than any version ({length})");
            continue;
          }

          for (var at = 0; at + 16 <= got.Length; at += fingerprintBlock)
            if (owners.TryGetValue(Convert.ToHexString(got, at, 16), out var owner) && owner != file) {
              failures.Add($"'{paths[file]}' read back a block at offset {at} that belongs to conc{owner}.bin — "
                           + "one file's data was served for another");
              break;
            }
        } catch (IOException) {
          // legal under contention
        }
    }) { IsBackground = true }).ToArray();

    foreach (var thread in writers.Concat(readers))
      thread.Start();
    foreach (var thread in writers.Concat(readers))
      thread.Join(TimeSpan.FromMinutes(3)).Should().BeTrue("no filesystem operation may hang the driver");

    failures.Should().BeEmpty();

    // once the contention stops there is no excuse left: every file settles on ONE WHOLE version,
    // not merely on something of the right size
    for (var file = 0; file < files; ++file) {
      new FileInfo(paths[file]).Length.Should().Be(length, $"'{paths[file]}' must settle at its full size");
      var settled = File.ReadAllBytes(paths[file]);
      versions[file].Any(v => settled.AsSpan().SequenceEqual(v)).Should().BeTrue(
        $"'{paths[file]}' settled on {settled.Length} bytes that are not any version written to it."
        + $"{Environment.NewLine}{this._pool.DescribeMembers()}");
    }
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
