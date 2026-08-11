using DivisonM.Vfs;

namespace DivisonM.Vfs.Tests.TestSupport;

/// <summary>Operations a fault can be injected into (TST-FAULT).</summary>
public enum VolumeOp {
  OpenRead,
  OpenWrite,
  Write,
  Flush,
  Truncate,
  Delete,
  EnsureFolder,
  DeleteFolder,
  AtomicReplace,
  Stat,
  List,
}

/// <summary>
/// In-memory <see cref="IVolumeIO"/> (TST-FAKE): deterministic, headless, and able to
/// inject NoSpace, IoError, fsync failure, partial writes, volume disappearance and
/// power loss (unflushed content vanishes) so every SAFE-* requirement is testable.
///
/// THREAD-SAFE (TST-CONCURRENCY): a real backend serves concurrent callers, and the
/// engine's own concurrency tests pump background jobs against foreground I/O. Every
/// namespace and content mutation — including the ones the returned streams perform —
/// happens under one lock, so a test failure is always an ENGINE race and never a
/// corrupted test double.
/// </summary>
public sealed class FakeVolumeIO(Guid memberId, string displayName, string physicalVolumeId, long capacity = 1L << 40) : IVolumeIO {

  private sealed class FakeFile {
    public byte[] Current = [];
    public byte[]? Persisted;
    public DateTime CreationTimeUtc = _now;
    public DateTime LastWriteTimeUtc = _now;
  }

  private static readonly DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>Guards every field below; also taken by the streams this volume hands out.</summary>
  private readonly Lock _lock = new();

  private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase) { "" };
  private readonly Queue<(VolumeOp op, PoolFsError error)> _oneShotFaults = new();
  private readonly HashSet<VolumeOp> _permanentFaults = [];
  private int? _partialWriteBytes;
  private bool _online = true;
  private long _capacity = capacity;

  public Guid MemberId { get; } = memberId;
  public string DisplayName { get; } = displayName;
  public string PhysicalVolumeId { get; } = physicalVolumeId;

  public bool IsOnline {
    get { lock (this._lock) return this._online; }
    set { lock (this._lock) this._online = value; }
  }

  public long Capacity {
    get { lock (this._lock) return this._capacity; }
    set { lock (this._lock) this._capacity = value; }
  }

  public BackendCaps Caps { get; init; } = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.AtomicRename | BackendCaps.DurableFlush | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps;

  public long BytesUsed {
    get { lock (this._lock) return this._BytesUsedLocked; }
  }

  public long BytesFree {
    get { lock (this._lock) return Math.Max(0, this._capacity - this._BytesUsedLocked); }
  }

  public long BytesTotal => this.Capacity;

  private long _BytesUsedLocked => this._files.Values.Sum(f => (long)f.Current.Length);

  #region fault injection

  public void FailNext(VolumeOp op, PoolFsError error) {
    lock (this._lock)
      this._oneShotFaults.Enqueue((op, error));
  }

  public void AlwaysFail(VolumeOp op) {
    lock (this._lock)
      this._permanentFaults.Add(op);
  }

  public void ClearFaults() {
    lock (this._lock) {
      this._oneShotFaults.Clear();
      this._permanentFaults.Clear();
      this._partialWriteBytes = null;
    }
  }

  /// <summary>The next write accepts only <paramref name="bytes"/> bytes, then fails with IoError (torn-write fault).</summary>
  public void InjectPartialWrite(int bytes) {
    lock (this._lock)
      this._partialWriteBytes = bytes;
  }

  /// <summary>Power loss: unflushed content reverts to the last flushed state; never-flushed files vanish.</summary>
  public void SimulateCrash() {
    lock (this._lock)
      foreach (var (path, file) in this._files.ToArray())
        if (file.Persisted == null)
          this._files.Remove(path);
        else
          file.Current = (byte[])file.Persisted.Clone();
  }

  /// <summary>
  /// Fires at the start of an operation, BEFORE the volume lock is taken, so a test can run
  /// engine work from inside it and force a precise interleaving. Deliberately NOT invoked
  /// under <see cref="_lock"/>: a hook that called back into the engine would otherwise
  /// deadlock against the very volume it is suspending.
  /// </summary>
  public Action<VolumeOp, string>? BeforeOperation { get; set; }

  /// <summary>As <see cref="BeforeOperation"/>, but fired once the operation's answer is captured
  /// and the lock released — the window in which a caller still believes a stale observation.</summary>
  public Action<VolumeOp, string>? AfterOperation { get; set; }

  private void _Before(VolumeOp op, string relativePath) => this.BeforeOperation?.Invoke(op, relativePath);

  private void _After(VolumeOp op, string relativePath) => this.AfterOperation?.Invoke(op, relativePath);

  /// <summary>Fault gate; the caller holds <see cref="_lock"/>.</summary>
  private void _Check(VolumeOp op) {
    if (!this._online)
      throw new PoolFsException(PoolFsError.Offline, $"Member '{this.DisplayName}' is offline");

    if (this._permanentFaults.Contains(op))
      throw new PoolFsException(PoolFsError.IoError, $"Injected permanent fault on {op}");

    if (this._oneShotFaults.Count > 0 && this._oneShotFaults.Peek().op == op) {
      var (_, error) = this._oneShotFaults.Dequeue();
      throw new PoolFsException(error, $"Injected fault on {op}");
    }
  }

  #endregion

  #region test inspection helpers

  public IReadOnlyCollection<string> FilePaths {
    get { lock (this._lock) return [.. this._files.Keys]; }
  }

  public byte[]? GetContent(string relativePath, bool shadow) {
    lock (this._lock) {
      var file = this._files.GetValueOrDefault(PoolPaths.ToPhysical(relativePath, shadow));
      return file == null ? null : (byte[])file.Current.Clone();
    }
  }

  public void Seed(string relativePath, bool shadow, byte[] content) {
    lock (this._lock) {
      var physical = PoolPaths.ToPhysical(relativePath, shadow);
      this._EnsureParents(physical);
      this._files[physical] = new() { Current = (byte[])content.Clone(), Persisted = (byte[])content.Clone() };
    }
  }

  /// <summary>Mutates content behind the driver's back (for OOB/bit-rot scenarios, SAFE-OOB).</summary>
  public void CorruptSilently(string relativePath, bool shadow, Action<byte[]> mutate) {
    lock (this._lock) {
      var file = this._files[PoolPaths.ToPhysical(relativePath, shadow)];
      mutate(file.Current);
      file.Persisted = (byte[])file.Current.Clone();
    }
  }

  #endregion

  private void _EnsureParents(string physicalPath) {
    var parent = PoolPaths.GetParent(physicalPath);
    while (parent.Length > 0 && this._folders.Add(parent))
      parent = PoolPaths.GetParent(parent);
  }

  public Stream OpenRead(string relativePath, bool shadow) {
    lock (this._lock) {
      this._Check(VolumeOp.OpenRead);
      var physical = PoolPaths.ToPhysical(relativePath, shadow);
      var file = this._files.GetValueOrDefault(physical)
                 ?? throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

      return new FakeVolumeStream(this, file, writable: false);
    }
  }

  public Stream OpenWrite(string relativePath, bool shadow, bool create) {
    this._Before(VolumeOp.OpenWrite, relativePath);
    lock (this._lock) {
      this._Check(VolumeOp.OpenWrite);
      var physical = PoolPaths.ToPhysical(relativePath, shadow);
      var file = this._files.GetValueOrDefault(physical);
      if (file == null) {
        if (!create)
          throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

        this._EnsureParents(physical);
        file = new();
        this._files[physical] = file;
      }

      return new FakeVolumeStream(this, file, writable: true);
    }
  }

  public void Truncate(string relativePath, bool shadow, long length) {
    lock (this._lock) {
      this._Check(VolumeOp.Truncate);
      var file = this._files.GetValueOrDefault(PoolPaths.ToPhysical(relativePath, shadow))
                 ?? throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

      var resized = new byte[length];
      Array.Copy(file.Current, resized, Math.Min(file.Current.Length, length));
      file.Current = resized;
      file.LastWriteTimeUtc = DateTime.UtcNow;
    }
  }

  public void Delete(string relativePath, bool shadow) {
    lock (this._lock) {
      this._Check(VolumeOp.Delete);
      if (!this._files.Remove(PoolPaths.ToPhysical(relativePath, shadow)))
        throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");
    }
  }

  public void EnsureFolder(string relativeFolder, bool shadow) {
    lock (this._lock) {
      this._Check(VolumeOp.EnsureFolder);
      var physical = PoolPaths.ToPhysicalFolder(relativeFolder, shadow);
      this._EnsureParents(physical + "/x");
      this._folders.Add(physical);
    }
  }

  public void DeleteFolder(string relativeFolder, bool shadow) {
    lock (this._lock) {
      this._Check(VolumeOp.DeleteFolder);
      var physical = PoolPaths.ToPhysicalFolder(relativeFolder, shadow);
      if (!this._folders.Contains(physical))
        throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {relativeFolder}");

      var prefix = physical + "/";
      if (this._files.Keys.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) || this._folders.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        throw new PoolFsException(PoolFsError.NotEmpty, $"Folder not empty: {relativeFolder}");

      this._folders.Remove(physical);
    }
  }

  public void RenameFolder(string fromRelativeFolder, string toRelativeFolder) {
    lock (this._lock) {
      this._Check(VolumeOp.EnsureFolder);
      var fromPhysical = PoolPaths.ToPhysicalFolder(fromRelativeFolder, false);
      var toPhysical = PoolPaths.ToPhysicalFolder(toRelativeFolder, false);
      if (!this._folders.Contains(fromPhysical))
        throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {fromRelativeFolder}");
      if (this._folders.Contains(toPhysical) || this._files.ContainsKey(toPhysical))
        throw new PoolFsException(PoolFsError.Exists, $"Target already exists: {toRelativeFolder}");

      this._EnsureParents(toPhysical + "/x");
      var fromPrefix = fromPhysical + "/";
      foreach (var folder in this._folders.Where(f => f.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray()) {
        this._folders.Remove(folder);
        this._folders.Add(toPhysical + "/" + folder[fromPrefix.Length..]);
      }

      this._folders.Remove(fromPhysical);
      this._folders.Add(toPhysical);

      foreach (var (key, file) in this._files.Where(kv => kv.Key.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)).ToArray()) {
        this._files.Remove(key);
        this._files[toPhysical + "/" + key[fromPrefix.Length..]] = file;
      }
    }
  }

  public void AtomicReplace(string tempRelative, string finalRelative, bool shadow) {
    lock (this._lock) {
      this._Check(VolumeOp.AtomicReplace);
      if ((this.Caps & BackendCaps.AtomicRename) == 0)
        throw new PoolFsException(PoolFsError.NotSupported, $"Backend '{this.DisplayName}' has no atomic rename (capability profile)");

      var tempPhysical = PoolPaths.ToPhysical(tempRelative, shadow);
      var finalPhysical = PoolPaths.ToPhysical(finalRelative, shadow);
      var staged = this._files.GetValueOrDefault(tempPhysical)
                   ?? throw new PoolFsException(PoolFsError.NotFound, $"Staged file not found: {tempRelative}");

      // rename is atomic and durable: content is persisted as part of publication (SAFE-ATOMIC)
      staged.Persisted = (byte[])staged.Current.Clone();
      this._files.Remove(tempPhysical);
      this._EnsureParents(finalPhysical);
      this._files[finalPhysical] = staged;
    }
  }

  public FileMeta? Stat(string relativePath, bool shadow) {
    this._Before(VolumeOp.Stat, relativePath);
    FileMeta? result;
    lock (this._lock) {
      this._Check(VolumeOp.Stat);
      var physical = PoolPaths.ToPhysical(relativePath, shadow);
      result = this._files.TryGetValue(physical, out var file)
        ? new(file.Current.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, FileAttributes.Normal)
        : this._folders.Contains(physical)
          ? new FileMeta(0, _now, _now, FileAttributes.Directory)
          : null;
    }

    // fired with the answer already captured but not yet returned: a test can run engine work
    // here to prove the CALLER re-validates instead of trusting a now-stale observation
    this._After(VolumeOp.Stat, relativePath);
    return result;
  }

  public bool FileExists(string relativePath, bool shadow) {
    lock (this._lock)
      return this._online && this._files.ContainsKey(PoolPaths.ToPhysical(relativePath, shadow));
  }

  public bool FolderExists(string relativeFolder, bool shadow) {
    lock (this._lock)
      return this._online && this._folders.Contains(PoolPaths.ToPhysicalFolder(relativeFolder, shadow));
  }

  /// <summary>Materialised under the lock — a lazily walked enumerable would read the namespace after it released.</summary>
  public IEnumerable<VolumeEntry> List(string relativeFolder, bool shadow) {
    lock (this._lock) {
      this._Check(VolumeOp.List);
      var physical = PoolPaths.ToPhysicalFolder(relativeFolder, shadow);
      if (!this._folders.Contains(physical))
        throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {relativeFolder}");

      var prefix = physical.Length == 0 ? "" : physical + "/";
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var entries = new List<VolumeEntry>();

      foreach (var (path, file) in this._files) {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
          continue;

        var rest = path[prefix.Length..];
        if (rest.Length == 0 || rest.IndexOf('/') >= 0)
          continue;

        if (seen.Add(rest))
          entries.Add(new(rest, false, file.Current.Length, file.LastWriteTimeUtc));
      }

      foreach (var folder in this._folders) {
        if (folder.Length == 0 || !folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
          continue;

        var rest = folder[prefix.Length..];
        if (rest.Length == 0 || rest.Contains('/'))
          continue;

        if (seen.Add(rest))
          entries.Add(new(rest, true, 0, _now));
      }

      return entries;
    }
  }

  public void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc) {
    lock (this._lock) {
      var file = this._files.GetValueOrDefault(PoolPaths.ToPhysical(relativePath, shadow))
                 ?? throw new PoolFsException(PoolFsError.NotFound, $"File not found: {relativePath}");

      if (creationTimeUtc is { } created)
        file.CreationTimeUtc = created;
      if (lastWriteTimeUtc is { } modified)
        file.LastWriteTimeUtc = modified;
    }
  }

  private sealed class FakeVolumeStream(FakeVolumeIO owner, FakeFile file, bool writable) : Stream {

    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => writable;

    public override long Length {
      get { lock (owner._lock) return file.Current.Length; }
    }

    public override long Position {
      get => this._position;
      set => this._position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) {
      lock (owner._lock) {
        // a real stream positioned at or past EOF reads 0 bytes; it does NOT throw. Array.Copy
        // validates sourceIndex even for a zero-length copy, so the past-EOF case must short-circuit
        if (this._position >= file.Current.Length)
          return 0;

        var available = (int)Math.Max(0, Math.Min(count, file.Current.Length - this._position));
        Array.Copy(file.Current, this._position, buffer, offset, available);
        this._position += available;
        return available;
      }
    }

    public override void Write(byte[] buffer, int offset, int count) {
      if (!writable)
        throw new NotSupportedException();

      lock (owner._lock) {
        owner._Check(VolumeOp.Write);

        var accepted = count;
        var tornWrite = false;
        if (owner._partialWriteBytes is { } partial) {
          accepted = Math.Min(count, partial);
          owner._partialWriteBytes = null;
          tornWrite = true;
        }

        var end = this._position + accepted;
        var growth = Math.Max(0, end - file.Current.Length);
        if (growth > 0 && owner.BytesFree < growth)
          throw new PoolFsException(PoolFsError.NoSpace, $"No space left on '{owner.DisplayName}'");

        if (end > file.Current.Length) {
          var resized = new byte[end];
          Array.Copy(file.Current, resized, file.Current.Length);
          file.Current = resized;
        }

        Array.Copy(buffer, offset, file.Current, this._position, accepted);
        this._position = end;
        file.LastWriteTimeUtc = DateTime.UtcNow;

        if (tornWrite)
          throw new PoolFsException(PoolFsError.IoError, "Injected partial write");
      }
    }

    public override void Flush() {
      lock (owner._lock) {
        owner._Check(VolumeOp.Flush);

        // a backend without DurableFlush acknowledges the flush but does not actually
        // persist — exactly the FTP/WebDAV behaviour the engine must never trust
        if ((owner.Caps & BackendCaps.DurableFlush) != 0)
          file.Persisted = (byte[])file.Current.Clone();
      }
    }

    public override long Seek(long offset, SeekOrigin origin) {
      lock (owner._lock)
        return this._position = origin switch {
          SeekOrigin.Begin => offset,
          SeekOrigin.Current => this._position + offset,
          _ => file.Current.Length + offset,
        };
    }

    public override void SetLength(long value) {
      lock (owner._lock) {
        var resized = new byte[value];
        Array.Copy(file.Current, resized, Math.Min(file.Current.Length, value));
        file.Current = resized;
      }
    }
  }

}
