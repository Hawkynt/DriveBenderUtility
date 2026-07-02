using System.Text.Json;
using DivisonM.Vfs;
using DivisonM.Vfs.Engine;

namespace DivisonM.Mount;

/// <summary>One activity row for the live feed (mirrors OPS-EVENTS for cross-process transport).</summary>
public sealed record ActivityRow(string Kind, string Path, long Bytes, string? From, string? To, string Reason);

/// <summary>
/// A mounted pool's live metrics + recent activity, published each second by the mount
/// process to the config dir so the <c>serve</c> daemon can stream it to the web UI
/// without hosting the engine itself (§6.13 the GUI talks to the daemon over a local API).
/// </summary>
public sealed record MetricsSnapshot {
  public required Guid PoolId { get; init; }
  public required string Name { get; init; }
  public required string Target { get; init; }
  public long ReadBytes { get; init; }
  public long WrittenBytes { get; init; }
  public double CacheHitRate { get; init; }
  public long DirtyFiles { get; init; }
  public long DrainedFiles { get; init; }
  public long RecoveredOperations { get; init; }
  public long BytesFree { get; init; }
  public long BytesTotal { get; init; }
  public required string StampUtc { get; init; }
  public IReadOnlyList<ActivityRow> RecentActivity { get; init; } = [];
}

/// <summary>Writes/reads the per-pool metrics snapshot files the daemon aggregates.</summary>
public sealed class MetricsPublisher(IHostEnvironment host) {

  private string _Directory => Path.Combine(host.ConfigRoot, "mounts");
  private string _Path(Guid poolId) => Path.Combine(this._Directory, $"{poolId:D}.metrics.json");

  public void Publish(PoolFileSystem fs, MountEntry entry) {
    var metrics = fs.GetMetrics();
    var stats = fs.StatFs();
    var snapshot = new MetricsSnapshot {
      PoolId = entry.PoolId,
      Name = entry.Name,
      Target = entry.Target,
      ReadBytes = metrics.ReadBytes,
      WrittenBytes = metrics.WrittenBytes,
      CacheHitRate = metrics.CacheHitRate,
      DirtyFiles = metrics.DirtyFiles,
      DrainedFiles = metrics.DrainedFiles,
      RecoveredOperations = metrics.RecoveredOperations,
      BytesFree = stats.BytesFree,
      BytesTotal = stats.BytesTotal,
      StampUtc = DateTime.UtcNow.ToString("O"),
      RecentActivity = [.. fs.Activity.History.Take(40).Select(e => new ActivityRow(
        e.Kind.ToString(), e.Path, e.Bytes, e.FromMember, e.ToMember, e.Reason))],
    };

    try {
      host.CreateDirectory(this._Directory);
      host.WriteAllTextAtomic(this._Path(entry.PoolId), JsonSerializer.Serialize(snapshot));
    } catch (IOException) {
      // metrics are best-effort; never let publishing perturb I/O
    }
  }

  public void Remove(Guid poolId) {
    var path = this._Path(poolId);
    if (host.FileExists(path))
      host.DeleteFile(path);
  }

  public MetricsSnapshot? TryRead(Guid poolId) {
    var path = this._Path(poolId);
    if (!host.FileExists(path))
      return null;

    try {
      return JsonSerializer.Deserialize<MetricsSnapshot>(host.ReadAllText(path));
    } catch (Exception) {
      return null;
    }
  }

}
