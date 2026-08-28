using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisonM.Vfs.Engine;

public sealed record ChecksumEntry(
  [property: JsonPropertyName("size")] long Size,
  [property: JsonPropertyName("mtime")] long MTimeTicks,
  [property: JsonPropertyName("hash")] string Hash,

  /// <summary>
  /// The recorded hash is known to be out of date and must NOT be trusted by a scan.
  ///
  /// A positional write changes part of a file, and rehashing the whole thing on every such write
  /// would make random access cost O(file) per operation — unacceptable for a database or a VM
  /// image. So the write marks the entry stale and moves on, and the next scrub re-baselines it
  /// from a single streamed read. Marking rather than deleting matters twice over: a scan can tell
  /// "never known" from "known to be dirty", and a hot file being rewritten thousands of times
  /// marks the entry ONCE instead of dirtying the database on every write.
  /// </summary>
  [property: JsonPropertyName("stale")] bool Stale = false
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

  /// <summary>
  /// Keys this instance deliberately dropped since it loaded.
  ///
  /// <see cref="Save"/> merges with whatever is on disk rather than overwriting it, and without
  /// this a deletion would simply come back: the other writer still has the entry, the merge unions
  /// the two, and a rename's old key reappears for a file that no longer exists there.
  /// </summary>
  private readonly HashSet<string> _removed = new(PoolPaths.PathComparer);
  private readonly Lock _lock = new();

  public IVolumeIO Member => member;

  public static string HashOf(ReadOnlySpan<byte> content) => Convert.ToHexString(XxHash3.Hash(content));

  /// <summary>Hashes a stream incrementally through a fixed buffer — never materialises the whole file (SAFE-BIGFILE).</summary>
  public static string HashOf(Stream source) {
    // rented, not allocated: a scrub hashes every file on a member, and a fresh megabyte per file
    // is a megabyte of large-object-heap garbage per file
    const int size = 1 << 20;
    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
    try {
      // sliced to the size ASKED FOR, never buffer.Length: the pool rounds up, so the array is
      // routinely bigger and its length is not a fact about this operation
      var window = buffer.AsSpan(0, size);
      var hash = new XxHash3();
      int read;
      while ((read = source.Read(window)) > 0)
        hash.Append(window[..read]);

      return Convert.ToHexString(hash.GetCurrentHash());
    } finally {
      System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  private Dictionary<string, ChecksumEntry> _Load() {
    if (this._entries != null)
      return this._entries;

    this._entries = new(PoolPaths.PathComparer);
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

  /// <summary>
  /// Flags an entry as no longer trustworthy. Returns false when it was already flagged (or absent),
  /// so a file under continuous random writes does not dirty the database again and again.
  /// </summary>
  public bool MarkStale(string physicalPath) {
    lock (this._lock) {
      var entries = this._Load();
      if (!entries.TryGetValue(physicalPath, out var entry) || entry.Stale)
        return false;

      entries[physicalPath] = entry with { Stale = true };
      this._dirty = true;
      return true;
    }
  }

  public void Remove(string physicalPath) {
    lock (this._lock) {
      if (!this._Load().Remove(physicalPath))
        return;

      this._removed.Add(physicalPath);
      this._dirty = true;
    }
  }

  public void Rename(string fromPhysical, string toPhysical) {
    lock (this._lock) {
      var entries = this._Load();
      if (!entries.Remove(fromPhysical, out var entry))
        return;

      this._removed.Add(fromPhysical);
      this._removed.Remove(toPhysical); // it exists again, under the new name
      entries[toPhysical] = entry;
      this._dirty = true;
    }
  }

  /// <summary>Remaps every entry under a renamed folder (embedded shadow entries included) so checksums survive folder renames.</summary>
  public void RenamePrefix(string fromPhysicalFolder, string toPhysicalFolder) {
    lock (this._lock) {
      var entries = this._Load();
      var fromPrefix = fromPhysicalFolder + "/";
      foreach (var key in entries.Keys.Where(k => k.StartsWith(fromPrefix, PoolPaths.PathComparison)).ToArray()) {
        entries.Remove(key, out var entry);
        var moved = toPhysicalFolder + "/" + key[fromPrefix.Length..];
        this._removed.Add(key);
        this._removed.Remove(moved);
        entries[moved] = entry!;
        this._dirty = true;
      }
    }
  }

  /// <summary>Reads the sidecar as it stands on disk right now, without disturbing our own view.</summary>
  private Dictionary<string, ChecksumEntry> _ReadFromDisk() {
    var onDisk = new Dictionary<string, ChecksumEntry>(PoolPaths.PathComparer);
    if (!member.IsOnline || !member.FileExists(DbPath, false))
      return onDisk;

    try {
      using var stream = member.OpenRead(DbPath, false);
      using var reader = new StreamReader(stream, Encoding.UTF8);
      var loaded = JsonSerializer.Deserialize<Dictionary<string, ChecksumEntry>>(reader.ReadToEnd());
      if (loaded != null)
        foreach (var (key, value) in loaded)
          onDisk[key] = value;
    } catch (Exception e) when (e is PoolFsException or JsonException) {
      DriveBender.Logger($"[Warning]Checksum DB on '{member.DisplayName}' unreadable while merging: {e.Message}");
    }

    return onDisk;
  }

  /// <summary>Of two records for one file, the one a scan may believe.</summary>
  private static ChecksumEntry _Better(ChecksumEntry ours, ChecksumEntry theirs) {
    // a trustworthy record beats a flagged one whichever side it came from — a stale entry says
    // only "do not believe this", so the other side knowing something concrete wins
    if (ours.Stale != theirs.Stale)
      return ours.Stale ? theirs : ours;

    // otherwise the one describing the newer state of the file
    return theirs.MTimeTicks > ours.MTimeTicks ? theirs : ours;
  }

  public void Save() {
    lock (this._lock) {
      if (!this._dirty || this._entries == null || !member.IsOnline)
        return;

      // MERGE, never overwrite. This sidecar has more than one writer: `pool-health` runs in its
      // own process while a pool may be mounted in another, and each holds its own view. Writing
      // ours wholesale silently discarded the other's work — which is exactly how a deep scrub of a
      // MOUNTED pool lost the baseline it had just computed, leaving bit-rot undetectable while
      // appearing to have done something.
      var merged = this._ReadFromDisk();
      foreach (var key in this._removed)
        merged.Remove(key); // never resurrect what this instance deliberately dropped

      foreach (var (key, ours) in this._entries)
        merged[key] = merged.TryGetValue(key, out var theirs) ? _Better(ours, theirs) : ours;

      this._entries = merged;
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
        this._removed.Clear();
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

  /// <summary>
  /// A write changed part of a file: its recorded checksums are MARKED stale, not dropped.
  ///
  /// Rehashing on the write path would make every positional write cost a full file read, which is
  /// exactly the pattern a database or a VM image uses. Marking defers that to the scrub, which
  /// re-baselines from one streamed pass. Keeping the entry lets a scan distinguish "never
  /// baselined" from "known dirty", and a file being rewritten continuously is marked once rather
  /// than dirtying the database on every write.
  /// </summary>
  public void InvalidateFile(string normalizedPath) {
    foreach (var database in this._databases.Values)
    foreach (var shadow in new[] { false, true })
      database.MarkStale(PoolPaths.ToPhysical(normalizedPath, shadow));
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
  public IReadOnlyList<IntegrityIssue> ScrubAll(Action<string>? invalidateCaches = null, OperationContext? operation = null)
    => this._Scrub(quick: false, detectOnly: false, invalidateCaches, operation);

  /// <summary>Mount-time delta scan (FR-OOB-MOUNT): only files whose (size, mtime) deviate from the DB are verified.</summary>
  public IReadOnlyList<IntegrityIssue> QuickScan(Action<string>? invalidateCaches = null, OperationContext? operation = null)
    => this._Scrub(quick: true, detectOnly: false, invalidateCaches, operation);

  /// <summary>
  /// Deep detection pass: re-checksums every file (bit-rot, stale copies, conflicts) but never
  /// mutates POOL DATA — health checking repairs nothing unless a fix is asked for.
  ///
  /// It does record a checksum for a file the database does not know yet, and only when every copy
  /// of that file agrees. That is what gives bit-rot detection something to compare against later;
  /// without it a pool that is only ever health-checked never acquires a baseline, and rot stays
  /// indistinguishable from an edit forever.
  /// </summary>
  public IReadOnlyList<IntegrityIssue> DetectAll(OperationContext? operation = null)
    => this._Scrub(quick: false, detectOnly: true, null, operation);

  /// <summary>Cheap detection pass: only files whose metadata deviates (from the DB, or across copies) are verified; never mutates.</summary>
  public IReadOnlyList<IntegrityIssue> DetectQuick(OperationContext? operation = null)
    => this._Scrub(quick: true, detectOnly: true, null, operation);

  // no whole-file Content: the hash is computed by streaming, repair re-streams from the source
  private sealed record CopyView(IVolumeIO Member, bool Shadow, long Size, long MTimeTicks, ChecksumEntry? Entry, string Hash) {
    public bool MatchesEntry => this.Entry != null && this.Hash == this.Entry.Hash;
    public bool MetaMatchesEntry => this.Entry != null && this.Size == this.Entry.Size && this.MTimeTicks == this.Entry.MTimeTicks;
  }

  private IReadOnlyList<IntegrityIssue> _Scrub(bool quick, bool detectOnly, Action<string>? invalidateCaches, OperationContext? operation = null) {
    var context = operation ?? OperationContext.None;
    var issues = new List<IntegrityIssue>();

    // Materialised so the progress has a REAL denominator. The walk is metadata only; on a deep
    // scrub, which re-checksums every byte, it is noise next to the hashing, and a total that grew
    // as the run went would make the bar meaningless.
    context.Phase("listing the pool");
    var paths = this._AllLogicalPaths().ToArray();
    long done = 0;

    try {
      foreach (var path in paths) {
        // between files, never inside one: a scrub interrupted mid-repair is the torn state it
        // exists to fix
        context.ThrowIfStopping();
        context.Step(done++, paths.Length, path);

        var views = this._CollectCopies(path, quick);
        if (views == null)
          continue; // quick scan: nothing deviates

        this._ClassifyAndReconcile(path, views, issues, invalidateCaches, detectOnly);
      }

      context.Step(done, paths.Length, "done");
    } finally {
      // Saved even after a detect-only pass, and even after a CANCELLED one: the pass may have
      // baselined files the database did not know, and a baseline that is not persisted is no
      // baseline at all — the next scan would start from nothing. Cancelling a scrub means "stop
      // here", not "throw away what you learned on the way".
      this.SaveAll();
    }

    return issues;
  }

  private IEnumerable<string> _AllLogicalPaths() {
    var seen = new HashSet<string>(PoolPaths.PathComparer);
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

      // Which recorded checksums may be believed at all.
      //
      // Two ways an entry stops describing the file it names, and BOTH have to be untrusted:
      //
      //  - the engine saw a write and flagged it (Stale), or
      //  - the file's size or mtime no longer match what the entry recorded, which means the
      //    content changed WITHOUT the engine seeing it — an external edit, a restored backup, a
      //    sync tool. The pool never had the chance to mark that one, so it must be inferred here.
      //
      // The second case is the subtle one: the entry looks perfectly fresh, and its hash is simply
      // wrong. Believing it would classify a legitimately edited file as damaged. The entry is
      // therefore flagged in the database as well as ignored for this pass, so the conclusion is
      // recorded rather than re-derived by every later scan — and so a scan interrupted before it
      // reconciles does not leave a hash behind that still looks authoritative.
      var physical = PoolPaths.ToPhysical(path, shadow);
      var recorded = this._Db(member).Get(physical);
      var describesThisFile = recorded != null
        && recorded.Size == found.Length
        && recorded.MTimeTicks == found.LastWriteTimeUtc.Ticks;

      // Recorded, but NOT withheld from this pass. The entry is exactly what distinguishes "this
      // copy changed behind our back" from "we never had a baseline", and that distinction is what
      // preserves a conflict: two copies edited externally to different content are kept for the
      // user, where copy-versus-copy comparison alone would pick the newer timestamp and overwrite
      // the other. Withholding it turned a preserved conflict into a silent resolution — measured,
      // not theorised, by `Scrub_GivenDivergentEditsOnBothCopies_...`.
      if (recorded is { Stale: false } && !describesThisFile)
        this._Db(member).MarkStale(physical);

      metas.Add((member, shadow, found, recorded is { Stale: false } ? recorded : null));
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

        // No divergence — baseline anything the DB does not know yet (streamed re-hash).
        //
        // This happens on a DETECT-ONLY pass too, and deliberately. Every classification above
        // turns on a copy having a recorded checksum: without one, rot cannot be told apart from an
        // edit, and two copies that rot identically look like agreement. Checksums for ordinary
        // files are not written by the write path, so if a read-only health check also declined to
        // record them, a pool that was only ever health-checked would never acquire a baseline and
        // bit-rot detection could never start working. Recording here is unambiguous — it happens
        // only when every copy of the file agrees, which is the one moment the content is not in
        // question — and it touches the checksum sidecar, never pool data.
        // this rewrites stale entries too — RecordHash stores a fresh, trusted one
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
