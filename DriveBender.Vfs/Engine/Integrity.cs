using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisonM.Vfs.Engine;

public sealed record ChecksumEntry(
  [property: JsonPropertyName("size")] long Size,
  [property: JsonPropertyName("mtime")] long MTimeTicks,
  [property: JsonPropertyName("hash")] string Hash
);

/// <summary>
/// Per-member checksum database (FR-CHECKSUM): a redundant sidecar recording
/// {size, mtime, fastHash} per physical copy, written on the write path (the data is in
/// RAM anyway) and persisted lazily via atomic replace. Removing it leaves a valid pool
/// (SAFE-COMPAT); a missing DB degrades to compare-copies integrity, never blocks mount.
/// </summary>
public sealed class ChecksumDatabase(IVolumeIO member) {

  public const string DbPath = PoolPaths.UtilityFolderName + "/checksums.json";

  private Dictionary<string, ChecksumEntry>? _entries;
  private bool _dirty;
  private readonly Lock _lock = new();

  public IVolumeIO Member => member;

  public static string HashOf(ReadOnlySpan<byte> content) => Convert.ToHexString(XxHash3.Hash(content));

  /// <summary>Hashes a stream incrementally through a fixed buffer — never materialises the whole file (SAFE-BIGFILE).</summary>
  public static string HashOf(Stream source) {
    var hash = new XxHash3();
    var buffer = new byte[1 << 20];
    int read;
    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
      hash.Append(buffer.AsSpan(0, read));

    return Convert.ToHexString(hash.GetCurrentHash());
  }

  private Dictionary<string, ChecksumEntry> _Load() {
    if (this._entries != null)
      return this._entries;

    this._entries = new(StringComparer.OrdinalIgnoreCase);
    if (member.IsOnline && member.FileExists(DbPath, false)) {
      try {
        using var stream = member.OpenRead(DbPath, false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var loaded = JsonSerializer.Deserialize<Dictionary<string, ChecksumEntry>>(reader.ReadToEnd());
        if (loaded != null)
          foreach (var (key, value) in loaded)
            this._entries[key] = value;
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Checksum DB on '{member.DisplayName}' unreadable — rebuilding from scrub: {e.Message}");
      } catch (JsonException e) {
        DriveBender.Logger($"[Warning]Checksum DB on '{member.DisplayName}' corrupt — rebuilding from scrub: {e.Message}");
      }
    }

    return this._entries;
  }

  public ChecksumEntry? Get(string physicalPath) {
    lock (this._lock)
      return this._Load().GetValueOrDefault(physicalPath);
  }

  public void Set(string physicalPath, ChecksumEntry entry) {
    lock (this._lock) {
      this._Load()[physicalPath] = entry;
      this._dirty = true;
    }
  }

  public void Remove(string physicalPath) {
    lock (this._lock) {
      if (this._Load().Remove(physicalPath))
        this._dirty = true;
    }
  }

  public void Rename(string fromPhysical, string toPhysical) {
    lock (this._lock) {
      var entries = this._Load();
      if (!entries.Remove(fromPhysical, out var entry))
        return;

      entries[toPhysical] = entry;
      this._dirty = true;
    }
  }

  /// <summary>Remaps every entry under a renamed folder (embedded shadow entries included) so checksums survive folder renames.</summary>
  public void RenamePrefix(string fromPhysicalFolder, string toPhysicalFolder) {
    lock (this._lock) {
      var entries = this._Load();
      var fromPrefix = fromPhysicalFolder + "/";
      foreach (var key in entries.Keys.Where(k => k.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray()) {
        entries.Remove(key, out var entry);
        entries[toPhysicalFolder + "/" + key[fromPrefix.Length..]] = entry!;
        this._dirty = true;
      }
    }
  }

  public void Save() {
    lock (this._lock) {
      if (!this._dirty || this._entries == null || !member.IsOnline)
        return;

      var json = JsonSerializer.Serialize(this._entries);
      var bytes = Encoding.UTF8.GetBytes(json);
      try {
        var temp = DbPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
        using (var stream = member.OpenWrite(temp, false, true)) {
          stream.SetLength(0);
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush();
        }

        member.AtomicReplace(temp, DbPath, false);
        this._dirty = false;
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Could not persist checksum DB on '{member.DisplayName}': {e.Message}");
      }
    }
  }

}

public enum IntegrityIssueKind {
  BitRotRepaired,
  BitRotUnrecoverable,
  ExternalEditAccepted,
  Conflict,
  BitRotDetected,
  ExternalEditDetected,
  StaleCopyRepaired,
  StaleCopyDetected,
}

public sealed record IntegrityIssue(IntegrityIssueKind Kind, string Path, string Message);

/// <summary>
/// Bit-rot detection, out-of-band-change reconciliation and scrubbing (CMP-SCRUB,
/// SAFE-OOB). Divergences between a copy and its DB entry are classified conservatively
/// before acting: content changed with unchanged (size, mtime) is silent corruption —
/// repaired from a DB-matching copy with the corrupt content quarantined; advanced
/// (size, mtime) is a legitimate external edit — accepted and re-propagated; anything
/// ambiguous is kept as a conflict. The last copy of any content is never overwritten.
/// </summary>
public sealed class IntegrityService(IReadOnlyList<IVolumeIO> members, ExternalEditPolicy editPolicy = ExternalEditPolicy.AcceptNewest) {

  private readonly Dictionary<Guid, ChecksumDatabase> _databases = members.ToDictionary(m => m.MemberId, m => new ChecksumDatabase(m));
  private long _quarantineCounter;

  // a per-instance token so a quarantine path from THIS run can never collide with (and overwrite)
  // a version quarantined by an earlier run/process — every preserved version stays preserved
  private readonly string _quarantineToken = Guid.NewGuid().ToString("N")[..8];

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  private ChecksumDatabase _Db(IVolumeIO member) => this._databases[member.MemberId];

  #region write-path hooks (FR-CHECKSUM: no extra read — the engine already holds the data)

  public void RecordWholeFile(IVolumeIO member, string normalizedPath, bool shadow, byte[] content) {
    var meta = member.Stat(normalizedPath, shadow);
    if (meta == null)
      return;

    this._Db(member).Set(PoolPaths.ToPhysical(normalizedPath, shadow), new(content.Length, meta.Value.LastWriteTimeUtc.Ticks, ChecksumDatabase.HashOf(content)));
  }

  /// <summary>Records a copy's checksum from an already-computed hash (streaming repair path — no re-read, no byte[]).</summary>
  public void RecordHash(IVolumeIO member, string normalizedPath, bool shadow, string hash) {
    var meta = member.Stat(normalizedPath, shadow);
    if (meta == null)
      return;

    this._Db(member).Set(PoolPaths.ToPhysical(normalizedPath, shadow), new(meta.Value.Length, meta.Value.LastWriteTimeUtc.Ticks, hash));
  }

  /// <summary>A positional write changed part of a file: the stale entries drop; the next scrub re-baselines.</summary>
  public void InvalidateFile(string normalizedPath) {
    foreach (var database in this._databases.Values)
    foreach (var shadow in new[] { false, true })
      database.Remove(PoolPaths.ToPhysical(normalizedPath, shadow));
  }

  public void RenameFile(string fromNormalized, string toNormalized) {
    foreach (var database in this._databases.Values)
    foreach (var shadow in new[] { false, true })
      database.Rename(PoolPaths.ToPhysical(fromNormalized, shadow), PoolPaths.ToPhysical(toNormalized, shadow));
  }

  /// <summary>Folder rename: every checksum under the subtree follows the new physical prefix.</summary>
  public void RenameSubtree(string fromNormalized, string toNormalized) {
    foreach (var database in this._databases.Values)
      database.RenamePrefix(fromNormalized, toNormalized);
  }

  public void SaveAll() {
    foreach (var database in this._databases.Values)
      database.Save();
  }

  #endregion

  /// <summary>Full scrub: verifies every file on every member against the DB and across copies (FR-SCRUB).</summary>
  public IReadOnlyList<IntegrityIssue> ScrubAll(Action<string>? invalidateCaches = null)
    => this._Scrub(quick: false, detectOnly: false, invalidateCaches);

  /// <summary>Mount-time delta scan (FR-OOB-MOUNT): only files whose (size, mtime) deviate from the DB are verified.</summary>
  public IReadOnlyList<IntegrityIssue> QuickScan(Action<string>? invalidateCaches = null)
    => this._Scrub(quick: true, detectOnly: false, invalidateCaches);

  /// <summary>
  /// Deep detection pass: re-checksums every file (bit-rot, stale copies, conflicts) but
  /// NEVER mutates the pool — health checking is read-only unless a fix is asked for.
  /// </summary>
  public IReadOnlyList<IntegrityIssue> DetectAll()
    => this._Scrub(quick: false, detectOnly: true, null);

  /// <summary>Cheap detection pass: only files whose metadata deviates (from the DB, or across copies) are verified; never mutates.</summary>
  public IReadOnlyList<IntegrityIssue> DetectQuick()
    => this._Scrub(quick: true, detectOnly: true, null);

  // no whole-file Content: the hash is computed by streaming, repair re-streams from the source
  private sealed record CopyView(IVolumeIO Member, bool Shadow, long Size, long MTimeTicks, ChecksumEntry? Entry, string Hash) {
    public bool MatchesEntry => this.Entry != null && this.Hash == this.Entry.Hash;
    public bool MetaMatchesEntry => this.Entry != null && this.Size == this.Entry.Size && this.MTimeTicks == this.Entry.MTimeTicks;
  }

  private IReadOnlyList<IntegrityIssue> _Scrub(bool quick, bool detectOnly, Action<string>? invalidateCaches) {
    var issues = new List<IntegrityIssue>();
    foreach (var path in this._AllLogicalPaths()) {
      var views = this._CollectCopies(path, quick);
      if (views == null)
        continue; // quick scan: nothing deviates

      this._ClassifyAndReconcile(path, views, issues, invalidateCaches, detectOnly);
    }

    if (!detectOnly)
      this.SaveAll();
    return issues;
  }

  private IEnumerable<string> _AllLogicalPaths() {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var member in this._Online) {
      var stack = new Stack<string>();
      stack.Push("");
      while (stack.Count > 0) {
        var folder = stack.Pop();
        VolumeEntry[] entries;
        try {
          entries = [.. member.List(folder, false)];
        } catch (PoolFsException) {
          continue;
        }

        foreach (var entry in entries) {
          var childPath = folder.Length == 0 ? entry.Name : $"{folder}/{entry.Name}";
          if (entry.IsDirectory) {
            if (entry.Name.Equals(DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase)) {
              // shadow container: surface its files under their logical names
              foreach (var shadowEntry in this._SafeList(member, $"{folder}", true).Where(e => !e.IsDirectory && !PoolPaths.IsHiddenName(e.Name)))
                seen.Add(folder.Length == 0 ? shadowEntry.Name : $"{folder}/{shadowEntry.Name}");
              continue;
            }

            if (!PoolPaths.IsHiddenName(entry.Name))
              stack.Push(childPath);
            continue;
          }

          if (!PoolPaths.IsHiddenName(entry.Name))
            seen.Add(childPath);
        }
      }
    }

    return seen;
  }

  private IEnumerable<VolumeEntry> _SafeList(IVolumeIO member, string folder, bool shadow) {
    try {
      return [.. member.List(folder, shadow)];
    } catch (PoolFsException) {
      return [];
    }
  }

  private List<CopyView>? _CollectCopies(string path, bool quick) {
    var metas = new List<(IVolumeIO member, bool shadow, FileMeta meta, ChecksumEntry? entry)>();
    foreach (var member in this._Online)
    foreach (var shadow in new[] { false, true }) {
      var meta = member.Stat(path, shadow);
      if (meta is not { } found || found.IsDirectory)
        continue;

      metas.Add((member, shadow, found, this._Db(member).Get(PoolPaths.ToPhysical(path, shadow))));
    }

    if (metas.Count == 0)
      return null;

    if (quick
        && metas.All(m => m.entry != null && m.entry.Size == m.meta.Length && m.entry.MTimeTicks == m.meta.LastWriteTimeUtc.Ticks)
        && metas.Select(m => m.meta.Length).Distinct().Count() == 1)
      return null; // (size, mtime) unchanged everywhere AND all copies agree in size — skip hashing

    var views = new List<CopyView>();
    foreach (var (member, shadow, meta, entry) in metas) {
      string hash;
      try {
        using var stream = member.OpenRead(path, shadow);
        hash = ChecksumDatabase.HashOf(stream); // streamed — a multi-GB copy is never held in RAM
      } catch (PoolFsException) {
        continue;
      }

      views.Add(new(member, shadow, meta.Length, meta.LastWriteTimeUtc.Ticks, entry, hash));
    }

    return views.Count == 0 ? null : views;
  }

  private void _ClassifyAndReconcile(string path, List<CopyView> views, List<IntegrityIssue> issues, Action<string>? invalidateCaches, bool detectOnly = false) {
    var bitRot = views.Where(v => v.Entry != null && !v.MatchesEntry && v.MetaMatchesEntry).ToArray();
    var edited = views.Where(v => v.Entry != null && !v.MatchesEntry && !v.MetaMatchesEntry).ToArray();
    var good = views.Where(v => v.MatchesEntry).ToArray();

    if (bitRot.Length > 0) {
      // silent corruption: the filesystem never saw a write, yet the content changed (SAFE-OOB case 1)
      if (good.Length == 0 && edited.Length == 0) {
        issues.Add(new(IntegrityIssueKind.BitRotUnrecoverable, path, $"All {views.Count} copies fail their recorded checksum — nothing overwritten, data left in place"));
        DriveBender.Logger($"[Error]Bit-rot on '{path}' is unrecoverable: no copy matches the checksum DB");
        return;
      }

      if (detectOnly) {
        foreach (var corrupt in bitRot)
          issues.Add(new(IntegrityIssueKind.BitRotDetected, path, $"Silent corruption on '{corrupt.Member.DisplayName}' — a checksum-verified copy exists; a fix repairs it"));
      } else {
        var source = good.Length > 0 ? good[0] : edited.OrderByDescending(v => v.MTimeTicks).First();
        foreach (var corrupt in bitRot) {
          this._Quarantine(corrupt, path, "bitrot");
          this._Repair(source, corrupt, path);
          issues.Add(new(IntegrityIssueKind.BitRotRepaired, path, $"Repaired silent corruption on '{corrupt.Member.DisplayName}' from a checksum-verified copy; corrupt content quarantined"));
          DriveBender.Logger($" - Repaired bit-rot on '{path}' ({corrupt.Member.DisplayName})");
        }

        invalidateCaches?.Invoke(path);
      }

      if (edited.Length == 0)
        return;
    }

    var distinctEdits = edited.GroupBy(v => v.Hash, StringComparer.Ordinal).ToArray();
    switch (distinctEdits.Length) {
      case 0: {
        // no copy deviates from its own DB entry — but copies can still deviate from EACH OTHER
        // (a member missed writes while offline and its stale copy re-baselined): the newest
        // write wins, exactly like the engine's own last-writer semantics (SAFE-OFFLINE)
        var distinctContents = views.GroupBy(v => v.Hash, StringComparer.Ordinal).ToArray();
        if (distinctContents.Length > 1) {
          var ranked = views.OrderByDescending(v => v.MTimeTicks).ToArray();
          var winner = ranked[0];
          if (ranked[0].MTimeTicks == ranked[1].MTimeTicks && ranked[0].Hash != ranked[1].Hash) {
            // identical timestamps with different content: never guess — a conflict (SAFE-OOB case 3)
            if (detectOnly) {
              issues.Add(new(IntegrityIssueKind.Conflict, path, $"{distinctContents.Length} divergent copies with identical timestamps — a fix preserves every version for resolution"));
              return;
            }

            foreach (var loser in views.Where(v => v.Hash != winner.Hash))
              this._Quarantine(loser, path, "conflict");

            issues.Add(new(IntegrityIssueKind.Conflict, path, $"{distinctContents.Length} divergent versions detected; all preserved under {PoolPaths.UtilityFolderName}/conflicts for resolution"));
            DriveBender.Logger($"[Warning]Conflict on '{path}': divergent copies with identical timestamps kept for user resolution");
            return;
          }

          var staleCopies = views.Where(v => v.Hash != winner.Hash).ToArray();
          if (detectOnly) {
            issues.Add(new(IntegrityIssueKind.StaleCopyDetected, path, $"{staleCopies.Length} cop(ies) lag behind the newest write (e.g. on '{staleCopies[0].Member.DisplayName}') — a fix re-synchronizes them"));
            return;
          }

          // quarantine each stale copy BEFORE overwriting it — a skewed clock could make the
          // "newest" wrong, so the replaced content is always recoverable (SAFE-NOLOSS)
          foreach (var stale in staleCopies) {
            this._Quarantine(stale, path, "stale");
            this._Repair(winner, stale, path);
          }

          this.RecordHash(winner.Member, path, winner.Shadow, winner.Hash);
          invalidateCaches?.Invoke(path);
          issues.Add(new(IntegrityIssueKind.StaleCopyRepaired, path, $"Re-synchronized {staleCopies.Length} stale cop(ies) from the newest write (replaced content quarantined)"));
          DriveBender.Logger($" - Re-synchronized {staleCopies.Length} stale cop(ies) of '{path}'");
          return;
        }

        // no divergence — baseline anything the DB does not know yet (streamed re-hash)
        if (!detectOnly)
          foreach (var view in views.Where(v => v.Entry == null))
            this.RecordHash(view.Member, path, view.Shadow, view.Hash);
        return;
      }

      case 1 when editPolicy == ExternalEditPolicy.AcceptNewest: {
        if (detectOnly) {
          issues.Add(new(IntegrityIssueKind.ExternalEditDetected, path, "Externally edited behind the pool's back — a fix accepts it as authoritative and re-propagates it"));
          return;
        }

        // one coherent external edit: accept it as authoritative, re-propagate (SAFE-OOB case 2).
        // Quarantine each replaced copy first so an accepted-but-wrong edit stays recoverable.
        var winner = edited[0];
        foreach (var stale in views.Where(v => v.Hash != winner.Hash)) {
          this._Quarantine(stale, path, "replaced");
          this._Repair(winner, stale, path);
        }

        this.RecordHash(winner.Member, path, winner.Shadow, winner.Hash);
        invalidateCaches?.Invoke(path);
        issues.Add(new(IntegrityIssueKind.ExternalEditAccepted, path, "External edit accepted as authoritative and re-propagated to all copies"));
        DriveBender.Logger($" - Accepted external edit of '{path}' and re-synchronized {views.Count - 1} cop(ies)");
        return;
      }

      default: {
        if (detectOnly) {
          issues.Add(new(IntegrityIssueKind.Conflict, path, $"{distinctEdits.Length} divergent out-of-band versions — a fix preserves every version for resolution"));
          return;
        }

        // divergent edits or a conflict-only policy: keep every version, never guess (SAFE-OOB case 3)
        var ranked = edited.OrderByDescending(v => v.MTimeTicks).ToArray();
        var ambiguous = ranked.Length > 1 && ranked[0].MTimeTicks == ranked[1].MTimeTicks && ranked[0].Hash != ranked[1].Hash;
        var winner = ranked[0];
        foreach (var loser in edited.Where(v => v != winner))
          this._Quarantine(loser, path, "conflict");

        if (!ambiguous && editPolicy == ExternalEditPolicy.AcceptNewest)
          foreach (var stale in views.Where(v => v.Hash != winner.Hash))
            this._Repair(winner, stale, path);

        invalidateCaches?.Invoke(path);
        issues.Add(new(IntegrityIssueKind.Conflict, path, $"{distinctEdits.Length} divergent versions detected; all preserved under {PoolPaths.UtilityFolderName}/conflicts for resolution"));
        DriveBender.Logger($"[Warning]Conflict on '{path}': divergent out-of-band edits kept for user resolution");
        return;
      }
    }
  }

  /// <summary>Preserves a copy under conflicts/ before it is overwritten — streamed, with a per-run unique name so no earlier version is clobbered (SAFE-NOLOSS).</summary>
  private void _Quarantine(CopyView copy, string path, string reason) {
    var quarantinePath = $"{PoolPaths.UtilityFolderName}/conflicts/{path}.{reason}.{this._quarantineToken}.{Interlocked.Increment(ref this._quarantineCounter)}";
    try {
      copy.Member.EnsureFolder(PoolPaths.GetParent(quarantinePath), false);
      WholeFilePublisher.CopyBetween(copy.Member, path, copy.Shadow, copy.Member, quarantinePath, false);
    } catch (PoolFsException e) {
      DriveBender.Logger($"[Warning]Could not quarantine '{path}' on '{copy.Member.DisplayName}': {e.Message}");
    }
  }

  /// <summary>Overwrites a target copy with the source's content (streamed) and records the known hash — no re-read, no byte[].</summary>
  private void _Repair(CopyView source, CopyView target, string path) {
    WholeFilePublisher.CopyBetween(source.Member, path, source.Shadow, target.Member, path, target.Shadow);
    this.RecordHash(target.Member, path, target.Shadow, source.Hash);
  }

}
