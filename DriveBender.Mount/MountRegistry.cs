using System.Text.Json;
using DivisonM.Vfs;

namespace DivisonM.Mount;

/// <summary>Live state of one mounted pool, published so other processes can query and unmount it.</summary>
public sealed record MountEntry {
  public required Guid PoolId { get; init; }
  public required string Name { get; init; }
  public required string Target { get; init; }
  public required int ProcessId { get; init; }
  public required string Backend { get; init; }
  public required string StartedUtc { get; init; }
}

/// <summary>
/// Cross-process mount registry (FR-MOUNT-CLI status/unmount): the mounting process
/// publishes its entry under the config root and polls a stop-file; another dbmount
/// invocation writes that stop-file to trigger a clean unmount (which flushes dirty
/// state, FR-CLEAN-UNMOUNT) rather than killing the process.
/// </summary>
public sealed class MountRegistry(IHostEnvironment host) {

  private string _Directory => Path.Combine(host.ConfigRoot, "mounts");
  private bool _secured;

  /// <summary>
  /// Creates the cross-process channel directory and locks it down (SEC-API): the mount
  /// registry, op requests, metrics snapshots and stop/reload flags all live here, so a
  /// standard local user must not be able to read member paths or inject forged control files.
  /// On Windows the DACL is restricted to SYSTEM/Administrators/owner; on Unix it is chmod 700.
  /// </summary>
  private void _EnsureSecureDir() {
    var existed = host.DirectoryExists(this._Directory);
    host.CreateDirectory(this._Directory);
    if (this._secured || existed)
      return;

    try {
#if WINDOWS
      if (OperatingSystem.IsWindows()) {
        var info = new DirectoryInfo(this._Directory);
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // drop ProgramData's inherited Users ACE
        var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        foreach (var sid in new[] {
          new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
          new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
          (System.Security.Principal.SecurityIdentifier)owner,
        })
          security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            sid, System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));

        System.IO.FileSystemAclExtensions.SetAccessControl(info, security);
      }
#else
      if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(this._Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#endif
      this._secured = true;
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]Could not restrict permissions on the channel directory '{this._Directory}': {e.Message}");
    }
  }

  private string _EntryPath(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.json");
  private string _StopPath(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.stop");
  private string _ErrorPath(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.error");
  private string _ReloadPath(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.reload");

  /// <summary>
  /// Acquires the pool's exclusive cross-process MOUNT lock, held for the mount's whole lifetime
  /// (returns null when another process already holds it). The registry entry check is racy — two
  /// managers can both pass it and launch two mount processes over one member set, and two engines
  /// over one set corrupt each other. This OS-level file lock (FileShare.None) is the real guard;
  /// DeleteOnClose releases it even if the mount process crashes.
  /// </summary>
  public IDisposable? TryAcquireMountLock(Guid poolId) {
    this._EnsureSecureDir();
    var path = Path.Combine(this._Directory, $"{poolId:D}.mountlock");
    try {
      return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    } catch (IOException) {
      return null; // another mount process holds it
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  public void Register(MountEntry entry) {
    this._EnsureSecureDir();
    host.WriteAllTextAtomic(this._EntryPath(entry.PoolId), JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
    // mount succeeded: clear any stop-request, stale failure report and pending reload marker
    foreach (var path in new[] { this._StopPath(entry.PoolId), this._ErrorPath(entry.PoolId), this._ReloadPath(entry.PoolId) })
      if (host.FileExists(path))
        host.DeleteFile(path);
  }

  private string _OpPath(Guid poolId, string id) => Path.Combine(this._Directory, $"{poolId:D}.op-{id}.json");
  private string _OpResultPath(Guid poolId, string id) => Path.Combine(this._Directory, $"{poolId:D}.opdone-{id}.json");
  private string _OpCancelPath(Guid poolId, string id) => Path.Combine(this._Directory, $"{poolId:D}.opstop-{id}");
  private string _OpProgressPath(Guid poolId, string id) => Path.Combine(this._Directory, $"{poolId:D}.opstep-{id}.json");

  /// <summary>
  /// Asks the pool's own process to stop an operation the manager relayed to it.
  ///
  /// The relay was one-way: the manager filed a request and then had nothing to say, so such work
  /// reported itself un-cancellable — honest, but it meant a deep scrub of a mounted pool could not
  /// be stopped for however many hours it took. This is the way back, in the same channel directory
  /// and with the same file-drop shape as the request itself.
  /// </summary>
  public void RequestOpCancel(Guid poolId, string id) {
    this._EnsureSecureDir();
    host.WriteAllTextAtomic(this._OpCancelPath(poolId, id), DateTime.UtcNow.ToString("O"));
  }

  /// <summary>Polled by the running operation, between items.</summary>
  public bool IsOpCancelRequested(Guid poolId, string id) => host.FileExists(this._OpCancelPath(poolId, id));

  /// <summary>The pool process publishes how far it has got; the manager pumps it into the job ticket.</summary>
  public void WriteOpProgress(Guid poolId, string id, string json) {
    this._EnsureSecureDir();
    try {
      host.WriteAllTextAtomic(this._OpProgressPath(poolId, id), json);
    } catch (IOException) {
      // progress is best-effort and must never disturb the work it is describing
    }
  }

  /// <summary>The manager's view of that progress, or null when none has been published yet.</summary>
  public string? ReadOpProgress(Guid poolId, string id) {
    var path = this._OpProgressPath(poolId, id);
    try {
      return host.FileExists(path) ? host.ReadAllText(path) : null;
    } catch (IOException) {
      return null; // mid-write; the next poll gets it
    }
  }

  /// <summary>Clears an operation's side-channel files once it is over, whichever way it ended.</summary>
  public void ClearOp(Guid poolId, string id) {
    foreach (var path in new[] { this._OpCancelPath(poolId, id), this._OpProgressPath(poolId, id) })
      try {
        if (host.FileExists(path))
          host.DeleteFile(path);
      } catch (IOException) {
        // best-effort: a stale marker is pruned by the next operation with the same id, which
        // cannot happen anyway because ids are fresh guids
      }
  }

  /// <summary>
  /// Cross-process pool operations (health/fix/restore): the MANAGER only files the request —
  /// the pool's own process executes it and files the result. The manager stays a pure UI shell
  /// that can be reloaded at any time without killing pool work.
  /// </summary>
  public void RequestOp(Guid poolId, string id, string op) {
    this._EnsureSecureDir();
    host.WriteAllTextAtomic(this._OpPath(poolId, id), op);
  }

  /// <summary>The pool process consumes its pending operation requests (checked each pump tick).</summary>
  public IReadOnlyList<(string id, string op)> ConsumeOps(Guid poolId) {
    var results = new List<(string, string)>();
    var prefix = $"{poolId:D}.op-";
    foreach (var file in host.EnumerateFiles(this._Directory, $"{poolId:D}.op-*.json")) {
      var name = Path.GetFileName(file);
      var id = name[prefix.Length..^".json".Length];
      string op;
      try {
        op = host.ReadAllText(file).Trim();
        host.DeleteFile(file);
      } catch (IOException) {
        continue; // racing writer — next tick
      }

      results.Add((id, op));
    }

    return results;
  }

  /// <summary>The pool process files an operation's result for the manager to pick up.</summary>
  public void WriteOpResult(Guid poolId, string id, string json) {
    this._EnsureSecureDir();
    host.WriteAllTextAtomic(this._OpResultPath(poolId, id), json);
  }

  /// <summary>Collects the pool process's result if it has filed one yet; null means "not finished".</summary>
  public string? TryTakeOpResult(Guid poolId, string id) {
    var path = this._OpResultPath(poolId, id);
    if (!host.FileExists(path))
      return null;

    try {
      var json = host.ReadAllText(path);
      host.DeleteFile(path);
      return json;
    } catch (IOException) {
      return null; // mid-write — the caller polls again
    }
  }

  /// <summary>The manager waits for the pool process's result (long ops allowed; null on timeout).</summary>
  public string? WaitOpResult(Guid poolId, string id, TimeSpan timeout) {
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline) {
      if (this.TryTakeOpResult(poolId, id) is { } json)
        return json;

      Thread.Sleep(250);
    }

    return null;
  }

  /// <summary>Asks the running mount process to re-read its configuration live (CFG.reload, cross-process).</summary>
  public void RequestReload(Guid poolId) {
    this._EnsureSecureDir();
    host.WriteAllTextAtomic(this._ReloadPath(poolId), DateTime.UtcNow.ToString("O"));
  }

  /// <summary>Consumes a pending reload request (checked by the mount pump each second).</summary>
  public bool ConsumeReload(Guid poolId) {
    var path = this._ReloadPath(poolId);
    if (!host.FileExists(path))
      return false;

    try {
      host.DeleteFile(path);
    } catch (IOException) {
      return false; // racing writer — pick it up next tick
    }

    return true;
  }

  /// <summary>
  /// A mount child records why it failed here so the launching daemon can report the real reason —
  /// an elevated (ShellExecute) child can't have its stderr redirected, so this file is the only
  /// channel back.
  /// </summary>
  public void ReportError(Guid poolId, string message) {
    this._EnsureSecureDir();
    host.WriteAllTextAtomic(this._ErrorPath(poolId), message);
  }

  /// <summary>Reads and removes a failure report left by a mount child, if any.</summary>
  public string? TakeError(Guid poolId) {
    var path = this._ErrorPath(poolId);
    if (!host.FileExists(path))
      return null;

    string message;
    try {
      message = host.ReadAllText(path).Trim();
    } catch (IOException) {
      return null;
    }

    try {
      host.DeleteFile(path);
    } catch (IOException) {
      // best-effort cleanup; the message is what matters
    }

    return message.Length > 0 ? message : null;
  }

  public void Unregister(Guid poolId) {
    foreach (var path in new[] { this._EntryPath(poolId), this._StopPath(poolId) })
      if (host.FileExists(path))
        host.DeleteFile(path);
  }

  public bool StopRequested(Guid poolId) => host.FileExists(this._StopPath(poolId));

  public void RequestStop(Guid poolId) {
    this._EnsureSecureDir();
    host.WriteAllTextAtomic(this._StopPath(poolId), DateTime.UtcNow.ToString("O"));
  }

  public IReadOnlyList<MountEntry> List() {
    var entries = new List<MountEntry>();
    foreach (var file in host.EnumerateFiles(this._Directory, "*.json")) {
      // A mount entry is named for its pool id and nothing else. The same directory is the whole
      // cross-process channel, so `*.json` also matches every `<pool>.metrics.json` snapshot and
      // every relayed-operation file in it — each of which was then read off disk and thrown at a
      // deserializer that could only fail. A machine that had mounted a few hundred pools was
      // paying several hundred file reads and as many exceptions for every `status`, and for each
      // of the hundred polls one `unmount` makes.
      if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _))
        continue;

      MountEntry? entry;
      try {
        entry = JsonSerializer.Deserialize<MountEntry>(host.ReadAllText(file));
      } catch (Exception) {
        continue;
      }

      if (entry == null)
        continue;

      // prune stale entries whose mounting process is gone
      if (!_ProcessAlive(entry.ProcessId)) {
        this.Unregister(entry.PoolId);
        continue;
      }

      entries.Add(entry);
    }

    return entries;
  }

  public MountEntry? Find(string targetOrPoolId) {
    var entries = this.List();
    if (Guid.TryParse(targetOrPoolId, out var id))
      return entries.FirstOrDefault(e => e.PoolId == id);

    return entries.FirstOrDefault(e =>
      e.Target.Equals(targetOrPoolId, StringComparison.OrdinalIgnoreCase)
      || e.Name.Equals(targetOrPoolId, StringComparison.OrdinalIgnoreCase));
  }

  private static bool _ProcessAlive(int pid) {
    try {
      System.Diagnostics.Process.GetProcessById(pid);
      return true;
    } catch (ArgumentException) {
      return false;
    }
  }

}
