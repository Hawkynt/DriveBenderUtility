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

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  private ChecksumDatabase _Db(IVolumeIO member) => this._databases[member.MemberId];

  #region write-path hooks (FR-CHECKSUM: no extra read — the engine already holds the data)

  public void RecordWholeFile(IVolumeIO member, string normalizedPath, bool shadow, byte[] content) {
    var meta = member.Stat(normalizedPath, shadow);
    if (meta == null)
      return;

    this._Db(member).Set(PoolPaths.ToPhysical(normalizedPath, shadow), new(content.Length, meta.Value.LastWriteTimeUtc.Ticks, ChecksumDatabase.HashOf(content)));
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

  public void SaveAll() {
    foreach (var database in this._databases.Values)
      database.Save();
  }

  #endregion

  /// <summary>Full scrub: verifies every file on every member against the DB and across copies (FR-SCRUB).</summary>
  public IReadOnlyList<IntegrityIssue> ScrubAll(Action<string>? invalidateCaches = null)
    => this._Scrub(quick: false, invalidateCaches);

  /// <summary>Mount-time delta scan (FR-OOB-MOUNT): only files whose (size, mtime) deviate from the DB are verified.</summary>
  public IReadOnlyList<IntegrityIssue> QuickScan(Action<string>? invalidateCaches = null)
    => this._Scrub(quick: true, invalidateCaches);

  private sealed record CopyView(IVolumeIO Member, bool Shadow, byte[] Content, long Size, long MTimeTicks, ChecksumEntry? Entry, string Hash) {
    public bool MatchesEntry => this.Entry != null && this.Hash == this.Entry.Hash;
    public bool MetaMatchesEntry => this.Entry != null && this.Size == this.Entry.Size && this.MTimeTicks == this.Entry.MTimeTicks;
  }

  private IReadOnlyList<IntegrityIssue> _Scrub(bool quick, Action<string>? invalidateCaches) {
    var issues = new List<IntegrityIssue>();
    foreach (var path in this._AllLogicalPaths()) {
      var views = this._CollectCopies(path, quick);
      if (views == null)
        continue; // quick scan: nothing deviates

      this._ClassifyAndReconcile(path, views, issues, invalidateCaches);
    }

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

    if (quick && metas.All(m => m.entry != null && m.entry.Size == m.meta.Length && m.entry.MTimeTicks == m.meta.LastWriteTimeUtc.Ticks))
      return null; // (size, mtime) unchanged everywhere — skip hashing

    var views = new List<CopyView>();
    foreach (var (member, shadow, meta, entry) in metas) {
      byte[] content;
      try {
        using var stream = member.OpenRead(path, shadow);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        content = buffer.ToArray();
      } catch (PoolFsException) {
        continue;
      }

      views.Add(new(member, shadow, content, meta.Length, meta.LastWriteTimeUtc.Ticks, entry, ChecksumDatabase.HashOf(content)));
    }

    return views.Count == 0 ? null : views;
  }

  private void _ClassifyAndReconcile(string path, List<CopyView> views, List<IntegrityIssue> issues, Action<string>? invalidateCaches) {
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

      var source = good.Length > 0 ? good[0] : edited.OrderByDescending(v => v.MTimeTicks).First();
      foreach (var corrupt in bitRot) {
        this._Quarantine(corrupt, path, "bitrot");
        this._Publish(corrupt.Member, path, corrupt.Shadow, source.Content);
        this.RecordWholeFile(corrupt.Member, path, corrupt.Shadow, source.Content);
        issues.Add(new(IntegrityIssueKind.BitRotRepaired, path, $"Repaired silent corruption on '{corrupt.Member.DisplayName}' from a checksum-verified copy; corrupt content quarantined"));
        DriveBender.Logger($" - Repaired bit-rot on '{path}' ({corrupt.Member.DisplayName})");
      }

      invalidateCaches?.Invoke(path);
      if (edited.Length == 0)
        return;
    }

    var distinctEdits = edited.GroupBy(v => v.Hash, StringComparer.Ordinal).ToArray();
    switch (distinctEdits.Length) {
      case 0:
        // no divergence — baseline anything the DB does not know yet
        foreach (var view in views.Where(v => v.Entry == null))
          this.RecordWholeFile(view.Member, path, view.Shadow, view.Content);
        return;

      case 1 when editPolicy == ExternalEditPolicy.AcceptNewest: {
        // one coherent external edit: accept it as authoritative, re-propagate (SAFE-OOB case 2)
        var winner = edited[0];
        foreach (var stale in views.Where(v => v.Hash != winner.Hash)) {
          this._Publish(stale.Member, path, stale.Shadow, winner.Content);
          this.RecordWholeFile(stale.Member, path, stale.Shadow, winner.Content);
        }

        this.RecordWholeFile(winner.Member, path, winner.Shadow, winner.Content);
        invalidateCaches?.Invoke(path);
        issues.Add(new(IntegrityIssueKind.ExternalEditAccepted, path, "External edit accepted as authoritative and re-propagated to all copies"));
        DriveBender.Logger($" - Accepted external edit of '{path}' and re-synchronized {views.Count - 1} cop(ies)");
        return;
      }

      default: {
        // divergent edits or a conflict-only policy: keep every version, never guess (SAFE-OOB case 3)
        var ranked = edited.OrderByDescending(v => v.MTimeTicks).ToArray();
        var ambiguous = ranked.Length > 1 && ranked[0].MTimeTicks == ranked[1].MTimeTicks && ranked[0].Hash != ranked[1].Hash;
        var winner = ranked[0];
        foreach (var loser in edited.Where(v => v != winner))
          this._Quarantine(loser, path, "conflict");

        if (!ambiguous && editPolicy == ExternalEditPolicy.AcceptNewest)
          foreach (var stale in views.Where(v => v.Hash != winner.Hash)) {
            this._Publish(stale.Member, path, stale.Shadow, winner.Content);
            this.RecordWholeFile(stale.Member, path, stale.Shadow, winner.Content);
          }

        invalidateCaches?.Invoke(path);
        issues.Add(new(IntegrityIssueKind.Conflict, path, $"{distinctEdits.Length} divergent versions detected; all preserved under {PoolPaths.UtilityFolderName}/conflicts for resolution"));
        DriveBender.Logger($"[Warning]Conflict on '{path}': divergent out-of-band edits kept for user resolution");
        return;
      }
    }
  }

  private void _Quarantine(CopyView copy, string path, string reason) {
    var quarantinePath = $"{PoolPaths.UtilityFolderName}/conflicts/{path}.{reason}.{Interlocked.Increment(ref this._quarantineCounter)}";
    try {
      copy.Member.EnsureFolder(PoolPaths.GetParent(quarantinePath), false);
      using var stream = copy.Member.OpenWrite(quarantinePath, false, true);
      stream.Write(copy.Content, 0, copy.Content.Length);
      stream.Flush();
    } catch (PoolFsException e) {
      DriveBender.Logger($"[Warning]Could not quarantine '{path}' on '{copy.Member.DisplayName}': {e.Message}");
    }
  }

  private void _Publish(IVolumeIO member, string path, bool shadow, byte[] content) {
    var temp = path + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
    using (var stream = member.OpenWrite(temp, shadow, true)) {
      stream.Write(content, 0, content.Length);
      stream.Flush();
    }

    member.AtomicReplace(temp, path, shadow);
  }

}
