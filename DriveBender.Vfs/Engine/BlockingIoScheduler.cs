namespace DivisonM.Vfs.Engine;

/// <summary>
/// A bounded pool of DEDICATED threads for I/O that blocks the caller (CMP-VFS, NFR-UI-LIVE).
///
/// The engine fans block loads out with <c>Parallel.ForEach</c> so several members serve a read
/// at once. On the shared thread pool that is fine for local disks, whose waits are short and
/// mostly kernel-side — but every remote provider is sync-over-async, so one in-flight cloud read
/// parks a thread for the whole WAN round trip. The thread pool grows by roughly one thread per
/// second once saturated, so a burst of cloud reads starves everything else in the process for
/// seconds at a time: other pools' I/O, the background scheduler, and the management daemon whose
/// whole purpose is to stay responsive while this happens.
///
/// These threads are that blast radius made explicit and finite. They are created ON DEMAND and
/// retire after an idle period, so a pool with no remote member never pays for them, and a
/// process that stops using one gets the memory back.
/// </summary>
public sealed class BlockingIoScheduler : TaskScheduler, IDisposable {

  private static readonly TimeSpan _IDLE_RETIREMENT = TimeSpan.FromSeconds(30);

  private readonly int _maxThreads;
  private readonly System.Collections.Concurrent.BlockingCollection<Task> _queue = new();
  private readonly Lock _lock = new();
  private int _threads;
  private int _idle;
  private bool _disposed;

  [ThreadStatic] private static bool _isWorker;

  public BlockingIoScheduler(int maxThreads) => this._maxThreads = Math.Max(1, maxThreads);

  /// <summary>
  /// The shared instance. Sized to keep a handful of remote requests in flight without letting a
  /// stalled remote consume an unbounded number of threads: the useful concurrency for whole-object
  /// remotes is small (most stores are serialized to one connection anyway), and the point of the
  /// cap is that a hung provider cannot grow past it.
  /// </summary>
  public static BlockingIoScheduler Shared { get; } = new(Math.Clamp(Environment.ProcessorCount, 4, 16));

  public override int MaximumConcurrencyLevel => this._maxThreads;

  /// <summary>Threads currently alive — zero until blocking work actually arrives (test/observability hook).</summary>
  public int LiveThreads {
    get {
      lock (this._lock)
        return this._threads;
    }
  }

  protected override void QueueTask(Task task) {
    if (this._disposed)
      throw new ObjectDisposedException(nameof(BlockingIoScheduler));

    this._queue.Add(task);
    this._EnsureWorker();
  }

  /// <summary>
  /// Inlining is allowed only ON a worker thread. Letting an arbitrary caller inline would put
  /// the blocking call back on the thread this scheduler exists to protect — and <c>Parallel</c>
  /// asks to inline the moment it joins.
  /// </summary>
  protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    => _isWorker && !taskWasPreviouslyQueued && this.TryExecuteTask(task);

  protected override IEnumerable<Task> GetScheduledTasks() => [.. this._queue];

  private void _EnsureWorker() {
    lock (this._lock) {
      // an idle worker will pick the item up; only grow when every thread is busy
      if (this._disposed || this._idle > 0 || this._threads >= this._maxThreads)
        return;

      ++this._threads;
    }

    new Thread(this._Work) { IsBackground = true, Name = "db-blocking-io" }.Start();
  }

  private void _Work() {
    _isWorker = true;
    try {
      while (true) {
        lock (this._lock)
          ++this._idle;

        Task? task;
        try {
          if (!this._queue.TryTake(out task, (int)_IDLE_RETIREMENT.TotalMilliseconds))
            task = null;
        } catch (Exception) {
          task = null; // the collection was completed by Dispose
        } finally {
          lock (this._lock)
            --this._idle;
        }

        if (task == null)
          return; // idle too long: retire and give the stack back

        this.TryExecuteTask(task);
      }
    } finally {
      lock (this._lock)
        --this._threads;
    }
  }

  public void Dispose() {
    lock (this._lock) {
      if (this._disposed)
        return;

      this._disposed = true;
    }

    this._queue.CompleteAdding();
  }

}
