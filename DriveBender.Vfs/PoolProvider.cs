namespace DivisonM.Vfs;

/// <summary>Reference to a discovered pool, carrying its manifest (the one true model).</summary>
public sealed record PoolRef(Guid PoolId, string Name, bool IsVirtual, PoolManifest Manifest);

/// <summary>
/// Health of an opened pool (§6.0.7): resolved/missing members, same-physical-volume
/// conflicts (SAFE-PHYS), network-durability warnings, and de-duplicated free-space
/// accounting (FR-SPACE-SHARED) so hosts can surface degraded state.
/// </summary>
public sealed record PoolHealth {
  public required Guid PoolId { get; init; }
  public required string Name { get; init; }
  public required IReadOnlyList<PoolMember> Members { get; init; }
  public required IReadOnlyList<string> Warnings { get; init; }

  /// <summary>Groups of online members sharing one physical volume — one failure domain each; duplication must never co-locate copies inside a group (SAFE-PHYS).</summary>
  public required IReadOnlyList<IReadOnlyList<PoolMember>> SharedFailureDomains { get; init; }

  /// <summary>Pool free bytes, de-duplicated across members sharing a volume and reduced by reserveBytes (FR-SPACE-SHARED).</summary>
  public required long BytesFree { get; init; }

  public required long BytesTotal { get; init; }

  public IEnumerable<PoolMember> OfflineMembers => this.Members.Where(m => !m.Online);
  public IEnumerable<PoolMember> OnlineMembers => this.Members.Where(m => m.Online);
  public bool IsDegraded => this.Members.Any(m => !m.Online) || this.SharedFailureDomains.Count > 0;

  /// <summary>Number of independent failure domains available for placing copies (§6.3: D≥2 requires ≥D domains).</summary>
  public int IndependentFailureDomains => this.OnlineMembers.Select(m => m.PhysicalVolumeId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public interface IPoolProvider {
  /// <summary>Union of all manifest sources; explicit manifests win over virtual ones with the same pool id.</summary>
  IReadOnlyList<PoolRef> Discover();

  /// <summary>Resolves members and opens the pool, reporting degraded state; throws when nothing is mountable.</summary>
  DriveBender.IMountPoint Open(PoolRef pool, out PoolHealth health);

  /// <summary>Resolves members and computes health without constructing a mount point.</summary>
  PoolHealth Inspect(PoolRef pool);
}

/// <summary>Default provider over any set of manifest sources (JSON registry + native scan).</summary>
public sealed class PoolProvider(IHostEnvironment host, ManifestStore store, IEnumerable<IManifestSource> sources, IReadOnlyList<string>? searchPaths = null, IRemoteMemberResolver? remoteResolver = null) : IPoolProvider {

  private readonly IManifestSource[] _sources = [.. sources];

  public IReadOnlyList<PoolRef> Discover() {
    var byId = new Dictionary<Guid, PoolManifest>();
    foreach (var source in this._sources)
    foreach (var manifest in source.Enumerate()) {
      if (byId.TryGetValue(manifest.PoolId, out var existing)) {
        // an explicit (non-virtual) manifest is authoritative over a scan-synthesized one (FR-ADOPT)
        if (existing.IsVirtual && !manifest.IsVirtual)
          byId[manifest.PoolId] = manifest;
      } else
        byId.Add(manifest.PoolId, manifest);
    }

    return [.. byId.Values.Select(m => new PoolRef(m.PoolId, m.Name, m.IsVirtual, m))];
  }

  public PoolHealth Inspect(PoolRef pool) {
    var resolver = new MemberResolver(host, store, searchPaths, remoteResolver);
    var members = resolver.ResolveAll(pool.Manifest);
    return this._ComputeHealth(pool, members);
  }

  public DriveBender.IMountPoint Open(PoolRef pool, out PoolHealth health) {
    // reconcile registry vs. member mirrors BEFORE serving: the discovery source read the registry,
    // which may be a stale restore-from-backup while a member mirror holds the newer truth (a member
    // added/removed, duplication raised on another machine). Highest version wins, and stale copies
    // are refreshed — otherwise the stale registry would be served AND re-saved over the newer
    // mirrors (SAFE-MANIFEST). Virtual (scan-synthesized) pools have no mirrors to reconcile.
    if (!pool.Manifest.IsVirtual
        && store.Reconcile(pool.PoolId, pool.Manifest.Members.Select(m => m.Path)) is { } winner
        && winner.Version > pool.Manifest.Version) {
      DriveBender.Logger($"Reconciled pool '{pool.Name}': using manifest version {winner.Version} (registry was {pool.Manifest.Version})");
      pool = new PoolRef(winner.PoolId, winner.Name, winner.IsVirtual, winner);
    }

    var resolver = new MemberResolver(host, store, searchPaths, remoteResolver);
    var members = resolver.ResolveAll(pool.Manifest);
    health = this._ComputeHealth(pool, members);

    var online = members.Where(m => m.Online).ToArray();
    if (online.Length == 0)
      throw new PoolFsException(PoolFsError.Offline, $"Pool '{pool.Name}' has no resolvable member — refusing to open");

    // newly resolved paths are written back so the next mount finds them first (FR-RESOLVE-MEMBER)
    if (!pool.Manifest.IsVirtual && members.Any(m => m is { Online: true, PathChanged: true })) {
      var updated = pool.Manifest with {
        Members = [.. pool.Manifest.Members.Select(definition => {
          var resolved = members.FirstOrDefault(m => m.MemberId == definition.MemberId);
          return resolved is { Online: true, PathChanged: true } ? definition with { Path = resolved.ResolvedPath } : definition;
        })],
      };
      store.Save(updated, members.Where(m => m.Online).ToDictionary(m => m.MemberId, m => m.ResolvedPath));
    }

    var drives = online
      .Where(m => !m.Network || !m.ResolvedPath.Contains("://")) // Core's offline tooling only walks host paths
      .Select(m => new DriveBender.PoolDriveWithoutPool(
        pool.Name,
        m.Label ?? m.ResolvedPath,
        pool.PoolId,
        new DirectoryInfo(m.ResolvedPath)
      ));

    return new DriveBender.MountPoint(drives);
  }

  private PoolHealth _ComputeHealth(PoolRef pool, IReadOnlyList<PoolMember> members) {
    var warnings = new List<string>();

    foreach (var member in members.Where(m => !m.Online))
      warnings.Add($"Member '{member.Label ?? member.ResolvedPath}' ({member.MemberId}) is offline — pool opens degraded, owed duplication is deferred (SAFE-OFFLINE)");

    foreach (var member in members.Where(m => m is { Online: true, MarkerVerified: false }))
      warnings.Add($"Member '{member.Label ?? member.ResolvedPath}' was accepted by path hint only (no marker yet)");

    foreach (var member in members.Where(m => m is { Online: true, PathChanged: true }))
      warnings.Add($"Member '{member.Label ?? member.ResolvedPath}' moved — resolved by marker content at '{member.ResolvedPath}'");

    var domains = members
      .Where(m => m.Online)
      .GroupBy(m => m.PhysicalVolumeId, StringComparer.OrdinalIgnoreCase)
      .Where(g => g.Count() > 1)
      .Select(g => (IReadOnlyList<PoolMember>)[.. g])
      .ToArray();

    foreach (var domain in domains)
      warnings.Add($"Members {string.Join(", ", domain.Select(m => $"'{m.Label ?? m.ResolvedPath}'"))} share one physical volume — one failure domain; redundant copies will never be co-located there (SAFE-PHYS)");

    foreach (var member in members.Where(m => m is { Online: true, Network: true }))
      warnings.Add($"Network member '{member.Label ?? member.ResolvedPath}': durable flush is unverified — not eligible to satisfy minCopiesBeforeAck until probed (SAFE-NET-DURABILITY)");

    // FR-SPACE-SHARED: free/total per distinct physical volume, reduced by reserves, never double-counted
    long bytesFree = 0, bytesTotal = 0;
    foreach (var group in members.Where(m => m.Online).GroupBy(m => m.PhysicalVolumeId, StringComparer.OrdinalIgnoreCase)) {
      var identity = host.GetVolumeIdentity(group.First().ResolvedPath);
      var reserved = group.Sum(m => m.ReserveBytes);
      bytesFree += Math.Max(0, identity.BytesFree - reserved);
      bytesTotal += identity.BytesTotal;
    }

    return new() {
      PoolId = pool.PoolId,
      Name = pool.Name,
      Members = members,
      Warnings = warnings,
      SharedFailureDomains = domains,
      BytesFree = bytesFree,
      BytesTotal = bytesTotal,
    };
  }

}
