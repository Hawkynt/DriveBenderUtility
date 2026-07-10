using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// SAFE-BIGFILE: files far larger than 2 GiB / 4 GiB are stored, copied, hashed and read
/// without ever being materialised in RAM and without any 32-bit size overflow. The engine's
/// copy/publish/scrub primitives all stream through a fixed buffer, and the local backend's
/// positional I/O has no size ceiling.
/// </summary>
[TestFixture]
[Category("Unit")]
public class LargeFileStreamingTests {

  private const long _FiveGiB = 5L * 1024 * 1024 * 1024;

  /// <summary>A read-only stream of <paramref name="length"/> zero bytes that never allocates the whole payload — models a multi-GB file cheaply.</summary>
  private sealed class ZeroStream(long length) : Stream {
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => this._position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) {
      var remaining = length - this._position;
      if (remaining <= 0)
        return 0;

      var n = (int)Math.Min(count, remaining);
      Array.Clear(buffer, offset, n);
      this._position += n;
      return n;
    }
  }

  /// <summary>A counting sink — discards bytes, tracks the length, so a huge copy needs no disk or RAM.</summary>
  private sealed class CountingStream : Stream {
    public long Written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this.Written;
    public override long Position { get => this.Written; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this.Written += count;
  }

  [Test]
  [Category("EdgeCase")]
  public void CopyCounted_GivenFiveGiBSource_WhenCopied_ThenReturnsFullCountWithoutOverflow() {
    using var source = new ZeroStream(_FiveGiB);
    var sink = new CountingStream();

    var copied = WholeFilePublisher.CopyCounted(source, sink);

    copied.Should().Be(_FiveGiB, "the streamed byte count is a long — no 32-bit wrap past 4 GiB");
    sink.Written.Should().Be(_FiveGiB);
  }

  [Test]
  [Category("EdgeCase")]
  public void HashOf_GivenFiveGiBStream_WhenHashed_ThenCompletesWithBoundedMemory() {
    using var source = new ZeroStream(_FiveGiB);

    var before = GC.GetTotalAllocatedBytes();
    var hash = ChecksumDatabase.HashOf(source);
    var allocated = GC.GetTotalAllocatedBytes() - before;

    hash.Should().NotBeNullOrEmpty();
    allocated.Should().BeLessThan(64L * 1024 * 1024, "hashing a 5 GiB stream must not allocate anywhere near the file size");
  }

  [Test]
  [Category("EdgeCase")]
  [Platform(Exclude = "MacOsX", Reason = "sparse-file behaviour differs")]
  public void LocalVolumeIO_GivenFiveGiBSparseFile_WhenWrittenAndRead_ThenSizeAndBytesSurviveBeyond4GiB() {
    var root = Path.Combine(Path.GetTempPath(), "dbig" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
      var io = new LocalVolumeIO(Guid.NewGuid(), "big", root, "PHYS-BIG");
      const long markerOffset = _FiveGiB - 8;
      var marker = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

      using (var write = io.OpenWrite("huge.bin", false, true)) {
        write.SetLength(_FiveGiB);      // sparse — instant, no 5 GiB of disk churn
        write.Seek(markerOffset, SeekOrigin.Begin);
        write.Write(marker, 0, marker.Length);
        write.Flush();                  // real FlushFileBuffers durability barrier
      }

      // metadata reports the full 64-bit length, never a wrapped int
      io.Stat("huge.bin", false)!.Value.Length.Should().Be(_FiveGiB, "a >4 GiB file reports its true length");

      // the bytes at a >4 GiB offset read back correctly (positional I/O, no seek overflow)
      using var read = io.OpenRead("huge.bin", false);
      read.Seek(markerOffset, SeekOrigin.Begin);
      var buffer = new byte[8];
      var total = 0;
      while (total < 8) {
        var got = read.Read(buffer, total, 8 - total);
        if (got == 0)
          break;
        total += got;
      }

      buffer.Should().Equal(marker, "bytes past the 4 GiB boundary survive a write/flush/read round-trip");
    } finally {
      try { Directory.Delete(root, true); } catch { /* best effort */ }
    }
  }
}
