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
  private string _EntryPath(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.json");
  private string _StopPath(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.stop");
  private string _ErrorPath(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.error");
  private string _ReloadPath(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.reload");

  public void Register(MountEntry entry) {
    host.CreateDirectory(this._Directory);
    host.WriteAllTextAtomic(this._EntryPath(entry.PoolId), JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
    // mount succeeded: clear any stop-request, stale failure report and pending reload marker
    foreach (var path in new[] { this._StopPath(entry.PoolId), this._ErrorPath(entry.PoolId), this._ReloadPath(entry.PoolId) })
      if (host.FileExists(path))
        host.DeleteFile(path);
  }

  private string _OpPath(Guid poolId, string id) => Path.Combine(this._Directory, $"{poolId:D}.op-{id}.json");
  private string _OpResultPath(Guid poolId, string id) => Path.Combine(this._Directory, $"{poolId:D}.opdone-{id}.json");

  /// <summary>
  /// Cross-process pool operations (health/fix/restore): the MANAGER only files the request —
  /// the pool's own process executes it and files the result. The manager stays a pure UI shell
  /// that can be reloaded at any time without killing pool work.
  /// </summary>
  public void RequestOp(Guid poolId, string id, string op) {
    host.CreateDirectory(this._Directory);
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
    host.CreateDirectory(this._Directory);
    host.WriteAllTextAtomic(this._OpResultPath(poolId, id), json);
  }

  /// <summary>The manager waits for the pool process's result (long ops allowed; null on timeout).</summary>
  public string? WaitOpResult(Guid poolId, string id, TimeSpan timeout) {
    var path = this._OpResultPath(poolId, id);
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline) {
      if (host.FileExists(path))
        try {
          var json = host.ReadAllText(path);
          host.DeleteFile(path);
          return json;
        } catch (IOException) {
          // mid-write — retry
        }

      Thread.Sleep(250);
    }

    return null;
  }

  /// <summary>Asks the running mount process to re-read its configuration live (CFG.reload, cross-process).</summary>
  public void RequestReload(Guid poolId) {
    host.CreateDirectory(this._Directory);
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
    host.CreateDirectory(this._Directory);
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
    host.CreateDirectory(this._Directory);
    host.WriteAllTextAtomic(this._StopPath(poolId), DateTime.UtcNow.ToString("O"));
  }

  public IReadOnlyList<MountEntry> List() {
    var entries = new List<MountEntry>();
    foreach (var file in host.EnumerateFiles(this._Directory, "*.json")) {
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
