namespace DivisonM.Vfs.Engine;

public sealed record RecoveryReport(int RolledForward, int Reconciled, int TempsRemoved) {
  public bool AnythingDone => this.RolledForward + this.Reconciled + this.TempsRemoved > 0;
}

/// <summary>
/// Crash recovery on mount (FR-RECOVER): replays the journal — rolls forward
/// completed-but-unacked operations, reconciles copies touched by interrupted writes,
/// removes orphaned *.TEMP.$DRIVEBENDER staging files — and is safe to run any number of
/// times (SAFE-IDEMP). No acknowledged write is lost (SAFE-NOLOSS): an ack only ever
/// happened after data was durable on the required copies, which replay never destroys.
/// </summary>
/// <param name="admit">
/// Charges a member for a chunk of copying, blocking until its limits allow it; null for unmetered.
/// Recovery resync moves whole files like any heal does, and an operator who capped a member did
/// not mean "except on mount".
/// </param>
/// <param name="snapshots">
/// The snapshot store, so an aside the power cut in half can be finished rather than abandoned.
/// Null for a pool without one, in which case a stranded version is reported and left alone.
/// </param>
public sealed class PoolRecovery(IReadOnlyList<IVolumeIO> members, Journal journal, Action<IVolumeIO, long>? admit = null,
  PoolSnapshots? snapshots = null) {

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  public RecoveryReport Run() {
    var rolledForward = 0;
    var reconciled = 0;

    // In SEQUENCE order, not file order: records can reach the journal out of order under group
    // commit, and replaying two writes to one range backwards would leave the older bytes on top.
    var incomplete = journal.ReadIncomplete().OrderBy(i => i.Sequence).ToArray();

    // Writes that name the copy which took their acknowledgement are replayed first, per file, by
    // byte range (see _ReplayRanges), and then completed like everything else below.
    foreach (var file in incomplete.Where(_NamesItsCopy).GroupBy(i => i.Path!, PoolPaths.PathComparer))
      reconciled += this._ReplayRanges(file.Key, [.. file]) ? 1 : 0;

    foreach (var intent in incomplete) {
      switch (intent.Op) {
        case JournalOp.Write when _NamesItsCopy(intent):
          break; // replayed by range above

        case JournalOp.Delete when intent.Path != null:
          // roll forward: some copies may already be gone; remove the rest (FR-DELETE)
          if (this._WouldDestroyNewerContent(intent, intent.Path))
            break;

          rolledForward += this._DeleteAllCopies(intent.Path) ? 1 : 0;
          break;

        case JournalOp.Rename or JournalOp.TrashMove when intent is { Path: not null, TargetPath: not null }:
          if (this._WouldDestroyNewerContent(intent, intent.Path))
            break;

          rolledForward += this._RollForwardRename(intent.Path, intent.TargetPath) ? 1 : 0;
          break;

        case JournalOp.Write or JournalOp.Truncate or JournalOp.Create or JournalOp.ShadowCreate or JournalOp.Drain when intent.Path != null:
          // copies may diverge mid-operation; resync every copy from the authoritative primary
          reconciled += this._ResyncCopies(intent.Path) ? 1 : 0;
          break;

        case JournalOp.SnapshotAside when intent is { Path: not null, TargetPath: not null }:
          rolledForward += this._ResolveInterruptedAside(intent.Path, intent.TargetPath) ? 1 : 0;
          break;

        case JournalOp.RemoveDir when intent.Path != null:
          rolledForward += this._RemoveDirEverywhere(intent.Path) ? 1 : 0;
          break;

        // MakeDir: an interrupted mkdir left either nothing or a valid empty folder — both consistent
      }

      journal.Complete(intent.Sequence, intent.Op);
    }

    var tempsRemoved = this._RemoveOrphanedTemps();
    journal.Checkpoint();
    return new(rolledForward, reconciled, tempsRemoved);
  }

  /// <summary>
  /// Whether rolling this intent forward would destroy content that CANNOT be what it was about.
  ///
  /// An intent is written before the operation and completed after it, so a crash can leave one
  /// behind that genuinely needs finishing. A journal can also arrive from somewhere else entirely:
  /// a member restored from a backup, a hidden folder copied across, an old <c>.drivebenderutility</c>
  /// put back by hand. Then it describes work that finished long ago, against a path that has since
  /// been recreated — and finishing it deletes a file nobody asked to delete, with the recovery
  /// machinery itself as the cause. That is the worst possible source of data loss.
  ///
  /// The two are told apart by time. In a real crash the file was written BEFORE the delete was
  /// logged, so its mtime is older than the intent. A file whose mtime is NEWER than the intent was
  /// written after the operation was recorded and therefore cannot be the content the operation was
  /// about; destroying it would finish an operation that already happened to something else.
  ///
  /// An intent with no timestamp — written by an older version, or forged — proves nothing about
  /// when it was made, so it never gets to destroy anything. Declining costs an unacknowledged
  /// operation that the caller was never told had succeeded; proceeding costs the file.
  /// </summary>
  private bool _WouldDestroyNewerContent(JournalRecord intent, string path) {
    var newest = DateTime.MinValue;
    foreach (var member in this._Online)
      foreach (var shadow in new[] { false, true })
        if (member.FileExists(path, shadow) && member.Stat(path, shadow) is { } meta && meta.LastWriteTimeUtc > newest)
          newest = meta.LastWriteTimeUtc;

    if (newest == DateTime.MinValue)
      return false; // nothing there to destroy; rolling forward is a no-op either way

    if (intent.LoggedUtc > DateTime.MinValue && newest <= intent.LoggedUtc)
      return false; // older than the intent: this really is the file the intent was about

    DriveBender.Logger(
      $"[Warning]Journal intent #{intent.Sequence} ({intent.Op} '{path}') is not being rolled forward: "
      + $"the file there was written {(intent.LoggedUtc > DateTime.MinValue ? $"at {newest:u}, after the intent was logged at {intent.LoggedUtc:u}" : $"at {newest:u} and the intent carries no timestamp")}. "
      + "A journal describing work already done — a restored disk, a copied-back hidden folder — must "
      + "not delete a file that is here now.");

    return true;
  }

  /// <summary>
  /// Finishes an aside a crash caught between its two halves.
  ///
  /// Preserving a snapshot's view of a file RENAMES the live file into the store and then writes the
  /// sidecar naming it. In between, on a pool keeping one copy, the file is at neither place the
  /// pool looks: not at its path, and not yet anything the store can see. Recovery used to have no
  /// case for this at all — the intent fell through the switch and was marked complete — so the file
  /// was simply gone, which is the one outcome this pool promises cannot happen.
  ///
  /// Which way to finish depends on whether the write that PROMPTED the aside landed, because the
  /// aside runs first:
  ///
  ///  - the original path is empty, so that write never happened. The aside had no reason to stand,
  ///    and rolling it BACK restores exactly the state before the crash. This is the same instinct
  ///    the rename roll-forward already has: an intent that never took effect leaves the source
  ///    authoritative.
  ///  - the original path is occupied, so the replacing write did land and the pool acknowledged it.
  ///    Renaming the version back over that would destroy content the pool promised in order to
  ///    rescue content it only promised a snapshot. The version is adopted instead, and both survive.
  /// </summary>
  private bool _ResolveInterruptedAside(string originalPath, string versionPath) {
    var holder = this._Online.FirstOrDefault(m => m.FileExists(versionPath, false));
    if (holder == null)
      return false; // the rename never happened, or the aside finished and this is already tidy

    if (holder.FileExists(PoolSnapshots.InfoPathFor(versionPath), false))
      return false; // both halves are on disk; nothing was interrupted

    if (this._Online.Any(m => m.FileExists(originalPath, false) || m.FileExists(originalPath, true))) {
      if (snapshots == null) {
        DriveBender.Logger(
          $"[Warning]A snapshot version of '{originalPath}' was stranded by an interrupted aside and "
          + $"cannot be adopted without the snapshot store; it stays on '{holder.DisplayName}' as "
          + $"'{versionPath}' and no snapshot can see it.");
        return false;
      }

      snapshots.AdoptStrandedVersion(holder, originalPath, versionPath);
      DriveBender.Logger($" - Adopted the snapshot version of '{originalPath}' that an interrupted aside left unnamed");
      return true;
    }

    var parent = PoolPaths.GetParent(originalPath);
    if (parent.Length > 0)
      holder.EnsureFolder(parent, false);

    holder.AtomicReplace(versionPath, originalPath, false);
    DriveBender.Logger($" - Rolled back an interrupted aside: '{originalPath}' is back where it was");
    return true;
  }

  private bool _DeleteAllCopies(string path) {
    var any = false;
    foreach (var member in this._Online) {
      foreach (var shadow in new[] { false, true })
        if (member.FileExists(path, shadow)) {
          member.Delete(path, shadow);
          any = true;
        }
    }

    return any;
  }

  /// <summary>Whether two files on one member hold the same bytes; false when either cannot be read.</summary>
  private static bool _SameContent(IVolumeIO member, string a, string b, bool shadow) {
    try {
      if (member.Stat(a, shadow)?.Length != member.Stat(b, shadow)?.Length)
        return false;

      using var left = member.OpenRead(a, shadow);
      using var right = member.OpenRead(b, shadow);
      var bufferA = new byte[1 << 16];
      var bufferB = new byte[1 << 16];
      while (true) {
        var readA = left.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
        var readB = right.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
        if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
          return false;
        if (readA == 0)
          return true;
      }
    } catch (PoolFsException) {
      return false;
    }
  }

  private bool _RollForwardRename(string from, string to) {
    // folder rename: some members may have flipped before the crash — finish the rest the same way
    if (this._Online.Any(m => m.FolderExists(to, false)) && !this._Online.Any(m => m.FileExists(to, false) || m.FileExists(to, true))) {
      var movedFolders = false;
      foreach (var member in this._Online.Where(m => m.FolderExists(from, false) && !m.FolderExists(to, false))) {
        var toParent = PoolPaths.GetParent(to);
        if (toParent.Length > 0)
          member.EnsureFolder(toParent, false);

        member.RenameFolder(from, to);
        movedFolders = true;
      }

      return movedFolders;
    }

    var targetExists = this._Online.Any(m => m.FileExists(to, false) || m.FileExists(to, true));
    if (!targetExists)
      return false; // nothing moved yet — the intent never took effect; source stays authoritative

    var moved = false;
    foreach (var member in this._Online)
    foreach (var shadow in new[] { false, true }) {
      if (!member.FileExists(from, shadow))
        continue;

      if (member.FileExists(to, shadow) && _SameContent(member, from, to, shadow)) {
        // both sides present and alike: a copy-based move got as far as the copy; the source is the leftover
        member.Delete(from, shadow);
      } else if (member.FileExists(to, shadow)) {
        // both sides present and DIFFERENT: a rename over an existing file had not reached this member
        // yet. Deleting the source here used to throw away the very content being renamed, and leave
        // the old file under the name; the move is finished instead, as it was asked for.
        member.AtomicReplace(from, to, shadow);
      } else {
        var parent = PoolPaths.GetParent(to);
        if (parent.Length > 0)
          member.EnsureFolder(parent, false);
        if (shadow)
          member.EnsureFolder(parent, true);

        member.AtomicReplace(from, to, shadow);
      }

      moved = true;
    }

    return moved;
  }

  /// <summary>
  /// Converges every copy of a path to the authoritative one after an interrupted write
  /// (SAFE-DUP / SAFE-NOLOSS). The source is the NEWEST copy by mtime — NOT "the first
  /// primary" — because the ack quorum can land the only fresh block on a readiness-selected
  /// shadow, so a stale primary must never win. Works with no surviving primary (shadow-only)
  /// and streams the source so a multi-GB file never lands in RAM (SAFE-BIGFILE).
  /// </summary>
  private static bool _NamesItsCopy(JournalRecord intent)
    => intent is { Op: JournalOp.Write, Path: not null, Length: > 0 } && intent.MemberId != Guid.Empty;

  /// <summary>
  /// Replays a file's interrupted writes by byte range: each write's bytes are carried FROM the
  /// copy that took its acknowledgement TO every copy of the file.
  ///
  /// Whole-file resync picks one copy as the truth, which is only right when that copy holds every
  /// acknowledged write. With fewer required copies than copies, acknowledgements rotate between
  /// copies block by block, so after a power cut each copy can hold acknowledged bytes the others
  /// lack — and a crash scenario lost one that way.
  ///
  /// Two phases, deliberately. Every range is READ before any is written, because replaying one at
  /// a time reads a source an earlier step of the same replay has already overwritten: a newer wide
  /// write on one copy then gets the older narrow write from the other copy put back on top of it.
  /// Then each range is applied, oldest first, to EVERY copy including the one it came from, so a
  /// source that an earlier range touched is set right again by the newer one. Where a write's copy
  /// is gone, nothing can say which bytes are right, and the whole-file rule is the fallback.
  /// </summary>
  private bool _ReplayRanges(string path, IReadOnlyList<JournalRecord> writes) {
    var captured = new List<(long offset, byte[] bytes)>(writes.Count);
    foreach (var write in writes) {
      var source = this._Online.FirstOrDefault(m => m.MemberId == write.MemberId);
      if (source == null || !source.FileExists(path, write.Shadow))
        return this._ResyncCopies(path);

      try {
        using var stream = source.OpenRead(path, write.Shadow);
        if (write.Offset >= stream.Length)
          continue; // truncated past this write since — nothing of it survives to carry

        var bytes = new byte[(int)Math.Min(write.Length, stream.Length - write.Offset)];
        stream.Seek(write.Offset, SeekOrigin.Begin);
        stream.ReadExactly(bytes);
        captured.Add((write.Offset, bytes));
      } catch (PoolFsException) {
        return this._ResyncCopies(path);
      }
    }

    var changed = false;
    foreach (var member in this._Online)
    foreach (var shadow in new[] { false, true }) {
      if (!member.FileExists(path, shadow))
        continue;

      WholeFilePublisher.SeparateIfLinked(member, path, shadow); // a replay must not write through a link into another file
      using var stream = member.OpenWrite(path, shadow, false);
      foreach (var (offset, bytes) in captured) {
        stream.Seek(offset, SeekOrigin.Begin);
        stream.Write(bytes, 0, bytes.Length);
      }

      stream.Flush(); // durable before the intents are completed
      changed = true;
    }

    return changed;
  }

  private bool _ResyncCopies(string path) {
    var copies = new List<(IVolumeIO member, bool shadow, FileMeta meta, string hash)>();
    foreach (var member in this._Online)
    foreach (var shadow in new[] { false, true }) {
      if (!member.FileExists(path, shadow))
        continue;

      var meta = member.Stat(path, shadow);
      if (meta is not { } found || found.IsDirectory)
        continue;

      string hash;
      try {
        using var stream = member.OpenRead(path, shadow);
        hash = ChecksumDatabase.HashOf(stream); // streamed
      } catch (PoolFsException) {
        continue;
      }

      copies.Add((member, shadow, found, hash));
    }

    if (copies.Count < 2)
      return false;

    // newest wins; a tie in mtime keeps whichever content the majority already holds (no needless rewrite)
    var winner = copies
      .OrderByDescending(c => c.meta.LastWriteTimeUtc.Ticks)
      .ThenByDescending(c => copies.Count(o => o.hash == c.hash))
      .First();

    var changed = false;
    foreach (var (member, shadow, _, hash) in copies) {
      if (hash == winner.hash)
        continue; // already converged

      // temp + atomic rename where supported, put-and-verify emulation otherwise (SAFE-ATOMIC, FR-CAP-ADAPT)
      WholeFilePublisher.CopyBetween(winner.member, path, winner.shadow, member, path, shadow,
        admit: WholeFilePublisher.Pace(admit, winner.member, member));
      changed = true;
    }

    return changed;
  }

  private bool _RemoveDirEverywhere(string path) {
    var any = false;
    foreach (var member in this._Online) {
      if (member.FolderExists(path, true)) {
        try {
          member.DeleteFolder(path, true);
          any = true;
        } catch (PoolFsException e) when (e.Error == PoolFsError.NotEmpty) {
          return false; // content re-appeared — do not roll forward a destructive op over data
        }
      }

      if (member.FolderExists(path, false)) {
        try {
          member.DeleteFolder(path, false);
          any = true;
        } catch (PoolFsException e) when (e.Error == PoolFsError.NotEmpty) {
          return false;
        }
      }
    }

    return any;
  }

  /// <summary>Deletes orphaned *.TEMP.$DRIVEBENDER staging files left by interrupted publications.</summary>
  private int _RemoveOrphanedTemps() {
    var removed = 0;
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
            stack.Push(childPath);
            continue;
          }

          if (!entry.Name.EndsWith("." + DriveBender.DriveBenderConstants.TEMP_EXTENSION, StringComparison.OrdinalIgnoreCase))
            continue;

          // the journal itself rewrites through a temp file — never treat an in-flight rewrite as orphaned
          if (childPath.Equals(MemberJournalStore.JournalPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION, StringComparison.OrdinalIgnoreCase))
            continue;

          try {
            member.Delete(childPath, false);
            ++removed;
            DriveBender.Logger($" - Removed orphaned staging file '{childPath}' on '{member.DisplayName}'");
          } catch (PoolFsException) {
            // best effort; the next mount retries
          }
        }
      }
    }

    return removed;
  }

}
