using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisonM.Vfs.Engine;

/// <summary>One namespace change (delete/rename/rmdir) that offline members still owe.</summary>
public sealed record TombstoneRecord {
  [JsonPropertyName("id")] public required Guid Id { get; init; }

  // ALWAYS serialized: enum value 0 would be omitted by the compact writer and make the
  // record unparseable on read (same pitfall the journal had with Create)
  [JsonPropertyName("op")] [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required JournalOp Op { get; init; }
  [JsonPropertyName("path")] public required string Path { get; init; }
  [JsonPropertyName("target")] public string? TargetPath { get; init; }
  [JsonPropertyName("owed")] public required Guid[] OwedMembers { get; init; }

  // monotonic per-log sequence: a member offline during a prune keeps a stale copy of an
  // already-applied record; the higher-sequence mirrors are authoritative, so the stale one is
  // excluded (and overwritten) instead of resurrecting a delete against a recreated file.
  [JsonPropertyName("seq")] public long Sequence { get; init; }

  // a compaction checkpoint carrying the high-water sequence forward — never applied.
  [JsonPropertyName("chk")] public bool Checkpoint { get; init; }
}

/// <summary>
/// Deletion/rename tombstones (SAFE-OFFLINE): when a namespace change happens while a member
/// is offline, that member still holds the old files — without a record they would resurrect
/// on its return. Each tombstone lists the members that still owe the change; a returning
/// member replays what it missed, then drops out of the owed list. Mirrored on every online
/// member (like the journal) so no single member loss destroys it; recorded BEFORE the
/// mutation so a crash in between merely replays a no-op (SAFE-IDEMP).
/// </summary>
public sealed class TombstoneLog(IReadOnlyList<IVolumeIO> members) {

  public const string LogPath = PoolPaths.UtilityFolderName + "/tombstones.jsonl";

  private static readonly JsonSerializerOptions _OPTIONS = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

  private readonly Lock _lock = new();
  private long _nextSeq;
  private bool _loaded;

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  private void _EnsureLoaded() {
    if (this._loaded)
      return;

    this._nextSeq = this._ReadPerMember().Select(m => m.maxSeq).DefaultIfEmpty(0).Max();
    this._loaded = true;
  }

  /// <summary>Records a namespace change the given members missed; throws when it cannot be persisted anywhere (the mutation must then not proceed).</summary>
  public void Record(JournalOp op, string path, string? targetPath, IReadOnlyCollection<Guid> owedMembers) {
    if (owedMembers.Count == 0)
      return;

    lock (this._lock) {
      this._EnsureLoaded();
      var record = new TombstoneRecord { Id = Guid.NewGuid(), Op = op, Path = path, TargetPath = targetPath, OwedMembers = [.. owedMembers], Sequence = ++this._nextSeq };
      var line = JsonSerializer.Serialize(record, _OPTIONS);
      var bytes = Encoding.UTF8.GetBytes(line + "\n");
      var wrote = false;
      foreach (var member in this._Online) {
        try {
          using var stream = member.OpenWrite(LogPath, false, true);
          stream.Seek(0, SeekOrigin.End);
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush();
          wrote = true;
        } catch (PoolFsException e) {
          DriveBender.Logger($"[Warning]Tombstone append failed on '{member.DisplayName}': {e.Message}");
        }
      }

      if (!wrote)
        throw new PoolFsException(PoolFsError.IoError, "The offline-member tombstone could not be persisted on any member — refusing the mutation (SAFE-OFFLINE)");
    }
  }

  /// <summary>Applies every change one returned member missed; returns how many were replayed.</summary>
  public int ReplayFor(IVolumeIO member, IReadOnlyCollection<Guid> validMemberIds)
    => this.Replay([member], validMemberIds);

  /// <summary>
  /// Replays owed changes for every present member (a returned one live, or all of them on
  /// mount), prunes member ids that left the pool, and rewrites the log down to what is
  /// still owed. A change that cannot be applied right now stays owed and retries later.
  /// </summary>
  public int Replay(IReadOnlyList<IVolumeIO> presentMembers, IReadOnlyCollection<Guid> validMemberIds) {
    lock (this._lock) {
      var all = this._ReadAll();
      if (all.Count == 0)
        return 0;

      var applied = 0;
      var remaining = new List<TombstoneRecord>();
      foreach (var record in all) {
        var owed = record.OwedMembers.Where(validMemberIds.Contains).ToList();
        foreach (var member in presentMembers.Where(m => m.IsOnline && owed.Contains(m.MemberId)))
          try {
            this._Apply(member, record);
            owed.Remove(member.MemberId);
            ++applied;
          } catch (PoolFsException e) {
            DriveBender.Logger($"[Warning]Could not replay missed {record.Op} of '{record.Path}' on '{member.DisplayName}' — retrying later: {e.Message}");
          }

        if (owed.Count > 0)
          remaining.Add(record with { OwedMembers = [.. owed] });
      }

      this._Rewrite(remaining);
      return applied;
    }
  }

  private void _Apply(IVolumeIO member, TombstoneRecord record) {
    switch (record.Op) {
      case JournalOp.Delete:
        foreach (var shadow in new[] { false, true })
          if (member.FileExists(record.Path, shadow))
            member.Delete(record.Path, shadow);
        break;

      case JournalOp.Rename when record.TargetPath is { } target: {
        // folder rename the member missed: flip the directory if it still holds the old name
        if (member.FolderExists(record.Path, false) && !member.FolderExists(target, false)) {
          var toParent = PoolPaths.GetParent(target);
          if (toParent.Length > 0)
            member.EnsureFolder(toParent, false);

          member.RenameFolder(record.Path, target);
          break;
        }

        foreach (var shadow in new[] { false, true }) {
          if (!member.FileExists(record.Path, shadow))
            continue;

          if (member.FileExists(target, shadow)) {
            member.Delete(record.Path, shadow); // the move happened elsewhere; this leftover source is stale
            continue;
          }

          var parent = PoolPaths.GetParent(target);
          if (parent.Length > 0)
            member.EnsureFolder(parent, false);
          if (shadow)
            member.EnsureFolder(parent, true);

          member.AtomicReplace(record.Path, target, shadow);
        }

        break;
      }

      case JournalOp.RemoveDir:
        foreach (var shadow in new[] { true, false })
          if (member.FolderExists(record.Path, shadow))
            try {
              member.DeleteFolder(record.Path, shadow);
            } catch (PoolFsException e) when (e.Error == PoolFsError.NotEmpty) {
              // content the pool does not know about — never roll a destructive op over data;
              // the folder resurfaces in the union listing and the scrub adopts its content
              DriveBender.Logger($"[Warning]Missed folder delete of '{record.Path}' on '{member.DisplayName}' skipped — the folder is not empty");
            }

        break;
    }
  }

  private List<(IVolumeIO member, long maxSeq, List<TombstoneRecord> records)> _ReadPerMember() {
    var perMember = new List<(IVolumeIO, long, List<TombstoneRecord>)>();
    foreach (var member in this._Online) {
      if (!member.FileExists(LogPath, false))
        continue;

      string content;
      try {
        using var stream = member.OpenRead(LogPath, false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        content = reader.ReadToEnd();
      } catch (PoolFsException) {
        continue;
      }

      var records = new List<TombstoneRecord>();
      long maxSeq = 0;
      foreach (var line in content.Split('\n')) {
        if (line.Length == 0)
          continue;

        TombstoneRecord? record;
        try {
          record = JsonSerializer.Deserialize<TombstoneRecord>(line, _OPTIONS);
        } catch (JsonException) {
          continue; // a torn final line after power loss — everything before it is intact
        }

        if (record == null)
          continue;

        if (record.Sequence > maxSeq)
          maxSeq = record.Sequence;
        if (!record.Checkpoint)
          records.Add(record);
      }

      perMember.Add((member, maxSeq, records));
    }

    return perMember;
  }

  /// <summary>
  /// The authoritative owed set: only the FRESHEST mirrors (highest sequence). A member offline
  /// during a prune keeps a stale copy of an already-applied record at a lower sequence — that
  /// copy is excluded here (and overwritten by the next rewrite), so it can never re-apply a
  /// delete against a since-recreated file (SAFE-OFFLINE).
  /// </summary>
  private List<TombstoneRecord> _ReadAll() {
    var perMember = this._ReadPerMember();
    if (perMember.Count == 0)
      return [];

    var globalMax = perMember.Max(m => m.maxSeq);
    var byId = new Dictionary<Guid, TombstoneRecord>();
    foreach (var (_, maxSeq, records) in perMember.Where(m => m.maxSeq == globalMax))
      foreach (var record in records)
        if (!byId.TryGetValue(record.Id, out var existing) || record.OwedMembers.Length < existing.OwedMembers.Length)
          byId[record.Id] = record;

    return [.. byId.Values.OrderBy(r => r.Sequence)];
  }

  private void _Rewrite(IReadOnlyList<TombstoneRecord> records) {
    this._EnsureLoaded();

    // nothing owed and no log anywhere → a pool that never deleted/renamed offline; do not
    // materialise an empty sidecar just to hold a checkpoint
    if (records.Count == 0 && !this._Online.Any(m => m.FileExists(LogPath, false)))
      return;

    // a checkpoint carries the high-water sequence forward so a pruned (possibly empty) log still
    // out-ranks a stale mirror; without it, an emptied authoritative log would look older
    var checkpoint = new TombstoneRecord { Id = Guid.Empty, Op = JournalOp.Delete, Path = "", OwedMembers = [], Sequence = this._nextSeq, Checkpoint = true };
    var lines = new List<string> { JsonSerializer.Serialize(checkpoint, _OPTIONS) };
    lines.AddRange(records.Select(r => JsonSerializer.Serialize(r, _OPTIONS)));
    var bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    foreach (var member in this._Online) {
      try {
        var temp = LogPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
        using (var stream = member.OpenWrite(temp, false, true)) {
          stream.SetLength(0); // never inherit a stale temp's tail (SAFE-ATOMIC)
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush();
        }

        member.AtomicReplace(temp, LogPath, false);
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Tombstone rewrite failed on '{member.DisplayName}': {e.Message}");
      }
    }
  }

}
