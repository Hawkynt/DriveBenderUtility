using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisonM.Vfs.Engine;

public enum JournalOp {
  Create,
  Write,
  Truncate,
  Delete,
  Rename,
  MakeDir,
  RemoveDir,
  ShadowCreate,
  ShadowDelete,
  Drain,
  Rebalance,
  TrashMove,

  /// <summary>A snapshot's index was written across the members.</summary>
  SnapshotTake,

  /// <summary>A snapshot was forgotten and the versions only it pinned were released.</summary>
  SnapshotDelete,

  /// <summary>A pinned file was renamed into the snapshot store so the content survives what happens to its name.</summary>
  SnapshotAside,
}

/// <summary>One journal line: an intent (Completed=false) or its completion marker.</summary>
public sealed record JournalRecord {
  [JsonPropertyName("seq")] public required long Sequence { get; init; }

  // ALWAYS serialized: Create is enum value 0, and the compact writer omits defaults — a
  // required property missing on read made every Create record silently unparseable
  [JsonPropertyName("op")] [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required JournalOp Op { get; init; }
  [JsonPropertyName("path")] public string? Path { get; init; }
  [JsonPropertyName("target")] public string? TargetPath { get; init; }
  [JsonPropertyName("offset")] public long Offset { get; init; }
  [JsonPropertyName("length")] public long Length { get; init; }
  [JsonPropertyName("member")] public Guid MemberId { get; init; }

  // Which of the member's two namespaces the named copy lives in. A member can hold both a
  // primary and a shadow of one file, so the member alone does not say which copy a write went to.
  [JsonPropertyName("shadow")] public bool Shadow { get; init; }

  // When the intent was logged. Recovery uses it to tell an interrupted operation from one that
  // finished long ago and came back on a restored disk: content NEWER than the intent cannot be the
  // content the intent was about. Absent (DateTime.MinValue) on records written before this field
  // existed and on anything forged, which reads as "cannot prove this is recent" — and recovery
  // then declines to destroy anything, which is the safe direction to be wrong in.
  [JsonPropertyName("utc")] public DateTime LoggedUtc { get; init; }
  [JsonPropertyName("done")] public bool Completed { get; init; }

  // a compaction checkpoint: carries the high-water sequence forward so a compacted (empty of
  // real work) journal still proves how current it is — a stale mirror on a returning member
  // can then be told apart from a genuinely interrupted op. Never an intent to replay.
  [JsonPropertyName("chk")] public bool Checkpoint { get; init; }
}

/// <summary>Durable storage for journal lines; mirrored across members so no single member loss destroys it.</summary>
public interface IJournalStore {
  /// <summary>Appends one line durably (fsync before returning) — the fsync-before-mutate ordering (SAFE-ORDER).</summary>
  void Append(string line);

  /// <summary>
  /// Appends several lines with ONE durability barrier covering all of them.
  ///
  /// The ordering guarantee is unchanged — this does not return until every line is on disk — but
  /// the cost of the barrier is shared instead of paid per record. A store that cannot do better
  /// falls back to appending one at a time, which is what this default does.
  /// </summary>
  void AppendBatch(IReadOnlyList<string> lines) {
    foreach (var line in lines)
      this.Append(line);
  }

  IEnumerable<string> ReadAll();

  /// <summary>Atomically replaces the journal content (checkpoint compaction).</summary>
  void Rewrite(IEnumerable<string> lines);

  /// <summary>
  /// Overwrites any stale journal mirror (a member that was offline during a compaction) with
  /// the freshest one, so its old, already-completed intents can never be replayed as if
  /// interrupted (SAFE-OFFLINE). No-op for stores that are not member-mirrored.
  /// </summary>
  void ReconcileMirrors() { }
}

/// <summary>
/// Journal store mirrored on every online member under .drivebenderutility/journal.jsonl
/// (a self-contained sidecar the original product ignores, SAFE-COMPAT). Reads take the
/// union of all copies so any single surviving member suffices.
///
/// The journal lives ONLY on members that can hold it cheaply and durably — those with
/// random-write AND a durable flush (local/UNC disks). A whole-file remote (FTP/WebDAV/cloud)
/// has no positional append: appending would re-download and re-upload the ENTIRE, ever-growing
/// journal per intent, throttling the whole pool to WAN speed and never compacting. Such members
/// are skipped; the WAL still lives redundantly on every real disk (SAFE-WAL). Only when NO
/// member is journal-capable (an all-remote pool) does it fall back to writing everywhere.
/// </summary>
public sealed class MemberJournalStore(IReadOnlyList<IVolumeIO> members) : IJournalStore {

  public const string JournalPath = PoolPaths.UtilityFolderName + "/journal.jsonl";

  private static bool _JournalCapable(IVolumeIO m)
    => (m.Caps & BackendCaps.RandomWrite) != 0 && (m.Caps & BackendCaps.DurableFlush) != 0;

  private IEnumerable<IVolumeIO> _Online {
    get {
      var capable = members.Where(m => m.IsOnline && _JournalCapable(m)).ToArray();
      return capable.Length > 0 ? capable : members.Where(m => m.IsOnline);
    }
  }

  public void Append(string line) => this.AppendBatch([line]);

  /// <summary>
  /// How many mirrors must hold an append before it counts as durable. The rest receive it behind,
  /// in order, without the caller waiting for them.
  /// </summary>
  public const int DurableQuorum = 2;

  /// <summary>
  /// Each member's appends, chained so they reach its mirror in the order they were made even when
  /// the caller stopped waiting for that member long ago.
  /// </summary>
  private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> _tails = new();
  private readonly Lock _chainLock = new();

  /// <summary>
  /// Durable on <see cref="DurableQuorum"/> mirrors before it returns; on the remaining ones behind it.
  ///
  /// Every append used to wait for EVERY member's flush, so the journal ran at the pace of the
  /// pool's slowest disk — one spinning disk in a pool of SSDs set the cost of every create, rename
  /// and delete. Two durable mirrors already survive the loss of any one disk; the others still get
  /// every line, in order, a moment later. Lines a lagging mirror never received are harmless:
  /// reads combine every mirror (see ReadAll), and a mirror that missed lines is rewritten whole at
  /// the next reconciliation.
  /// </summary>
  public void AppendBatch(IReadOnlyList<string> lines) {
    if (lines.Count == 0)
      return;

    var builder = new StringBuilder();
    foreach (var line in lines)
      builder.Append(line).Append('\n');

    var bytes = Encoding.UTF8.GetBytes(builder.ToString());
    var targets = this._Online.ToArray();
    if (targets.Length == 0)
      throw new PoolFsException(PoolFsError.IoError, "Journal intent could not be persisted on any member — refusing the mutation (SAFE-WAL)");

    // Every mirror IS the quorum (two disks or fewer): nothing would run behind, so the chains only
    // cost a task per member. Written directly, once anything still queued for these mirrors from a
    // time with more of them online has landed, which keeps each mirror's lines in order.
    if (targets.Length <= DurableQuorum) {
      this._CatchUp();
      var wrote = 0;
      Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, targets.Length) }, member => {
        if (_AppendTo(member, bytes))
          Interlocked.Increment(ref wrote);
      });

      if (wrote == 0)
        throw new PoolFsException(PoolFsError.IoError, "Journal intent could not be persisted on any member — refusing the mutation (SAFE-WAL)");
      return;
    }

    var quorum = DurableQuorum;
    var succeeded = 0;
    var finished = 0;
    using var progress = new SemaphoreSlim(0);

    lock (this._chainLock) // the chains are extended in one order for all members
      foreach (var member in targets) {
        var previous = this._tails.TryGetValue(member.MemberId, out var tail) ? tail : Task.CompletedTask;
        this._tails[member.MemberId] = previous.ContinueWith(_ => {
          try {
            if (_AppendTo(member, bytes))
              Interlocked.Increment(ref succeeded);
          } finally {
            Interlocked.Increment(ref finished);
            try {
              progress.Release();
            } catch (ObjectDisposedException) {
              // the caller has its quorum and is gone; this mirror simply caught up behind it
            }
          }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
      }

    // until enough mirrors have it, or every mirror has answered
    while (Volatile.Read(ref succeeded) < quorum && Volatile.Read(ref finished) < targets.Length)
      progress.Wait();

    if (Volatile.Read(ref succeeded) == 0)
      throw new PoolFsException(PoolFsError.IoError, "Journal intent could not be persisted on any member — refusing the mutation (SAFE-WAL)");
  }

  private static bool _AppendTo(IVolumeIO member, byte[] bytes) {
    try {
      using var stream = member.OpenWrite(JournalPath, false, true);
      stream.Seek(0, SeekOrigin.End);
      stream.Write(bytes, 0, bytes.Length);
      stream.Flush();
      return true;
    } catch (PoolFsException e) {
      DriveBender.Logger($"[Warning]Journal append failed on '{member.DisplayName}': {e.Message}");
      return false;
    }
  }

  /// <summary>Waits until every mirror holds every append made so far — before a mirror is read or rewritten.</summary>
  private void _CatchUp() {
    Task[] tails;
    lock (this._chainLock)
      tails = [.. this._tails.Values];
    Task.WaitAll(tails);
  }

  /// <summary>
  /// The union of every readable mirror — minus what a compaction has proven finished.
  ///
  /// An append counts as durable once ANY mirror takes it, so no single mirror is guaranteed to
  /// hold every intent: intent N can reach only A and intent N+1 only B. Reading just the mirror
  /// with the highest sequence then read B alone, and intent N was never replayed after a crash.
  ///
  /// Taking everything is not safe either. A member that was offline during a compaction keeps
  /// intents whose completion records were compacted away elsewhere; replayed, its orphan
  /// "Delete F" would destroy a since-recreated F. A compaction writes a checkpoint carrying the
  /// high-water sequence it covered, and a mirror holding that checkpoint holds every intent at
  /// or below it that was still open. So below the newest checkpoint only the mirrors carrying it
  /// are believed; above it nothing has been compacted yet, and every mirror's records count.
  /// Completion records are taken from every mirror at any sequence: sequences are never reused,
  /// so a completion can only ever close the intent it was written for.
  /// </summary>
  public IEnumerable<string> ReadAll() {
    var perMember = this._ReadPerMember();
    if (perMember.Count == 0)
      return [];

    var newestCheckpoint = perMember.Max(m => m.checkpoint);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var lines = new List<string>();
    foreach (var (_, memberCheckpoint, memberLines) in perMember) {
      var current = memberCheckpoint == newestCheckpoint;
      foreach (var line in memberLines)
        if ((current || _IsCompletion(line) || _SeqOf(line) > newestCheckpoint) && seen.Add(line))
          lines.Add(line);
    }

    return lines;
  }

  // System.Text.Json escapes quotes inside strings, so a path can never forge these markers
  private static bool _IsCompletion(string line) => line.Contains("\"done\":true", StringComparison.Ordinal);

  private static bool _IsCheckpoint(string line) => line.Contains("\"chk\":true", StringComparison.Ordinal);

  private List<(IVolumeIO member, long checkpoint, List<string> lines)> _ReadPerMember() {
    this._CatchUp();
    var perMember = new List<(IVolumeIO, long, List<string>)>();
    foreach (var member in this._Online) {
      if (!member.FileExists(JournalPath, false))
        continue;

      string content;
      try {
        using var stream = member.OpenRead(JournalPath, false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        content = reader.ReadToEnd();
      } catch (PoolFsException) {
        continue;
      }

      // the newest compaction this mirror took part in (0 when it was never compacted)
      var lines = new List<string>();
      long checkpoint = 0;
      foreach (var line in content.Split('\n')) {
        if (line.Length == 0)
          continue;

        lines.Add(line);
        if (_IsCheckpoint(line) && _SeqOf(line) is var seq && seq > checkpoint)
          checkpoint = seq;
      }

      perMember.Add((member, checkpoint, lines));
    }

    return perMember;
  }

  /// <summary>Cheap extraction of the "seq" field without a full JSON parse (called per line at mount).</summary>
  private static long _SeqOf(string line) {
    const string key = "\"seq\":";
    var start = line.IndexOf(key, StringComparison.Ordinal);
    if (start < 0)
      return 0;

    start += key.Length;
    var end = start;
    while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '-'))
      ++end;

    return long.TryParse(line.AsSpan(start, end - start), out var value) ? value : 0;
  }

  /// <summary>
  /// Writes the merged view (<see cref="ReadAll"/>) to every mirror that does not already hold
  /// exactly it. Copying the mirror with the highest sequence over the others used to do this, and
  /// that erased any intent the others held but it had missed.
  /// </summary>
  public void ReconcileMirrors() {
    var perMember = this._ReadPerMember();
    if (perMember.Count == 0)
      return;

    var merged = this.ReadAll().ToArray();
    var mergedSet = new HashSet<string>(merged, StringComparer.Ordinal);

    // any member whose journal is not already exactly the merged set gets overwritten — a
    // returning member's stale intents are wiped so they can never resurface (SAFE-OFFLINE)
    foreach (var (member, _, lines) in perMember) {
      if (lines.Count == mergedSet.Count && lines.All(mergedSet.Contains))
        continue;

      this._RewriteOne(member, merged);
    }
  }

  public void Rewrite(IEnumerable<string> lines) {
    // a line still on its way to a lagging mirror must land before the rewrite, never after it:
    // after, it would bring back an intent the compaction has just retired
    this._CatchUp();
    var snapshot = lines.ToArray();
    foreach (var member in this._Online)
      this._RewriteOne(member, snapshot);
  }

  private void _RewriteOne(IVolumeIO member, IReadOnlyCollection<string> lines) {
    var bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
    try {
      var temp = JournalPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
      using (var stream = member.OpenWrite(temp, false, true)) {
        stream.SetLength(0); // never inherit a stale temp's tail (SAFE-ATOMIC)
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
      }

      member.AtomicReplace(temp, JournalPath, false);
    } catch (PoolFsException e) {
      DriveBender.Logger($"[Warning]Journal rewrite failed on '{member.DisplayName}': {e.Message}");
    }
  }

}

/// <summary>
/// The per-pool write-ahead log (CMP-WAL, SAFE-WAL): every non-atomic mutation logs a
/// durable intent before touching disk and a completion record after, so a crash at any
/// instant is recoverable to a consistent state. Replay is idempotent (SAFE-IDEMP).
/// </summary>
public sealed class Journal(IJournalStore store, Func<DateTime>? clock = null) {

  private readonly Lock _lock = new();
  private readonly Func<DateTime> _clock = clock ?? (static () => DateTime.UtcNow);
  private long _nextSequence;
  private bool _loaded;

  // sequences of intents still awaiting completion; the journal is compacted down to just these so
  // at rest it holds only open entries (completed history is redundant once its mutation is durable)
  private readonly HashSet<long> _open = [];
  private int _completedSinceCompact;
  private DateTime _lastCompactUtc = DateTime.MinValue;
  private const int _COMPACT_THRESHOLD = 512; // bound growth under sustained load that never quiesces
  private static readonly TimeSpan _COMPACT_MIN_INTERVAL = TimeSpan.FromSeconds(5); // amortized: a per-chunk quiesce during a large copy must not rewrite per write

  private static readonly JsonSerializerOptions _OPTIONS = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

  private void _EnsureLoaded() {
    if (this._loaded)
      return;

    // the RAW read (checkpoints included) so the high-water sequence survives a full compaction —
    // otherwise _nextSequence would reset to 0 and reuse sequence numbers (SAFE-OFFLINE)
    var all = this._ReadRaw();
    this._nextSequence = all.Select(r => r.Sequence).DefaultIfEmpty(0).Max();
    var completed = all.Where(r => r.Completed).Select(r => r.Sequence).ToHashSet();
    foreach (var record in all)
      if (!record.Completed && !record.Checkpoint && !completed.Contains(record.Sequence))
        this._open.Add(record.Sequence);
    this._loaded = true;
  }

  /// <summary>One caller waiting for its record to become durable.</summary>
  private sealed class PendingRecord(string line) {
    public readonly string Line = line;
    public readonly ManualResetEventSlim Durable = new(false);
    public Exception? Failure;
  }

  private readonly Lock _commitLock = new();
  private readonly List<PendingRecord> _queued = [];
  private readonly Lock _queueLock = new();

  /// <summary>
  /// Makes one record durable, sharing the barrier with whatever else is waiting (group commit).
  ///
  /// The guarantee is exactly as before: this does not return until THIS record is on disk, so a
  /// caller still never mutates anything before its intent is durable. What changes is who pays for
  /// the flush. Every journalled operation used to take its own barrier while holding the journal's
  /// lock, so concurrent operations queued behind each other and each paid in full — a small file
  /// costs an intent and a completion, and with two members that was four serial fsyncs, which is
  /// most of the fifteen milliseconds a small file took.
  ///
  /// Leader/followers: whoever wins <see cref="_commitLock"/> flushes everything queued, including
  /// records that arrived while the previous leader was inside its flush. Under load one barrier
  /// covers many operations; a lone caller simply commits its own record and waits for nothing, so
  /// there is no latency added when there is nothing to batch with.
  ///
  /// Deliberately NOT called while holding <see cref="_lock"/> — that is the whole point, and it is
  /// also why compaction takes <see cref="_commitLock"/>: a rewrite must not run while a batch is
  /// in flight against the same file.
  /// </summary>
  private void _AppendDurable(string line) {
    var mine = new PendingRecord(line);
    lock (this._queueLock)
      this._queued.Add(mine);

    lock (this._commitLock)
      if (!mine.Durable.IsSet) {
        List<PendingRecord> batch;
        lock (this._queueLock) {
          batch = [.. this._queued];
          this._queued.Clear();
        }

        if (batch.Count > 0) {
          Exception? failure = null;
          try {
            store.AppendBatch([.. batch.Select(p => p.Line)]);
          } catch (Exception e) {
            failure = e;
          }

          foreach (var pending in batch) {
            pending.Failure = failure;
            pending.Durable.Set();
          }
        }
      }

    mine.Durable.Wait();
    if (mine.Failure != null)
      throw mine.Failure;
  }

  public long LogIntent(JournalOp op, string? path = null, string? targetPath = null, long offset = 0, long length = 0, Guid memberId = default, bool shadow = false) {
    string line;
    long sequence;
    lock (this._lock) {
      this._EnsureLoaded();
      var record = new JournalRecord {
        Sequence = ++this._nextSequence,
        Op = op,
        Path = path,
        TargetPath = targetPath,
        Offset = offset,
        Length = length,
        MemberId = memberId,
        Shadow = shadow,
        LoggedUtc = this._clock(),
      };
      line = JsonSerializer.Serialize(record, _OPTIONS);
      this._open.Add(record.Sequence);
      sequence = record.Sequence;
    }

    // durability happens OUTSIDE the journal lock, which is what lets concurrent operations share
    // one barrier. Records may therefore reach the file out of sequence order, which is harmless:
    // each one carries its own sequence, and a completion is only ever enqueued after its intent
    // has already returned durable, so an intent can never land after the completion it belongs to.
    this._AppendDurable(line);
    return sequence;
  }

  public void Complete(long sequence, JournalOp op) {
    // 0 is never a real sequence (they start at 1) and means "this operation was never journaled":
    // callers that elide an intent can then complete unconditionally, as the write path already does
    if (sequence == 0)
      return;

    string line;
    lock (this._lock) {
      this._EnsureLoaded();
      line = JsonSerializer.Serialize(new JournalRecord { Sequence = sequence, Op = op, Completed = true }, _OPTIONS);
      this._open.Remove(sequence);
      ++this._completedSinceCompact;
    }

    this._AppendDurable(line);
    this._CompactIfDue();
  }

  /// <summary>
  /// Compaction keeps the at-rest journal down to its open entries — but AMORTIZED: a large
  /// sequential copy quiesces after every chunk, and rewriting the journal on each of those starves
  /// the data path (the disk ends up writing journals instead of the file). A checkpoint line always
  /// survives so the high-water sequence is never lost (SAFE-OFFLINE).
  ///
  /// It takes <see cref="_commitLock"/> around the rewrite because a rewrite REPLACES the file, and
  /// a group-commit batch may be appending to it at that moment — the two must never overlap. The
  /// lock order is always <see cref="_lock"/> then <see cref="_commitLock"/>, and the commit path
  /// takes only the latter, so the two can never deadlock.
  /// </summary>
  private void _CompactIfDue() {
    lock (this._lock) {
      var now = this._clock();
      var quiescent = this._open.Count == 0 && now - this._lastCompactUtc >= _COMPACT_MIN_INTERVAL;
      var overdue = !quiescent && this._completedSinceCompact >= _COMPACT_THRESHOLD;
      if (!quiescent && !overdue)
        return;

      lock (this._commitLock)
        store.Rewrite(quiescent
          ? this._CheckpointLines()
          : [
            .. this._CheckpointLines(),
            .. this.ReadAll()
              .Where(r => !r.Completed && !r.Checkpoint && this._open.Contains(r.Sequence))
              .Select(r => JsonSerializer.Serialize(r, _OPTIONS)),
          ]);

      this._completedSinceCompact = 0;
      this._lastCompactUtc = now;
    }
  }

  /// <summary>
  /// The checkpoint prefix for a compaction: a single record carrying the current high-water
  /// sequence so mirror freshness stays comparable — but nothing at all until a sequence has
  /// ever been allocated (a never-written pool keeps an empty journal, occupying no space).
  /// </summary>
  private string[] _CheckpointLines()
    => this._nextSequence == 0
      ? []
      : [JsonSerializer.Serialize(new JournalRecord { Sequence = this._nextSequence, Op = JournalOp.Create, Completed = true, Checkpoint = true }, _OPTIONS)];

  /// <summary>Overwrites any stale journal mirror with the freshest one (call on mount and on member return, SAFE-OFFLINE).</summary>
  public void ReconcileMirrors() {
    lock (this._lock) {
      this._EnsureLoaded();
      store.ReconcileMirrors();
    }
  }

  /// <summary>Every real record (compaction checkpoints hidden — they are internal high-water bookkeeping, not operations).</summary>
  public IReadOnlyList<JournalRecord> ReadAll() => [.. this._ReadRaw().Where(r => !r.Checkpoint)];

  private List<JournalRecord> _ReadRaw() {
    var records = new List<JournalRecord>();
    foreach (var line in store.ReadAll()) {
      try {
        var record = JsonSerializer.Deserialize<JournalRecord>(line, _OPTIONS);
        if (record != null)
          records.Add(record);
      } catch (JsonException) {
        // a torn final line is expected after power loss — everything before it is intact
      }
    }

    return records;
  }

  /// <summary>Intents without a completion record — the operations a crash interrupted.</summary>
  public IReadOnlyList<JournalRecord> ReadIncomplete() {
    var completed = new HashSet<long>();
    var intents = new List<JournalRecord>();
    foreach (var record in this.ReadAll())
      if (record.Completed)
        completed.Add(record.Sequence);
      else
        intents.Add(record);

    return [.. intents.Where(i => !completed.Contains(i.Sequence)).OrderBy(i => i.Sequence)];
  }

  /// <summary>Compacts the journal after successful recovery: completed history is dropped, the high-water checkpoint kept.</summary>
  public void Checkpoint() {
    lock (this._lock) {
      this._EnsureLoaded();
      store.Rewrite(this._CheckpointLines());
    }
  }

}
