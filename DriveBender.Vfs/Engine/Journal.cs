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
}

/// <summary>One journal line: an intent (Completed=false) or its completion marker.</summary>
public sealed record JournalRecord {
  [JsonPropertyName("seq")] public required long Sequence { get; init; }
  [JsonPropertyName("op")] public required JournalOp Op { get; init; }
  [JsonPropertyName("path")] public string? Path { get; init; }
  [JsonPropertyName("target")] public string? TargetPath { get; init; }
  [JsonPropertyName("offset")] public long Offset { get; init; }
  [JsonPropertyName("length")] public long Length { get; init; }
  [JsonPropertyName("member")] public Guid MemberId { get; init; }
  [JsonPropertyName("done")] public bool Completed { get; init; }
}

/// <summary>Durable storage for journal lines; mirrored across members so no single member loss destroys it.</summary>
public interface IJournalStore {
  /// <summary>Appends one line durably (fsync before returning) — the fsync-before-mutate ordering (SAFE-ORDER).</summary>
  void Append(string line);

  IEnumerable<string> ReadAll();

  /// <summary>Atomically replaces the journal content (checkpoint compaction).</summary>
  void Rewrite(IEnumerable<string> lines);
}

/// <summary>
/// Journal store mirrored on every online member under .drivebenderutility/journal.jsonl
/// (a self-contained sidecar the original product ignores, SAFE-COMPAT). Reads take the
/// union of all copies so any single surviving member suffices.
/// </summary>
public sealed class MemberJournalStore(IReadOnlyList<IVolumeIO> members) : IJournalStore {

  public const string JournalPath = PoolPaths.UtilityFolderName + "/journal.jsonl";

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  public void Append(string line) {
    var wrote = false;
    var bytes = Encoding.UTF8.GetBytes(line + "\n");
    foreach (var member in this._Online) {
      try {
        using var stream = member.OpenWrite(JournalPath, false, true);
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        wrote = true;
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Journal append failed on '{member.DisplayName}': {e.Message}");
      }
    }

    if (!wrote)
      throw new PoolFsException(PoolFsError.IoError, "Journal intent could not be persisted on any member — refusing the mutation (SAFE-WAL)");
  }

  public IEnumerable<string> ReadAll() {
    var lines = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
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

      foreach (var line in content.Split('\n'))
        if (line.Length > 0 && seen.Add(line))
          lines.Add(line);
    }

    return lines;
  }

  public void Rewrite(IEnumerable<string> lines) {
    var content = string.Concat(lines.Select(l => l + "\n"));
    var bytes = Encoding.UTF8.GetBytes(content);
    foreach (var member in this._Online) {
      try {
        var temp = JournalPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
        using (var stream = member.OpenWrite(temp, false, true)) {
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush();
        }

        member.AtomicReplace(temp, JournalPath, false);
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Journal rewrite failed on '{member.DisplayName}': {e.Message}");
      }
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

    var all = this.ReadAll();
    this._nextSequence = all.Select(r => r.Sequence).DefaultIfEmpty(0).Max();
    var completed = all.Where(r => r.Completed).Select(r => r.Sequence).ToHashSet();
    foreach (var record in all)
      if (!record.Completed && !completed.Contains(record.Sequence))
        this._open.Add(record.Sequence);
    this._loaded = true;
  }

  public long LogIntent(JournalOp op, string? path = null, string? targetPath = null, long offset = 0, long length = 0, Guid memberId = default) {
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
      };
      store.Append(JsonSerializer.Serialize(record, _OPTIONS));
      this._open.Add(record.Sequence);
      return record.Sequence;
    }
  }

  public void Complete(long sequence, JournalOp op) {
    lock (this._lock) {
      this._EnsureLoaded();
      store.Append(JsonSerializer.Serialize(new JournalRecord { Sequence = sequence, Op = op, Completed = true }, _OPTIONS));
      this._open.Remove(sequence);
      ++this._completedSinceCompact;

      // compaction keeps the at-rest journal down to its open entries — but AMORTIZED: a large
      // sequential copy quiesces after every chunk, and rewriting the journal on each of those
      // starves the data path (the disk ends up writing journals instead of the file)
      var now = this._clock();
      if (this._open.Count == 0 && now - this._lastCompactUtc >= _COMPACT_MIN_INTERVAL) {
        store.Rewrite([]);
        this._completedSinceCompact = 0;
        this._lastCompactUtc = now;
      } else if (this._completedSinceCompact >= _COMPACT_THRESHOLD) {
        store.Rewrite(this.ReadAll()
          .Where(r => !r.Completed && this._open.Contains(r.Sequence))
          .Select(r => JsonSerializer.Serialize(r, _OPTIONS)));
        this._completedSinceCompact = 0;
        this._lastCompactUtc = now;
      }
    }
  }

  public IReadOnlyList<JournalRecord> ReadAll() {
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

  /// <summary>Compacts the journal after successful recovery: completed history is dropped.</summary>
  public void Checkpoint() {
    lock (this._lock) {
      this._EnsureLoaded();
      store.Rewrite([]);
    }
  }

}
