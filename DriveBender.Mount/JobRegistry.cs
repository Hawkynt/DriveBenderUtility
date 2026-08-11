namespace DivisonM.Mount;

/// <summary>
/// Long-running operations the management UI started (NFR-UI-LIVE).
///
/// A deep scan, a restore, scattering a member's data or purging a pool all run for as long as
/// the DATA takes — minutes to hours. Executed inline they held the browser's request and a
/// daemon thread open for the whole run: the tab showed a dead spinner, any proxy or client
/// timeout threw the result away, a page reload lost it, and nothing could stop it. Here every
/// such operation answers immediately with a ticket, reports its worker's own progress, survives
/// a reload, and can be cancelled.
/// </summary>
public sealed class JobRegistry(Func<DateTime>? clock = null) {

  /// <summary>Finished jobs are kept briefly so a reloaded page can still collect its result.</summary>
  public static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);

  public sealed class Job : IDisposable {

    private readonly CancellationTokenSource _cancellation = new();
    private string _progress = "";
    private object? _result;
    private long _finishedTicks; // written LAST — readers treat it as the completion fence

    internal Job(string id, string kind, string pool, DateTime startedUtc, bool cancellable) {
      this.Id = id;
      this.Kind = kind;
      this.Pool = pool;
      this.StartedUtc = startedUtc;
      this.Cancellable = cancellable;
    }

    public string Id { get; }
    public string Kind { get; }
    public string Pool { get; }
    public DateTime StartedUtc { get; }

    /// <summary>
    /// Whether stopping this operation is actually possible. Work that runs in a child process
    /// can be killed; work already inside a system installer, or handed to the pool's own mount
    /// process, cannot. Offering a Cancel button that silently does nothing is worse than not
    /// offering one, so the UI is told which kind this is.
    /// </summary>
    public bool Cancellable { get; }

    public CancellationToken Cancellation => this._cancellation.Token;
    public bool IsCancelling => this._cancellation.IsCancellationRequested;

    /// <summary>The worker's most recent line of output — what the UI shows instead of a bare spinner.</summary>
    public string Progress {
      get => Volatile.Read(ref this._progress);
      internal set => Volatile.Write(ref this._progress, value);
    }

    public bool IsFinished => Interlocked.Read(ref this._finishedTicks) != 0;

    public DateTime? FinishedUtc {
      get {
        var ticks = Interlocked.Read(ref this._finishedTicks);
        return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
      }
    }

    /// <summary>The operation's envelope, once it has finished.</summary>
    public object? Result => this.IsFinished ? Volatile.Read(ref this._result) : null;

    internal void Complete(object envelope, DateTime finishedUtc) {
      Volatile.Write(ref this._result, envelope);
      Interlocked.Exchange(ref this._finishedTicks, finishedUtc.Ticks); // fence: set after the result
    }

    internal void RequestCancel() {
      try {
        this._cancellation.Cancel();
      } catch (ObjectDisposedException) {
        // already pruned
      }
    }

    public void Dispose() => this._cancellation.Dispose();
  }

  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

  /// <summary>
  /// Starts <paramref name="work"/> on its own thread and returns the ticket immediately. The
  /// work is handed a cancellation token and a progress sink; a cancelled job reports as such
  /// rather than as a crash.
  /// </summary>
  public Job Start(string kind, string pool, Func<CancellationToken, Action<string>, object> work, bool cancellable = true) {
    this.Prune();
    var job = new Job(Guid.NewGuid().ToString("N"), kind, pool, this._clock(), cancellable);
    this._jobs[job.Id] = job;

    new Thread(() => {
      object envelope;
      try {
        envelope = work(job.Cancellation, progress => job.Progress = progress);
      } catch (OperationCanceledException) {
        envelope = new { ok = false, error = "cancelled" };
      } catch (Exception e) {
        envelope = new { ok = false, error = e.Message };
      }

      job.Complete(envelope, this._clock());
    }) { IsBackground = true, Name = $"job-{kind}" }.Start();

    return job;
  }

  public Job? Find(string id) => this._jobs.TryGetValue(id ?? "", out var job) ? job : null;

  /// <summary>The outcome of a cancel request — distinguishing "stopping" from "cannot be stopped".</summary>
  public enum CancelResult {
    Unknown,
    NotCancellable,
    AlreadyFinished,
    Cancelling,
  }

  /// <summary>Asks a job to stop. A job that cannot be stopped says so rather than pretending.</summary>
  public CancelResult Cancel(string id) {
    if (this.Find(id) is not { } job)
      return CancelResult.Unknown;
    if (job.IsFinished)
      return CancelResult.AlreadyFinished;
    if (!job.Cancellable)
      return CancelResult.NotCancellable;

    job.RequestCancel();
    return CancelResult.Cancelling;
  }

  public IReadOnlyList<Job> Running() => [.. this._jobs.Values.Where(job => !job.IsFinished)];

  /// <summary>Forgets jobs whose results nobody can still be waiting for.</summary>
  public void Prune() {
    var cutoff = this._clock() - Retention;
    foreach (var (id, job) in this._jobs)
      if (job.FinishedUtc is { } finished && finished < cutoff && this._jobs.TryRemove(id, out _))
        job.Dispose();
  }

}
