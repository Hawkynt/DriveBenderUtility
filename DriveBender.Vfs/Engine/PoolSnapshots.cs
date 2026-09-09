using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisonM.Vfs.Engine;

/// <summary>One snapshot as the outside world sees it.</summary>
public sealed record SnapshotEntry(Guid Id, string Name, DateTime CreatedUtc, int Files);

/// <summary>What a snapshot recorded, mirrored onto every member that was online when it was taken.</summary>
public sealed class SnapshotIndex {
  [JsonPropertyName("id")] public required Guid Id { get; init; }
  [JsonPropertyName("name")] public required string Name { get; init; }
  [JsonPropertyName("createdUtc")] public required DateTime CreatedUtc { get; init; }

  /// <summary>
  /// Every logical path the pool held at that instant.
  ///
  /// Written out in full rather than derived, because the alternatives are worse. Deriving the
  /// namespace from what exists NOW plus what has been set aside since cannot tell a file created
  /// after the snapshot from one unchanged since it, and the only thing that could — a creation
  /// timestamp — is metadata the pool carries but does not own. This is the cost the design
  /// accepts: proportional to the number of files, paid once, and no data is copied.
  /// </summary>
  [JsonPropertyName("paths")] public required string[] Paths { get; init; }
}

/// <summary>The pins on one set-aside version: which snapshots still need it.</summary>
public sealed class SnapshotVersionInfo {
  [JsonPropertyName("originalPath")] public required string OriginalPath { get; init; }
  [JsonPropertyName("asidedUtc")] public required DateTime AsidedUtc { get; init; }
  [JsonPropertyName("pins")] public required Guid[] Pins { get; init; }
}

/// <summary>
/// Snapshots over a whole-file pool (docs/Snapshots.md).
///
/// A snapshot copies no data. It records the namespace, and from then on the pool must stop
/// destroying any version those paths still point at. The version that gets preserved is the file
/// ITSELF: when a pinned path is about to be overwritten or deleted, the live file is RENAMED into
/// this store and the new content takes its place. Two renames on one filesystem, no bytes moved —
/// which is only possible because the engine already publishes a written file by renaming a staged
/// temp over it, and this hangs off that same moment.
///
/// Versions are ordered by the token in their name, which is the tick count at the instant they
/// were set aside. Reading path P as of snapshot S is then "the earliest version of P set aside
/// after S was taken", or the live file when there is none — so a file nobody has touched since the
/// snapshot costs nothing at all, which is most of a backup pool.
/// </summary>
public sealed class PoolSnapshots(IReadOnlyList<IVolumeIO> members, Journal journal, Func<DateTime> clock,
  Action<IVolumeIO, long>? admit = null) {

  public const string SnapshotPrefix = PoolPaths.UtilityFolderName + "/snapshots";

  private const string _INDEX_FOLDER = SnapshotPrefix + "/index";
  private const string _VERSION_FOLDER = SnapshotPrefix + "/versions";
  private const string _VERSION_SUFFIX = ".snapver";
  private const string _INFO_SUFFIX = ".snapinfo";

  private long _uniquifier;

  private IEnumerable<IVolumeIO> _Online => members.Where(m => m.IsOnline);

  private static string _IndexPathFor(Guid id) => $"{_INDEX_FOLDER}/{id:D}.json";
  private static string _InfoPathFor(string versionPath) => versionPath + _INFO_SUFFIX;

  /// <summary>
  /// A version destination that sorts by WHEN it was set aside.
  ///
  /// The token is the aside instant, so "the earliest version after snapshot S" is a comparison
  /// rather than a lookup, and two asides of the same path in the same tick still get distinct
  /// names. Hex so the name sorts the same way the number does.
  /// </summary>
  private string _NewVersionPathFor(string normalizedPath) {
    var token = clock().Ticks + (Interlocked.Increment(ref this._uniquifier) & 0xFFFF);
    return $"{_VERSION_FOLDER}/{normalizedPath}.{token:x16}{_VERSION_SUFFIX}";
  }

  private static long _TokenOf(string versionPath) {
    var name = versionPath[..^_VERSION_SUFFIX.Length];
    var dot = name.LastIndexOf('.');
    return dot >= 0 && long.TryParse(name[(dot + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var token)
      ? token
      : 0;
  }

  #region taking, listing, deleting

  /// <summary>
  /// Records the pool's namespace as it is now. No data is copied and no file is touched.
  /// </summary>
  public SnapshotEntry Take(string name, IReadOnlyCollection<string> paths) {
    var id = Guid.NewGuid();
    var index = new SnapshotIndex { Id = id, Name = name, CreatedUtc = clock(), Paths = [.. paths] };
    var sequence = journal.LogIntent(JournalOp.SnapshotTake, _IndexPathFor(id));

    // Mirrored onto every online member, like the manifest. A snapshot that lived on one member
    // would be lost by the disk failure the pool exists to survive, and the index is the only part
    // of a snapshot that is not already redundant — the versions inherit the file's duplication.
    var written = 0;
    foreach (var member in this._Online)
      try {
        _WriteJson(member, _IndexPathFor(id), index);
        ++written;
      } catch (PoolFsException e) {
        DriveBender.Logger($"[Warning]Could not mirror snapshot '{name}' onto '{member.DisplayName}': {e.Message}");
      }

    if (written == 0) {
      journal.Complete(sequence, JournalOp.SnapshotTake);
      throw new PoolFsException(PoolFsError.IoError, $"No member accepted the snapshot index for '{name}'");
    }

    journal.Complete(sequence, JournalOp.SnapshotTake);
    DriveBender.Logger($" - Snapshot '{name}' ({id:D}) taken over {paths.Count} path(s)");
    return new(id, name, index.CreatedUtc, paths.Count);
  }

  /// <summary>Every snapshot, oldest first. One logical entry however many members mirror it.</summary>
  public IReadOnlyList<SnapshotEntry> List() {
    var found = new Dictionary<Guid, SnapshotEntry>();
    foreach (var index in this._Indexes())
      found.TryAdd(index.index.Id, new(index.index.Id, index.index.Name, index.index.CreatedUtc, index.index.Paths.Length));

    return [.. found.Values.OrderBy(e => e.CreatedUtc)];
  }

  /// <summary>
  /// Forgets a snapshot and releases the versions only it was holding.
  ///
  /// A version is deleted when the last pin goes, and the pin list lives WITH the version rather
  /// than in a central table — so a member that was offline for this converges when it returns
  /// instead of leaking its copies for good.
  /// </summary>
  public int Delete(Guid id) {
    var sequence = journal.LogIntent(JournalOp.SnapshotDelete, _IndexPathFor(id));
    var released = 0;

    foreach (var (member, versionPath, info) in this._Versions()) {
      if (!info.Pins.Contains(id))
        continue;

      var remaining = info.Pins.Where(p => p != id).ToArray();
      if (remaining.Length > 0) {
        _WriteJson(member, _InfoPathFor(versionPath), new SnapshotVersionInfo {
          OriginalPath = info.OriginalPath,
          AsidedUtc = info.AsidedUtc,
          Pins = remaining,
        });
        continue;
      }

      // nothing needs it any more: the version and its sidecar go
      _TryDelete(member, versionPath);
      _TryDelete(member, _InfoPathFor(versionPath));
      ++released;
    }

    foreach (var member in this._Online)
      _TryDelete(member, _IndexPathFor(id));

    journal.Complete(sequence, JournalOp.SnapshotDelete);
    return released;
  }

  #endregion

  #region preserving

  /// <summary>
  /// Every path that at least one snapshot still points at the LIVE file for.
  ///
  /// This is what the engine checks before letting a write or a delete destroy content, so it has
  /// to be cheap: it is built once at mount and shrinks as versions are set aside. A path leaves
  /// the set the moment it has a version newer than every snapshot that names it.
  /// </summary>
  public HashSet<string> PinnedPaths() {
    var pinned = new HashSet<string>(PoolPaths.PathComparer);
    var snapshots = this.List();
    if (snapshots.Count == 0)
      return pinned;

    foreach (var index in this._Indexes())
      foreach (var path in index.index.Paths)
        pinned.Add(path);

    // a path with a version set aside after the newest snapshot that names it is already preserved
    foreach (var group in this._Versions().GroupBy(v => v.info.OriginalPath, PoolPaths.PathComparer)) {
      var newestAside = group.Max(v => v.info.AsidedUtc);
      var newestSnapshotNaming = snapshots
        .Where(s => this._Names(s.Id, group.Key))
        .Select(s => (DateTime?)s.CreatedUtc)
        .Max();

      if (newestSnapshotNaming is { } taken && newestAside > taken)
        pinned.Remove(group.Key);
    }

    return pinned;
  }

  /// <summary>
  /// Moves one copy of a pinned file into the store, so the content survives what is about to
  /// happen to its name. A RENAME on the member that holds it — no bytes are read or written.
  /// </summary>
  public string Aside(IVolumeIO member, string normalizedPath, bool shadow, IReadOnlyCollection<Guid> pins) {
    var versionPath = this._NewVersionPathFor(normalizedPath);
    var sequence = journal.LogIntent(JournalOp.SnapshotAside, normalizedPath, versionPath);

    member.EnsureFolder(PoolPaths.GetParent(versionPath), false);
    if (shadow)
      // a shadow cannot be renamed across the shadow/primary namespace in one step; streamed, so a
      // multi-GB copy never lands in RAM (SAFE-BIGFILE)
      WholeFilePublisher.CopyBetween(member, normalizedPath, true, member, versionPath, false,
        admit: WholeFilePublisher.Pace(admit, member, member));
    else
      member.AtomicReplace(normalizedPath, versionPath, false);

    _WriteJson(member, _InfoPathFor(versionPath), new SnapshotVersionInfo {
      OriginalPath = normalizedPath,
      AsidedUtc = clock(),
      Pins = [.. pins],
    });

    journal.Complete(sequence, JournalOp.SnapshotAside);
    return versionPath;
  }

  /// <summary>
  /// Preserves a version by COPYING it, leaving the live file where it is.
  ///
  /// For a modification that does not replace the whole file: the bytes the writer does not touch
  /// have to survive in the live file as well as in the version, so there is nothing to rename and
  /// this is the case that genuinely costs. Streamed, so a multi-gigabyte file never lands in RAM.
  /// </summary>
  public string AsideByCopy(IVolumeIO member, string normalizedPath, bool shadow, IReadOnlyCollection<Guid> pins) {
    var versionPath = this._NewVersionPathFor(normalizedPath);
    var sequence = journal.LogIntent(JournalOp.SnapshotAside, normalizedPath, versionPath);

    member.EnsureFolder(PoolPaths.GetParent(versionPath), false);
    WholeFilePublisher.CopyBetween(member, normalizedPath, shadow, member, versionPath, false,
      admit: WholeFilePublisher.Pace(admit, member, member));

    _WriteJson(member, _InfoPathFor(versionPath), new SnapshotVersionInfo {
      OriginalPath = normalizedPath,
      AsidedUtc = clock(),
      Pins = [.. pins],
    });

    journal.Complete(sequence, JournalOp.SnapshotAside);
    return versionPath;
  }

  /// <summary>What the version store is currently occupying, across every online member.</summary>
  public long StoreBytes() => this._Versions().Sum(v => v.member.Stat(v.versionPath, false)?.Length ?? 0);

  /// <summary>What one snapshot is costing: the versions it pins, with a shared version counted once.</summary>
  public long BytesHeldBy(Guid id)
    => this._Versions().Where(v => v.info.Pins.Contains(id)).Sum(v => v.member.Stat(v.versionPath, false)?.Length ?? 0);

  /// <summary>The snapshots that still point at the live content of a path — the pins an aside must carry.</summary>
  public IReadOnlyList<Guid> SnapshotsNeeding(string normalizedPath) {
    var versions = this._Versions().Where(v => PoolPaths.PathComparer.Equals(v.info.OriginalPath, normalizedPath)).ToArray();
    return [.. this.List()
      .Where(s => this._Names(s.Id, normalizedPath))
      .Where(s => !versions.Any(v => v.info.AsidedUtc > s.CreatedUtc))
      .Select(s => s.Id)];
  }

  #endregion

  #region reading

  /// <summary>
  /// Where the content of <paramref name="normalizedPath"/> as of a snapshot lives: a version in
  /// the store, or null meaning "the live file, which nothing has touched since".
  /// </summary>
  public (IVolumeIO member, string versionPath)? Resolve(Guid snapshotId, string normalizedPath) {
    var taken = this.List().FirstOrDefault(s => s.Id == snapshotId)?.CreatedUtc;
    if (taken == null)
      throw new PoolFsException(PoolFsError.NotFound, $"No snapshot {snapshotId:D}");

    // the EARLIEST version set aside after the snapshot was taken is the content it saw
    return this._Versions()
      .Where(v => PoolPaths.PathComparer.Equals(v.info.OriginalPath, normalizedPath) && v.info.AsidedUtc > taken)
      .OrderBy(v => _TokenOf(v.versionPath))
      .Select(v => ((IVolumeIO member, string versionPath)?)(v.member, v.versionPath))
      .FirstOrDefault();
  }

  /// <summary>The paths a snapshot recorded.</summary>
  public IReadOnlyList<string> PathsIn(Guid snapshotId)
    => this._Indexes().Where(i => i.index.Id == snapshotId).Select(i => i.index.Paths).FirstOrDefault() ?? [];

  private bool _Names(Guid snapshotId, string normalizedPath)
    => this.PathsIn(snapshotId).Contains(normalizedPath, PoolPaths.PathComparer);

  #endregion

  #region on-disk plumbing

  private IEnumerable<(IVolumeIO member, SnapshotIndex index)> _Indexes() {
    foreach (var member in this._Online) {
      VolumeEntry[] entries;
      try {
        entries = [.. member.List(_INDEX_FOLDER, false)];
      } catch (PoolFsException) {
        continue; // this member holds no snapshots
      }

      foreach (var entry in entries) {
        if (entry.IsDirectory || !entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
          continue;

        if (_ReadJson<SnapshotIndex>(member, $"{_INDEX_FOLDER}/{entry.Name}") is { } index)
          yield return (member, index);
      }
    }
  }

  private IEnumerable<(IVolumeIO member, string versionPath, SnapshotVersionInfo info)> _Versions() {
    foreach (var member in this._Online)
      foreach (var versionPath in _Walk(member, _VERSION_FOLDER)) {
        if (!versionPath.EndsWith(_VERSION_SUFFIX, StringComparison.Ordinal))
          continue;

        if (_ReadJson<SnapshotVersionInfo>(member, _InfoPathFor(versionPath)) is { } info)
          yield return (member, versionPath, info);
      }
  }

  private static IEnumerable<string> _Walk(IVolumeIO member, string root) {
    var stack = new Stack<string>();
    stack.Push(root);
    while (stack.Count > 0) {
      var folder = stack.Pop();
      VolumeEntry[] entries;
      try {
        entries = [.. member.List(folder, false)];
      } catch (PoolFsException) {
        continue;
      }

      foreach (var entry in entries) {
        var child = $"{folder}/{entry.Name}";
        if (entry.IsDirectory)
          stack.Push(child);
        else
          yield return child;
      }
    }
  }

  private static void _WriteJson<T>(IVolumeIO member, string path, T value) {
    member.EnsureFolder(PoolPaths.GetParent(path), false);
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
    using var stream = member.OpenWrite(path, false, true);
    stream.SetLength(0);
    stream.Write(bytes, 0, bytes.Length);
    stream.Flush();
  }

  private static T? _ReadJson<T>(IVolumeIO member, string path) where T : class {
    try {
      using var stream = member.OpenRead(path, false);
      return JsonSerializer.Deserialize<T>(stream);
    } catch (Exception) {
      // a half-written or unreadable sidecar is not a reason to fail the whole listing
      return null;
    }
  }

  private static void _TryDelete(IVolumeIO member, string path) {
    try {
      if (member.FileExists(path, false))
        member.Delete(path, false);
    } catch (PoolFsException) {
      // a version we cannot remove is space, not danger; the next delete pass tries again
    }
  }

  #endregion

}
