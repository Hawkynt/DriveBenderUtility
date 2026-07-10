namespace DivisonM.Vfs.Engine;

/// <summary>
/// Per-file state shared by all handles open on the same path (FR-CONCURRENCY): a
/// single owner for that file's lock and (from M2 on) its dirty write buffer, so no two
/// writers ever race. Locks are per-file, never global.
/// </summary>
public sealed class FileState(string normalizedPath) {
  public string Path { get; internal set; } = normalizedPath;
  public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.NoRecursion);
  internal int RefCount;

  /// <summary>Per-handle read-ahead detectors keyed by handle value.</summary>
  internal readonly Dictionary<long, ReadAheadState> ReadAhead = [];
}

/// <summary>
/// Central handle table (§6.4): maps open handles to their shared per-file state,
/// refcounted so the state object lives exactly as long as any handle on the path.
/// </summary>
public sealed class HandleTable {

  public sealed record OpenHandle(NodeHandle Handle, FileState File, AccessMode Access);

  private readonly Dictionary<long, OpenHandle> _handles = [];
  private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
  private readonly Lock _lock = new();
  private long _nextHandle;

  public OpenHandle Open(string normalizedPath, AccessMode access) {
    lock (this._lock) {
      if (!this._files.TryGetValue(normalizedPath, out var file))
        this._files.Add(normalizedPath, file = new(normalizedPath));

      ++file.RefCount;
      var handle = new NodeHandle(++this._nextHandle);
      var open = new OpenHandle(handle, file, access);
      this._handles.Add(handle.Value, open);
      return open;
    }
  }

  public OpenHandle Get(NodeHandle handle) {
    lock (this._lock)
      return this._handles.TryGetValue(handle.Value, out var open)
        ? open
        : throw new PoolFsException(PoolFsError.StaleHandle, $"Handle {handle.Value} is not open");
  }

  public void Close(NodeHandle handle) {
    lock (this._lock) {
      if (!this._handles.Remove(handle.Value, out var open))
        throw new PoolFsException(PoolFsError.StaleHandle, $"Handle {handle.Value} is not open");

      // the read path mutates ReadAhead under lock(File.ReadAhead) — take the SAME lock here so
      // a concurrent Read + Close on one file never corrupts the dictionary
      lock (open.File.ReadAhead)
        open.File.ReadAhead.Remove(handle.Value);
      if (--open.File.RefCount == 0)
        this._files.Remove(open.File.Path);
    }
  }

  /// <summary>True when any handle is open on the path (used by structural ops).</summary>
  public bool IsOpen(string normalizedPath) {
    lock (this._lock)
      return this._files.ContainsKey(normalizedPath);
  }

  /// <summary>Follows an open file across a rename so existing handles stay valid.</summary>
  public void RenamePath(string fromNormalized, string toNormalized) {
    lock (this._lock) {
      if (!this._files.Remove(fromNormalized, out var file))
        return;

      file.Path = toNormalized;
      this._files[toNormalized] = file;
    }
  }

  /// <summary>Follows every open file under a renamed folder so their handles stay valid (folder FR-RENAME).</summary>
  public void RenameSubtree(string fromNormalized, string toNormalized) {
    lock (this._lock) {
      var fromPrefix = fromNormalized + "/";
      foreach (var key in this._files.Keys.Where(k => k.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray()) {
        var file = this._files[key];
        this._files.Remove(key);
        file.Path = toNormalized + "/" + key[fromPrefix.Length..];
        this._files[file.Path] = file;
      }
    }
  }

  public int OpenHandleCount {
    get {
      lock (this._lock)
        return this._handles.Count;
    }
  }

}
