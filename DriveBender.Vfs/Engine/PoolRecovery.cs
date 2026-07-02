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

  /// <summary>Copies the authoritative primary over every other copy so all copies converge (SAFE-DUP).</summary>
  private bool _ResyncCopies(string path) {
    var copies = new List<(IVolumeIO member, bool shadow)>();
    foreach (var member in this._Online) {
      if (member.FileExists(path, false))
        copies.Add((member, false));
      if (member.FileExists(path, true))
        copies.Add((member, true));
    }

    if (copies.Count < 2)
      return false;

    var (sourceMember, sourceShadow) = copies.First(c => !c.shadow);
    byte[] content;
    using (var stream = sourceMember.OpenRead(path, sourceShadow)) {
      using var buffer = new MemoryStream();
      stream.CopyTo(buffer);
      content = buffer.ToArray();
    }

    var changed = false;
    foreach (var (member, shadow) in copies) {
      if (member == sourceMember && shadow == sourceShadow)
        continue;

      if (this._ContentEquals(member, path, shadow, content))
        continue;

      // publish via temp + atomic rename — never a torn overwrite (SAFE-ATOMIC)
      var temp = path + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
      using (var stream = member.OpenWrite(temp, shadow, true)) {
        stream.Write(content, 0, content.Length);
        stream.Flush();
      }

      member.AtomicReplace(temp, path, shadow);
      changed = true;
    }

    return changed;
  }

  private bool _ContentEquals(IVolumeIO member, string path, bool shadow, byte[] expected) {
    var meta = member.Stat(path, shadow);
    if (meta == null || meta.Value.Length != expected.Length)
      return false;

    using var stream = member.OpenRead(path, shadow);
    var actual = new byte[expected.Length];
    var total = 0;
    while (total < actual.Length) {
      var read = stream.Read(actual, total, actual.Length - total);
      if (read == 0)
        break;

      total += read;
    }

    return total == expected.Length && actual.AsSpan().SequenceEqual(expected);
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
