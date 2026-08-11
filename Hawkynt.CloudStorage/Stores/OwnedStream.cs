namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// A read-only stream that keeps an SDK response alive for exactly as long as the caller reads
/// it. Providers hand back a network stream whose lifetime belongs to a response wrapper —
/// returning the inner stream on its own either leaks the connection or lets the wrapper be
/// disposed mid-read. Handing out this instead makes the ownership transfer explicit: dispose
/// the stream, dispose the response.
/// </summary>
internal sealed class OwnedStream(Stream inner, IDisposable owner, long length) : Stream {

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => length;

  public override long Position {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

  public override int Read(Span<byte> buffer) => inner.Read(buffer);

  public override void Flush() {
  }

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  protected override void Dispose(bool disposing) {
    if (disposing) {
      inner.Dispose();
      owner.Dispose();
    }

    base.Dispose(disposing);
  }

}

/// <summary>An empty stream — what a read at or past the end of an object yields, as on a local file.</summary>
internal static class EmptyStream {
  public static Stream Instance => new MemoryStream([], false);
}

/// <summary>
/// Presents bytes <c>[offset, offset+count)</c> of a forward-only stream. Needed where a server
/// IGNORES a Range request and answers with the whole object: the bytes before the window are
/// consumed and discarded, and reading stops at its end, so the caller gets exactly the range it
/// asked for either way. Correct but not cheap — that difference is what <see cref="CloudCaps"/>
/// exists to tell callers about.
/// </summary>
internal sealed class WindowStream : Stream {

  private readonly Stream _inner;
  private readonly long _count;
  private long _delivered;

  public WindowStream(Stream inner, long offset, long count) {
    this._inner = inner;
    this._count = count;

    // skip forward to the window; a stream shorter than the offset simply yields nothing
    var buffer = new byte[Math.Min(offset, 64 * 1024)];
    var remaining = offset;
    while (remaining > 0) {
      var read = inner.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
      if (read <= 0)
        break;

      remaining -= read;
    }
  }

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => this._count;

  public override long Position {
    get => this._delivered;
    set => throw new NotSupportedException();
  }

  public override int Read(byte[] buffer, int offset, int count) {
    var allowed = (int)Math.Min(count, this._count - this._delivered);
    if (allowed <= 0)
      return 0;

    var read = this._inner.Read(buffer, offset, allowed);
    this._delivered += read;
    return read;
  }

  public override void Flush() {
  }

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  protected override void Dispose(bool disposing) {
    if (disposing)
      this._inner.Dispose();

    base.Dispose(disposing);
  }

}
