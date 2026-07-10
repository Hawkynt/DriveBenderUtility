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

  private long _uniquifier;

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  private static string _BaseTrashPathFor(string normalizedPath) => $"{TrashPrefix}/{normalizedPath}";
  private static string _InfoPathFor(string trashPath) => trashPath + ".trashinfo";

  /// <summary>
  /// A unique trash destination for this deletion: the original path plus a monotonic token,
  /// so deleting → recreating → deleting the same path keeps BOTH versions restorable instead
  /// of the newer copy overwriting (or being dropped in favour of) the older one (SAFE-NOLOSS).
  /// </summary>
  private string _NewTrashPathFor(string normalizedPath) {
    var token = clock().Ticks ^ (Interlocked.Increment(ref this._uniquifier) << 20);
    return $"{_BaseTrashPathFor(normalizedPath)}.{token:x}.trashver";
  }

  /// <summary>
  /// Moves every copy of a file into the trash: the primary is renamed into its member's
  /// trash tree; shadow copies are dropped when <paramref name="dropDuplicates"/> is set
  /// (space relief — the item stays restorable from the single kept copy). The trash
  /// destination is unique per deletion, so an earlier trashed version of the same path is
  /// never destroyed by this one.
  /// </summary>
  public void MoveToTrash(string normalizedPath, IReadOnlyList<PhysicalCopy> copies, bool dropDuplicates) {
    var trashPath = _NewTrashPathFor(normalizedPath);
    var sequence = journal.LogIntent(JournalOp.TrashMove, normalizedPath, trashPath);

    var kept = 0;
    foreach (var copy in copies) {
      // space relief: with dropDuplicatesInTrash only one restorable copy is kept (§6.14).
      // Never hard-delete a copy while it is still the ONLY one trashed (kept == 0): that would
      // destroy the just-deleted content instead of preserving it.
      if (dropDuplicates && kept >= 1) {
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
    // a shadow copy cannot be renamed across the shadow/primary namespace in one step: stage +
    // publish, STREAMED so a multi-GB shadow copy never lands in RAM (SAFE-BIGFILE)
    WholeFilePublisher.CopyBetween(member, normalizedPath, true, member, trashPath, false);
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
    // one logical entry per original path; the NEWEST trashed version represents it (older
    // versions are still on disk and purged by age, but Restore hands back the newest)
    var entries = new Dictionary<string, TrashEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var member in this._Online)
    foreach (var (trashPath, info) in this._EntriesOn(member)) {
      var length = member.Stat(trashPath, false)?.Length ?? 0;
      if (!entries.TryGetValue(info.OriginalPath, out var existing) || info.DeletedUtc > existing.DeletedUtc)
        entries[info.OriginalPath] = new(info.OriginalPath, info.DeletedUtc, length, member.MemberId);
    }

    return [.. entries.Values.OrderBy(e => e.DeletedUtc)];
  }

  /// <summary>Every on-disk trash version of a given original path, across members (for restore/purge by original path).</summary>
  private IEnumerable<(IVolumeIO member, string trashPath, TrashInfo info)> _VersionsOf(string normalizedOriginal) {
    foreach (var member in this._Online)
    foreach (var (trashPath, info) in this._EntriesOn(member))
      if (info.OriginalPath.Equals(normalizedOriginal, StringComparison.OrdinalIgnoreCase))
        yield return (member, trashPath, info);
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

  /// <summary>Restores the NEWEST trashed version to its original path; the engine re-establishes duplication afterwards.</summary>
  public (IVolumeIO member, string restoredPath)? Restore(string originalPath) {
    var normalized = PoolPaths.Normalize(originalPath);
    var newest = this._VersionsOf(normalized).OrderByDescending(v => v.info.DeletedUtc).FirstOrDefault();
    if (newest.member == null)
      return null;

    var (member, trashPath, _) = newest;
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
    // purge the OLDEST version of this path (List/Purge iterate oldest-first); each version has a
    // unique trash path, so removing them one at a time frees space without touching newer copies
    var normalized = PoolPaths.Normalize(originalPath);
    var oldest = this._VersionsOf(normalized).OrderBy(v => v.info.DeletedUtc).FirstOrDefault();
    if (oldest.member == null)
      return;

    var (member, trashPath, _) = oldest;
    if (member.FileExists(trashPath, false))
      member.Delete(trashPath, false);
    if (member.FileExists(_InfoPathFor(trashPath), false))
      member.Delete(_InfoPathFor(trashPath), false);
  }

}
