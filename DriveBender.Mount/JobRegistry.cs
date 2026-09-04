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

  /// <summary>
  /// How far a job has got, in items rather than in prose.
  ///
  /// The only thing a job could report used to be its worker's most recent line of output. That
  /// cannot be drawn as a bar, cannot say whether a run is a tenth or nine tenths through, and
  /// cannot tell "still working" from "wedged on one enormous file" — which for an operation that
  /// legitimately takes hours is most of what the person watching it wants to know.
  /// <see cref="Total"/> is 0 when the work genuinely cannot count itself yet; reported honestly
  /// rather than guessed, because a denominator that moves is worse than none.
  /// </summary>
  public readonly record struct JobProgress(long Completed, long Total, string Item, string Text) {
    /// <summary>A plain line of output — a worker's stdout, a phase announcement.</summary>
    public static JobProgress Line(string text) => new(0, 0, "", text);
  }

  public sealed class Job : IDisposable {

    private readonly CancellationTokenSource _cancellation = new();
    private string _progress = "";
    private object? _result;
    private long _finishedTicks; // written LAST — readers treat it as the completion fence

    internal Job(string id, string kind, string pool, DateTime startedUtc, bool cancellable, string subject = "") {
      this.Id = id;
      this.Kind = kind;
      this.Pool = pool;
      this.StartedUtc = startedUtc;
      this.Cancellable = cancellable;
      this.Subject = subject;
    }

    public string Id { get; }
    public string Kind { get; }
    public string Pool { get; }

    /// <summary>
    /// What inside the pool this job is acting ON — a member id, for the operations that target
    /// one. The dashboard uses it to mark that storage as being evacuated while the work runs, so
    /// a disk having its data moved off it does not look like an ordinary healthy member.
    /// </summary>
    public string Subject { get; }
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

    private long _completed;
    private long _total;
    private string _item = "";

    /// <summary>Items finished, and out of how many; <see cref="Total"/> 0 means "not countable yet".</summary>
    public long Completed => Interlocked.Read(ref this._completed);

    public long Total => Interlocked.Read(ref this._total);

    /// <summary>What the job is working on right now — a pool-relative path, or a phase name.</summary>
    public string Item => Volatile.Read(ref this._item);

    internal void Advance(JobProgress step) {
      Interlocked.Exchange(ref this._completed, step.Completed);
      Interlocked.Exchange(ref this._total, step.Total);
      Volatile.Write(ref this._item, step.Item);
      Volatile.Write(ref this._progress, step.Text);
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
  public Job Start(string kind, string pool, Func<CancellationToken, Action<JobProgress>, object> work, bool cancellable = true, string subject = "") {
    this.Prune();
    var job = new Job(Guid.NewGuid().ToString("N"), kind, pool, this._clock(), cancellable, subject);
    this._jobs[job.Id] = job;

    new Thread(() => {
      object envelope;
      try {
        envelope = work(job.Cancellation, job.Advance);
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
