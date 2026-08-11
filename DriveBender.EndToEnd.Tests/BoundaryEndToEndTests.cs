using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The edges of the implementation, where data quietly goes missing: the exact sizes at which a
/// cache page, a read block or a buffer changes behaviour, files with holes in them, files that
/// shrink and grow again, and names the two operating systems disagree about.
///
/// None of these are exotic. They are what an ordinary application does — a database extending its
/// file, an installer truncating one, a build tool writing a name that differs only in case — and
/// every one of them is a place where an off-by-one costs the user real bytes.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class BoundaryEndToEndTests {

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>
  /// The sizes worth suspecting: nothing, one byte, and each side of the 4 KiB page, the 64 KiB
  /// buffer and the 1 MiB block the engine works in.
  /// </summary>
  private static IEnumerable<int> _BoundarySizes {
    get {
      yield return 0;
      yield return 1;
      foreach (var boundary in new[] { 4096, 65536, 1024 * 1024 })
        foreach (var delta in new[] { -1, 0, 1 })
          yield return boundary + delta;
    }
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Files sized exactly on and either side of the page, buffer and block boundaries round-trip byte for byte.")]
  public void Sizes_GivenFilesOnTheCacheAndBlockBoundaries_ThenEachRoundTripsExactly() {
    using var pool = MountedPool.Create();

    foreach (var size in _BoundarySizes) {
      var content = _Payload(size, size + 1);
      var path = pool.PathTo($"size{size}.bin");
      File.WriteAllBytes(path, content);

      new FileInfo(path).Length.Should().Be(size,
        $"a {size}-byte file must be reported as {size} bytes.{Environment.NewLine}{pool.MountLog}");
      File.ReadAllBytes(path).Should().Equal(content, $"a {size}-byte file must read back exactly as written");
    }

    // and again after a remount, because a length that only lives in memory is not a length
    pool.Remount();
    foreach (var size in _BoundarySizes)
      File.ReadAllBytes(pool.PathTo($"size{size}.bin")).Should().Equal(_Payload(size, size + 1),
        $"a {size}-byte file must still be intact after a remount");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Reads that straddle a page or block boundary return the right bytes, whatever offset and length they use.")]
  public void Reads_GivenTheyStraddleBlockBoundaries_ThenTheyReturnTheRightBytes() {
    using var pool = MountedPool.Create();
    const int size = 3 * 1024 * 1024 + 777;
    var content = _Payload(size, 31);
    var path = pool.PathTo("straddle.bin");
    File.WriteAllBytes(path, content);
    pool.Remount(); // read it back cold, so the cache has to fetch rather than remember

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    foreach (var boundary in new[] { 4096, 65536, 1024 * 1024, 2 * 1024 * 1024 })
      foreach (var (offset, length) in new[] {
                 (boundary - 1, 2), (boundary - 1, 1), (boundary, 1),
                 (boundary - 3, 7), (boundary - 4096, 8192),
               }) {
        if (offset < 0 || offset + length > size)
          continue;

        var buffer = new byte[length];
        stream.Position = offset;
        var read = stream.Read(buffer, 0, length);

        read.Should().Be(length, $"a {length}-byte read at {offset} is entirely inside a {size}-byte file");
        buffer.Should().Equal(content.Skip(offset).Take(length).ToArray(),
          $"a read of {length} bytes at offset {offset} crosses a {boundary}-byte boundary and came back wrong."
          + $"{Environment.NewLine}{pool.MountLog}");
      }
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file shrunk and grown again reads as zeroes in the re-exposed region, never the content that used to be there.")]
  public void Truncate_GivenAFileIsShrunkThenGrown_ThenTheOldContentDoesNotResurface() {
    using var pool = MountedPool.Create();
    const int size = 1024 * 1024;
    var secret = _Payload(size, 66);
    var path = pool.PathTo("truncated.bin");

    File.WriteAllBytes(path, secret);

    using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
      stream.SetLength(4096);   // throw the tail away...
      stream.SetLength(size);   // ...and ask for the space back
    }

    var got = File.ReadAllBytes(path);
    got.Length.Should().Be(size, "the file was grown back to its original length");

    // Everything past the truncation point is a hole the user never wrote. It must read as zeroes.
    // Handing back what used to be there leaks whatever the file previously held — and the caller
    // has no way to tell the stale bytes from real ones, so it silently persists them onwards.
    var stale = Enumerable.Range(4096, size - 4096).Where(i => got[i] != 0).Take(8).ToArray();
    stale.Should().BeEmpty(
      $"the region beyond a truncation must read as zeroes, but offsets [{string.Join(", ", stale)}] "
      + $"still hold the discarded content.{Environment.NewLine}{pool.MountLog}");

    got.Take(4096).Should().Equal(secret.Take(4096), "the part that was kept must be untouched");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Writing far past the end of a file leaves a hole that reads as zeroes, and the bytes written land at the right offset.")]
  public void Sparse_GivenAWriteFarBeyondTheEnd_ThenTheHoleReadsAsZeroes() {
    using var pool = MountedPool.Create();
    const long offset = 5 * 1024 * 1024 + 13;
    var marker = _Payload(64, 77);
    var path = pool.PathTo("sparse.bin");

    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
      stream.Write([1, 2, 3, 4], 0, 4);
      stream.Position = offset;
      stream.Write(marker, 0, marker.Length);
    }

    pool.Remount(); // the hole must be a property of the stored file, not of the write path

    var got = File.ReadAllBytes(path);
    ((long)got.Length).Should().Be(offset + marker.Length,
      $"the file ends where the last write ended.{Environment.NewLine}{pool.MountLog}");
    got.Take(4).Should().Equal([1, 2, 3, 4], "the bytes before the hole must survive it");
    got.Skip((int)offset).Take(marker.Length).Should().Equal(marker,
      "the bytes past the hole must land at the offset they were written to, not wherever the data happened to be packed");

    var polluted = Enumerable.Range(4, (int)offset - 4).Where(i => got[i] != 0).Take(8).ToArray();
    polluted.Should().BeEmpty($"a hole must read as zeroes, but offsets [{string.Join(", ", polluted)}] hold something else");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Two names differing only in case: on Windows they are one file, on Linux two, and in neither case does content go missing.")]
  public void Names_GivenTwoPathsDifferingOnlyInCase_ThenNoContentIsLost() {
    using var pool = MountedPool.Create();
    var lower = _Payload(4096, 101);
    var upper = _Payload(4096, 102);

    File.WriteAllBytes(pool.PathTo("Report.txt"), lower);
    File.WriteAllBytes(pool.PathTo("REPORT.TXT"), upper);

    var listed = Directory.GetFiles(pool.MountPath).Select(Path.GetFileName).ToArray();

    if (OperatingSystem.IsWindows()) {
      // Windows treats the two as one name, so the second write replaces the first. What must not
      // happen is the pool keeping two physical files and serving whichever it finds first: the
      // user would see their content change depending on nothing they can observe, and one of the
      // two copies is unreachable storage that never gets freed.
      listed.Should().HaveCount(1, $"Windows paths are case-insensitive, so this is one file."
                                   + $"{Environment.NewLine}{pool.DescribeMembers()}");
      File.ReadAllBytes(pool.PathTo("Report.txt")).Should().Equal(upper, "the later write wins, whichever casing addressed it");
      File.ReadAllBytes(pool.PathTo("REPORT.TXT")).Should().Equal(upper, "both spellings must reach the same file");
    } else {
      // POSIX names are distinct, so both files exist and neither may have clobbered the other
      listed.Should().HaveCount(2, $"POSIX paths are case-sensitive, so these are two files."
                                   + $"{Environment.NewLine}{pool.DescribeMembers()}");
      File.ReadAllBytes(pool.PathTo("Report.txt")).Should().Equal(lower, "the lower-cased file kept its own content");
      File.ReadAllBytes(pool.PathTo("REPORT.TXT")).Should().Equal(upper, "the upper-cased file kept its own content");
    }
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Many threads creating the same new path at once: one wins, no exception escapes unexplained, and the file is whole.")]
  public void Create_GivenManyThreadsRaceForOnePath_ThenExactlyOneFileExistsAndItIsWhole() {
    using var pool = MountedPool.Create();
    const int size = 256 * 1024;
    var path = pool.PathTo("contested.bin");
    var contents = Enumerable.Range(0, 8).Select(t => _Payload(size, 200 + t)).ToArray();
    var unexpected = new System.Collections.Concurrent.ConcurrentBag<string>();

    var racers = Enumerable.Range(0, 8).Select(thread => new Thread(() => {
      try {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
        stream.Write(contents[thread], 0, size);
      } catch (IOException) {
        // losing the race is a legitimate outcome — another thread holds the file
      } catch (UnauthorizedAccessException) {
        // as is being refused sharing
      } catch (Exception e) {
        unexpected.Add($"thread {thread}: {e.GetType().Name}: {e.Message}");
      }
    }) { IsBackground = true }).ToArray();

    foreach (var racer in racers)
      racer.Start();
    foreach (var racer in racers)
      racer.Join(TimeSpan.FromMinutes(1)).Should().BeTrue("a contested create must not hang");

    unexpected.Should().BeEmpty($"a race for one path must fail in ways a caller can handle."
                                + $"{Environment.NewLine}{pool.MountLog}");

    Directory.GetFiles(pool.MountPath).Should().HaveCount(1, "eight threads raced for ONE path");
    var got = File.ReadAllBytes(path);
    got.Length.Should().Be(size, $"the winner wrote {size} bytes.{Environment.NewLine}{pool.DescribeMembers()}");

    // whichever thread won, the file must be that thread's content and not a blend of several
    contents.Any(candidate => candidate.AsSpan().SequenceEqual(got)).Should().BeTrue(
      $"the surviving file must be exactly one writer's content, not a mixture of the racers'."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A deep tree with long paths and awkward but legal names survives a remount with its content addressable.")]
  public void Names_GivenLongPathsAndAwkwardNames_ThenTheyRemainAddressableAfterARemount() {
    using var pool = MountedPool.Create();

    // a path long enough to pass the classic 260-character limit once the member root is prefixed
    var deep = string.Join(Path.DirectorySeparatorChar, Enumerable.Range(0, 12).Select(level => $"level{level}-{new string('n', 14)}"));
    Directory.CreateDirectory(pool.PathTo(deep));

    var names = new[] { "plain.txt", "with space.txt", "dotted.name.v2.txt", "ümläut.txt", "+plus&amp.txt", ".hidden" };
    var written = new Dictionary<string, byte[]>();
    foreach (var (name, index) in names.Select((n, i) => (n, i))) {
      var relative = Path.Combine(deep, name);
      var content = _Payload(8192, 300 + index);
      File.WriteAllBytes(pool.PathTo(relative), content);
      written[relative] = content;
    }

    pool.Remount();

    foreach (var (relative, content) in written) {
      File.Exists(pool.PathTo(relative)).Should().BeTrue(
        $"'{relative}' was stored, so it must still be addressable by the same name."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
      File.ReadAllBytes(pool.PathTo(relative)).Should().Equal(content, $"'{relative}' must read back exactly as written");
    }

    // and the directory listing has to agree with what is reachable — a file that opens but does
    // not enumerate is invisible to every backup tool the user owns
    Directory.GetFiles(pool.PathTo(deep)).Select(Path.GetFileName).Should().BeEquivalentTo(names,
      "everything reachable by name must also be listed");
  }

}
