namespace DivisonM.Vfs.Caching;

/// <summary>
/// A statistics counter bumped from many threads at once without them fighting over it: each CPU
/// adds to its own cache line and a read sums them. One shared field incremented on every cache hit
/// bounces that line between every core doing reads, which is contention even without a lock.
/// A read concurrent with increments is a snapshot, as any statistic read under load is.
/// </summary>
internal sealed class StripedCounter {

  /// <summary>Longs per stripe: 64 bytes apart, so two stripes never share a cache line.</summary>
  private const int _SPACING = 8;

  private readonly long[] _cells;
  private readonly int _mask;

  public StripedCounter() {
    var stripes = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(Environment.ProcessorCount, 1, 64));
    this._mask = stripes - 1;
    this._cells = new long[stripes * _SPACING];
  }

  public void Increment() => Interlocked.Increment(ref this._cells[(Thread.GetCurrentProcessorId() & this._mask) * _SPACING]);

  public long Value {
    get {
      long sum = 0;
      for (var index = 0; index < this._cells.Length; index += _SPACING)
        sum += Volatile.Read(ref this._cells[index]);

      return sum;
    }
  }

  public void Reset() {
    for (var index = 0; index < this._cells.Length; index += _SPACING)
      Interlocked.Exchange(ref this._cells[index], 0);
  }

}
