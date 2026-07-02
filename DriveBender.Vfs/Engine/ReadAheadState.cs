namespace DivisonM.Vfs.Engine;

/// <summary>
/// Per-handle sequential-access detector (CMP-RA, FR-RA): the prefetch window ramps up
/// from minWindow while reads stay sequential and collapses on random access; the engine
/// prefetches at most the window ahead, never past EOF.
/// </summary>
public sealed class ReadAheadState(long minWindowBytes, long maxWindowBytes, bool adaptive) {

  private long _expectedNextOffset = -1;

  public long CurrentWindowBytes { get; private set; } = minWindowBytes;

  /// <summary>
  /// Records a read and returns how many bytes to prefetch beyond its end
  /// (0 when the pattern is not sequential or read-ahead is effectively off).
  /// </summary>
  public long OnRead(long offset, int length) {
    var isFirstRead = this._expectedNextOffset < 0;
    var sequential = isFirstRead || offset == this._expectedNextOffset;
    this._expectedNextOffset = offset + length;

    if (!sequential) {
      // random access: collapse the window and stop prefetching
      this.CurrentWindowBytes = minWindowBytes;
      return 0;
    }

    // first read prefetches conservatively at the min window; sustained
    // sequential hits double the window up to the cap
    if (!isFirstRead && adaptive)
      this.CurrentWindowBytes = Math.Min(maxWindowBytes, this.CurrentWindowBytes * 2);

    return this.CurrentWindowBytes;
  }

}
