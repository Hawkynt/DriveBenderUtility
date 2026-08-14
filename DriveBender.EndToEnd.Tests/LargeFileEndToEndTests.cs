using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Files past the 2 GiB mark (SAFE-BIGFILE), which is where 32-bit arithmetic stops being an
/// abstract worry: a single <c>int</c> anywhere on the offset or length path turns a valid read
/// into a negative index, a wrapped length, or silently truncated data. Disk images, VM disks,
/// database files and video masters all live up here, and they are exactly the files a user can
/// least afford to have quietly corrupted.
///
/// Nothing here materialises the file in memory, on either side. The content is generated from the
/// byte's own offset, so any position can be verified without ever holding more than a window —
/// and a test that allocated a 2.5 GiB array would be measuring the test host rather than the pool
/// (a <c>byte[]</c> cannot even reach that size by default).
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("Performance")]
[NonParallelizable]
public class LargeFileEndToEndTests {

  private const long _SIZE = 2L * 1024 * 1024 * 1024 + 64 * 1024 * 1024; // 2 GiB + 64 MiB
  private const int _CHUNK = 8 * 1024 * 1024;
  private const long _TWO_GIB = 2L * 1024 * 1024 * 1024;

  /// <summary>The byte that belongs at an absolute offset — position-derivable, so any window verifies alone.</summary>
  private static byte _ByteAt(long offset) => (byte)((offset / 4096 * 167 + offset % 251) & 0xFF);

  private static void _FillChunk(byte[] buffer, long startOffset, int length) {
    for (var i = 0; i < length; ++i)
      buffer[i] = _ByteAt(startOffset + i);
  }

  /// <summary>A pool whose page cache is far smaller than the file under test.</summary>
  private const string _SMALL_CACHE =
    """{ "caches": { "global": { "size": "256MiB" } } }""";

  private static MountedPool _pool = null!;
  private static long _writtenBytesPerSecond;

  /// <summary>
  /// One 2 GiB+ file written once and shared by every check here. Writing it per test would cost
  /// minutes of disk each time and prove nothing extra — what differs between the tests is how the
  /// file is READ, which is where the 32-bit hazards live.
  /// </summary>
  [OneTimeSetUp]
  public void WriteTheLargeFile() {
    var free = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace;
    if (free < _SIZE * 3)
      Assert.Ignore($"needs about {_SIZE * 3 / (1024 * 1024 * 1024)} GiB free, has {free / (1024 * 1024 * 1024)} GiB");

    // A DELIBERATELY SMALL cache. The default global cache is 4 GiB, so a 2 GiB file fits in it
    // entirely and the mount's memory would sit near the file size for a perfectly good reason —
    // measuring that would prove nothing about streaming. Capped at 256 MiB, memory has to track
    // the BUDGET rather than the file, which is the actual SAFE-BIGFILE property.
    _pool = MountedPool.Create(poolDefaults: _SMALL_CACHE);
    var buffer = new byte[_CHUNK];
    var clock = Stopwatch.StartNew();

    using (var stream = new FileStream(_pool.PathTo("huge.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) {
      for (var written = 0L; written < _SIZE; written += _CHUNK) {
        var length = (int)Math.Min(_CHUNK, _SIZE - written);
        _FillChunk(buffer, written, length);
        stream.Write(buffer, 0, length);
      }

      stream.Flush();
    }

    clock.Stop();
    _writtenBytesPerSecond = (long)(_SIZE / Math.Max(0.001, clock.Elapsed.TotalSeconds));
    TestContext.Out.WriteLine(
      $"wrote {_SIZE / (1024 * 1024)} MiB in {clock.Elapsed.TotalSeconds:F1}s "
      + $"({_writtenBytesPerSecond / (1024 * 1024)} MiB/s)");
  }

  [OneTimeTearDown]
  public void Cleanup() => _pool?.Dispose();

  /// <summary>Reads one window and returns the first offset whose byte is wrong, or null.</summary>
  private static long? _FirstBadOffset(FileStream stream, long offset, int length) {
    var buffer = new byte[length];
    stream.Position = offset;

    var filled = 0;
    while (filled < length) {
      var read = stream.Read(buffer, filled, length - filled);
      if (read <= 0)
        return offset + filled; // short read inside the file is itself the defect

      filled += read;
    }

    for (var i = 0; i < length; ++i)
      if (buffer[i] != _ByteAt(offset + i))
        return offset + i;

    return null;
  }


  [Test]
  [Category("Performance")]
  [Description("Writing a file past 2 GiB sustains a sensible rate and does not slow down as the file grows.")]
  public void LargeFile_WhenWritten_ThenThroughputHoldsAndDoesNotDegradeWithSize() {
    // The write rate is measured on the shared 2 GiB+ file in OneTimeSetUp; this reports it and
    // guards the floor. Reads had a number and writes did not, which is the half of the workload
    // that actually has to durably land.
    TestContext.Out.WriteLine($"write: {_writtenBytesPerSecond / (1024 * 1024)} MiB/s for {_SIZE / (1024 * 1024)} MiB");

    _writtenBytesPerSecond.Should().BeGreaterThan(10L * 1024 * 1024,
      $"writing a large file must not collapse to a crawl (measured {_writtenBytesPerSecond / (1024 * 1024)} MiB/s)");

    // The shape that matters more than the absolute number: a per-write cost that grows with the
    // file — rehashing it, rescanning it, copying it — shows up as the last stretch being far
    // slower than the first. Two equal runs on the SAME file, one starting past the 2 GiB mark.
    var buffer = new byte[_CHUNK];
    var span = 128 * 1024 * 1024;

    var early = _TimeWrite(0, span, buffer);
    var late = _TimeWrite(_SIZE - span, span, buffer);
    TestContext.Out.WriteLine($"first {span / (1024 * 1024)} MiB: {early.TotalSeconds:F2}s, "
      + $"last {span / (1024 * 1024)} MiB: {late.TotalSeconds:F2}s");

    late.TotalSeconds.Should().BeLessThan(Math.Max(2.0, early.TotalSeconds * 6),
      $"writing near the end of a {_SIZE / (1024 * 1024)} MiB file took {late.TotalSeconds:F2}s against "
      + $"{early.TotalSeconds:F2}s near the start — a per-write cost that scales with the file would "
      + $"look exactly like this, and it is what rehashing on every write would produce");
  }

  private static TimeSpan _TimeWrite(long offset, int length, byte[] buffer) {
    using var stream = new FileStream(_pool.PathTo("huge.bin"), FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20);
    stream.Position = offset;
    var clock = Stopwatch.StartNew();
    for (var written = 0; written < length; written += _CHUNK) {
      var take = Math.Min(_CHUNK, length - written);
      _FillChunk(buffer, offset + written, take);
      stream.Write(buffer, 0, take);
    }

    stream.Flush();
    clock.Stop();
    return clock.Elapsed;
  }

  [Test]
  [Category("HappyPath")]
  [Description("A file larger than 2 GiB reports its true length rather than a 32-bit wrapped one.")]
  public void LargeFile_GivenItExceedsTwoGiB_ThenItsLengthIsReportedInFull() {
    var info = new FileInfo(_pool.PathTo("huge.bin"));

    info.Length.Should().Be(_SIZE,
      $"a length that wrapped would come back as {unchecked((int)_SIZE)} or negative."
      + $"{Environment.NewLine}{_pool.MountLog}");

    // the directory listing must agree with the stat — they are separate code paths
    new DirectoryInfo(_pool.MountPath).GetFiles("huge.bin").Single().Length.Should().Be(_SIZE,
      "the enumeration path must report the same length as the stat path");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Reads on both sides of the 2 GiB and 4 GiB-relevant boundaries return the right bytes.")]
  public void LargeFile_GivenReadsAroundTheThirtyTwoBitBoundaries_ThenEveryByteIsCorrect() {
    using var stream = new FileStream(_pool.PathTo("huge.bin"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

    // int.MaxValue and 2 GiB are where a signed 32-bit offset flips negative; the ends catch a
    // length that wrapped, and the odd offsets catch arithmetic that only works when aligned
    foreach (var offset in new[] {
               0L,
               int.MaxValue - 4096L,
               int.MaxValue - 1L,
               (long)int.MaxValue,
               int.MaxValue + 1L,
               _TWO_GIB - 4096L,
               _TWO_GIB - 1L,
               _TWO_GIB,
               _TWO_GIB + 1L,
               _TWO_GIB + 4097L,
               _SIZE - 64 * 1024L,
             }) {
      var length = (int)Math.Min(64 * 1024, _SIZE - offset);
      _FirstBadOffset(stream, offset, length).Should().BeNull(
        $"a {length}-byte read at offset {offset} ({offset / 1024.0 / 1024:F1} MiB) came back wrong."
        + $"{Environment.NewLine}{_pool.MountLog}");
    }
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Appending to a file that is already past 2 GiB puts the bytes at the true end, not at a wrapped offset.")]
  public void LargeFile_WhenAppendedTo_ThenTheNewBytesLandPastTheOldEnd() {
    var path = _pool.PathTo("huge.bin");
    var marker = new byte[4096];
    _FillChunk(marker, _SIZE, marker.Length);

    using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
      append.Write(marker, 0, marker.Length);

    new FileInfo(path).Length.Should().Be(_SIZE + marker.Length, "the append must extend the real end of the file");

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    _FirstBadOffset(stream, _SIZE, marker.Length).Should().BeNull("the appended bytes must be readable where they were written");

    // and the byte immediately BEFORE the append is untouched — an append that wrapped would have
    // overwritten somewhere near the start instead
    _FirstBadOffset(stream, 0, 64 * 1024).Should().BeNull("appending past 2 GiB must not have written over the beginning");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Writing into the middle of a file past 2 GiB changes only that region.")]
  public void LargeFile_WhenWrittenInThePastTwoGiBRegion_ThenOnlyThatRegionChanges() {
    var path = _pool.PathTo("huge.bin");
    var at = _TWO_GIB + 32 * 1024 * 1024;
    var patch = new byte[64 * 1024];
    Array.Fill(patch, (byte)0xA5);

    using (var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20)) {
      write.Position = at;
      write.Write(patch, 0, patch.Length);
      write.Flush();
    }

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    var readBack = new byte[patch.Length];
    stream.Position = at;
    var filled = 0;
    while (filled < readBack.Length) {
      var read = stream.Read(readBack, filled, readBack.Length - filled);
      if (read <= 0)
        break;

      filled += read;
    }

    readBack.Should().Equal(patch, "a positional write past 2 GiB must land exactly where it was addressed");

    // the neighbours are the real assertion: a wrapped offset would have written near offset 0
    _FirstBadOffset(stream, 0, 64 * 1024).Should().BeNull("the start of the file must be untouched");
    _FirstBadOffset(stream, at - 64 * 1024, 64 * 1024).Should().BeNull("the region before the patch must be untouched");
    _FirstBadOffset(stream, at + patch.Length, 64 * 1024).Should().BeNull("the region after the patch must be untouched");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file past 2 GiB streams rather than being materialised: the mount's memory stays far below the file size, and throughput stays reasonable.")]
  public void LargeFile_WhenStreamedEndToEnd_ThenMemoryStaysBoundedAndThroughputHolds() {
    var buffer = new byte[_CHUNK];
    var clock = Stopwatch.StartNew();
    var total = 0L;

    using (var stream = new FileStream(_pool.PathTo("huge.bin"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) {
      while (true) {
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0)
          break;

        total += read;
      }
    }

    clock.Stop();
    var readBytesPerSecond = (long)(total / Math.Max(0.001, clock.Elapsed.TotalSeconds));
    TestContext.Out.WriteLine(
      $"read {total / (1024 * 1024)} MiB in {clock.Elapsed.TotalSeconds:F1}s ({readBytesPerSecond / (1024 * 1024)} MiB/s); "
      + $"mount peak working set {_pool.PeakWorkingSetBytes / (1024 * 1024)} MiB");

    total.Should().BeGreaterThanOrEqualTo(_SIZE, "the whole file must be readable from end to end");

    // The point of SAFE-BIGFILE: memory tracks the CACHE BUDGET, not the file size. With a 256 MiB
    // cache, a mount that streams stays around that plus its own overhead however big the file is,
    // while one that materialises climbs with the file. The ceiling is deliberately loose — this
    // must fail on materialisation, not on GC timing or cache tuning.
    _pool.PeakWorkingSetBytes.Should().BeLessThan(_SIZE / 2,
      $"with a 256 MiB cache the mount must STREAM a {_SIZE / (1024 * 1024)} MiB file rather than hold it."
      + $"{Environment.NewLine}{_pool.MountLog}");

    // A floor low enough that only a collapse trips it — this is a guard against an accidental
    // per-byte or per-block round trip, not a benchmark, and CI hardware varies wildly.
    readBytesPerSecond.Should().BeGreaterThan(10L * 1024 * 1024,
      $"streaming a large file must not collapse to a crawl (read {readBytesPerSecond / (1024 * 1024)} MiB/s, "
      + $"written at {_writtenBytesPerSecond / (1024 * 1024)} MiB/s)");
  }

}
