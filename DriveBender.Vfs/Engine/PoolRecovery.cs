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
public sealed class PoolRecovery(IReadOnlyList<IVolumeIO> members, Journal journal) {

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  public RecoveryReport Run() {
    var rolledForward = 0;
    var reconciled = 0;

    foreach (var intent in journal.ReadIncomplete()) {
      switch (intent.Op) {
        case JournalOp.Delete when intent.Path != null:
          // roll forward: some copies may already be gone; remove the rest (FR-DELETE)
          rolledForward += this._DeleteAllCopies(intent.Path) ? 1 : 0;
          break;

        case JournalOp.Rename or JournalOp.TrashMove when intent is { Path: not null, TargetPath: not null }:
          rolledForward += this._RollForwardRename(intent.Path, intent.TargetPath) ? 1 : 0;
          break;

        case JournalOp.Write or JournalOp.Truncate or JournalOp.Create or JournalOp.ShadowCreate or JournalOp.Drain when intent.Path != null:
          // copies may diverge mid-operation; resync every copy from the authoritative primary
          reconciled += this._ResyncCopies(intent.Path) ? 1 : 0;
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

      if (member.FileExists(to, shadow)) {
        // both sides present on this member: the move happened elsewhere; the leftover source is stale
        member.Delete(from, shadow);
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
      WholeFilePublisher.CopyBetween(winner.member, path, winner.shadow, member, path, shadow);
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
