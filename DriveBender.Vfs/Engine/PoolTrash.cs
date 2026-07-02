using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisonM.Vfs.Engine;

public sealed record TrashEntry(string OriginalPath, DateTime DeletedUtc, long Length, Guid MemberId);

internal sealed record TrashInfo {
  [JsonPropertyName("originalPath")] public required string OriginalPath { get; init; }
  [JsonPropertyName("deletedUtc")] public required DateTime DeletedUtc { get; init; }
}

/// <summary>
/// Optional per-pool trash (CMP-TRASH, FR-TRASH): deletes move items under the hidden
/// .drivebenderutility/trash tree (same member, cheap rename) instead of purging all
/// copies, so they stay recoverable until retention or the size cap purges them
/// oldest-first. Moves are journalled (SAFE-WAL).
/// </summary>
public sealed class PoolTrash(IReadOnlyList<IVolumeIO> members, Journal journal, Func<DateTime> clock) {

  public const string TrashPrefix = PoolPaths.UtilityFolderName + "/trash";

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  private static string _TrashPathFor(string normalizedPath) => $"{TrashPrefix}/{normalizedPath}";
  private static string _InfoPathFor(string trashPath) => trashPath + ".trashinfo";

  /// <summary>
  /// Moves every copy of a file into the trash: the primary is renamed into its member's
  /// trash tree; shadow copies are dropped when <paramref name="dropDuplicates"/> is set
  /// (space relief — the item stays restorable from the single kept copy).
  /// </summary>
  public void MoveToTrash(string normalizedPath, IReadOnlyList<PhysicalCopy> copies, bool dropDuplicates) {
    var trashPath = _TrashPathFor(normalizedPath);
    var sequence = journal.LogIntent(JournalOp.TrashMove, normalizedPath, trashPath);

    var kept = 0;
    foreach (var copy in copies) {
      // space relief: with dropDuplicatesInTrash only one restorable copy is kept (§6.14)
      if (dropDuplicates && kept >= 1 || copy.Volume.FileExists(trashPath, false)) {
        copy.Volume.Delete(normalizedPath, copy.Shadow);
        continue;
      }

      // same-member move into the trash tree (cheap rename); shadow copies flatten into a primary-style trash copy
      copy.Volume.EnsureFolder(PoolPaths.GetParent(trashPath), false);
      if (copy.Shadow)
        this._MoveShadowIntoTrash(copy.Volume, normalizedPath, trashPath);
      else
        copy.Volume.AtomicReplace(normalizedPath, trashPath, false);

      this._WriteInfo(copy.Volume, trashPath, normalizedPath);
      ++kept;
    }

    journal.Complete(sequence, JournalOp.TrashMove);
  }

  private void _MoveShadowIntoTrash(IVolumeIO member, string normalizedPath, string trashPath) {
    // a shadow copy cannot be renamed across the shadow/primary namespace in one step: stage + publish
    byte[] content;
    using (var source = member.OpenRead(normalizedPath, true)) {
      using var buffer = new MemoryStream();
      source.CopyTo(buffer);
      content = buffer.ToArray();
    }

    var temp = trashPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
    using (var stream = member.OpenWrite(temp, false, true)) {
      stream.Write(content, 0, content.Length);
      stream.Flush();
    }

    member.AtomicReplace(temp, trashPath, false);
    member.Delete(normalizedPath, true);
  }

  private void _WriteInfo(IVolumeIO member, string trashPath, string originalPath) {
    var info = JsonSerializer.Serialize(new TrashInfo { OriginalPath = originalPath, DeletedUtc = clock() });
    var bytes = Encoding.UTF8.GetBytes(info);
    using var stream = member.OpenWrite(_InfoPathFor(trashPath), false, true);
    stream.SetLength(0);
    stream.Write(bytes, 0, bytes.Length);
    stream.Flush();
  }

  public IReadOnlyList<TrashEntry> List() {
    var entries = new Dictionary<string, TrashEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var member in this._Online)
    foreach (var (trashPath, info) in this._EntriesOn(member)) {
      var length = member.Stat(trashPath, false)?.Length ?? 0;
      if (!entries.ContainsKey(info.OriginalPath))
        entries.Add(info.OriginalPath, new(info.OriginalPath, info.DeletedUtc, length, member.MemberId));
    }

    return [.. entries.Values.OrderBy(e => e.DeletedUtc)];
  }

  private IEnumerable<(string trashPath, TrashInfo info)> _EntriesOn(IVolumeIO member) {
    var stack = new Stack<string>();
    if (member.FolderExists(TrashPrefix, false))
      stack.Push(TrashPrefix);

    while (stack.Count > 0) {
      var folder = stack.Pop();
      VolumeEntry[] items;
      try {
        items = [.. member.List(folder, false)];
      } catch (PoolFsException) {
        continue;
      }

      foreach (var item in items) {
        var childPath = $"{folder}/{item.Name}";
        if (item.IsDirectory) {
          stack.Push(childPath);
          continue;
        }

        if (!item.Name.EndsWith(".trashinfo", StringComparison.OrdinalIgnoreCase))
          continue;

        TrashInfo? info = null;
        try {
          using var stream = member.OpenRead(childPath, false);
          using var reader = new StreamReader(stream, Encoding.UTF8);
          info = JsonSerializer.Deserialize<TrashInfo>(reader.ReadToEnd());
        } catch (PoolFsException) {
          // unreadable sidecar: skip; purge cleans it up eventually
        } catch (JsonException) {
        }

        if (info != null)
          yield return (childPath[..^".trashinfo".Length], info);
      }
    }
  }

  /// <summary>Restores an item to its original path on the member holding the trash copy; the engine re-establishes duplication afterwards.</summary>
  public (IVolumeIO member, string restoredPath)? Restore(string originalPath) {
    var normalized = PoolPaths.Normalize(originalPath);
    var trashPath = _TrashPathFor(normalized);
    foreach (var member in this._Online) {
      if (!member.FileExists(trashPath, false))
        continue;

      var sequence = journal.LogIntent(JournalOp.TrashMove, trashPath, normalized);
      var parent = PoolPaths.GetParent(normalized);
      if (parent.Length > 0)
        member.EnsureFolder(parent, false);

      member.AtomicReplace(trashPath, normalized, false);
      if (member.FileExists(_InfoPathFor(trashPath), false))
        member.Delete(_InfoPathFor(trashPath), false);

      journal.Complete(sequence, JournalOp.TrashMove);
      return (member, normalized);
    }

    return null;
  }

  /// <summary>
  /// Auto-purge (oldest first): entries beyond <paramref name="retention"/> always go;
  /// further entries go while the trash exceeds <paramref name="maxSizeBytes"/>.
  /// A purge is the real, final delete of all remaining copies.
  /// </summary>
  public int Purge(TimeSpan retention, long maxSizeBytes) {
    var now = clock();
    var entries = this.List();
    var totalBytes = entries.Sum(e => e.Length);
    var purged = 0;

    foreach (var entry in entries) {
      var expired = now - entry.DeletedUtc >= retention;
      var overSize = totalBytes > maxSizeBytes;
      if (!expired && !overSize)
        break; // entries are oldest-first; nothing further qualifies

      this._PurgeEntry(entry.OriginalPath);
      totalBytes -= entry.Length;
      ++purged;
    }

    return purged;
  }

  private void _PurgeEntry(string originalPath) {
    var trashPath = _TrashPathFor(PoolPaths.Normalize(originalPath));
    foreach (var member in this._Online) {
      if (member.FileExists(trashPath, false))
        member.Delete(trashPath, false);
      if (member.FileExists(_InfoPathFor(trashPath), false))
        member.Delete(_InfoPathFor(trashPath), false);
    }
  }

}
