using DivisonM.Backends;
using DivisonM.Vfs;
using DivisonM.Vfs.Engine;

namespace DivisonM.Mount;

/// <summary>
/// Administrative pool operations that work on the member set directly (no mount needed):
/// health check/correct (G16) and media lifecycle — restore, remove-media, replace-media
/// (§1.1). Builds the engine members the same way the mount host does.
/// </summary>
internal static class PoolOpsCommand {

  private static (PoolRef pool, IReadOnlyList<(PoolMemberDefinition def, IVolumeIO io)> online, int duplication) _Open(
    IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, string poolNameOrId) {
    var pools = provider.Discover();
    var pool = Guid.TryParse(poolNameOrId, out var id)
      ? pools.FirstOrDefault(p => p.PoolId == id)
      : pools.FirstOrDefault(p => p.Name.Equals(poolNameOrId, StringComparison.OrdinalIgnoreCase));
    if (pool == null)
      throw new ManifestException($"No pool '{poolNameOrId}'");

    var health = provider.Inspect(pool);
    var online = health.OnlineMembers.Select(m => {
      var def = pool.Manifest.FindMember(m.MemberId)!;
      IVolumeIO io = MemberSchemes.IsRemoteMember(def)
        ? remoteResolver.OpenVolume(def)
        : new LocalVolumeIO(m.MemberId, m.Label ?? m.ResolvedPath, m.ResolvedPath, m.PhysicalVolumeId);
      return (def, io);
    }).ToArray();

    var config = ConfigResolver.ResolveEffective(
      host.FileExists(Path.Combine(host.ConfigRoot, "config.json")) ? host.ReadAllText(Path.Combine(host.ConfigRoot, "config.json")) : null,
      pool.Manifest.Defaults?.GetRawText());
    return (pool, online, Math.Max(1, config.Duplication ?? 1));
  }

  /// <summary>Runs the health scan (optionally correcting) and returns the structured report — shared by the CLI verb and the daemon's API.</summary>
  public static HealthReport RunHealth(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, string poolNameOrId, bool fix) {
    var (_, online, duplication) = _Open(host, provider, remoteResolver, poolNameOrId);
    var ios = online.Select(m => m.io).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));
    var service = new HealthService(ios, new SmartctlMonitor(), new IntegrityService(ios), new MediaLifecycle(ios, journal, duplication));
    return fix ? service.CheckAndCorrect() : service.Check();
  }

  public static int Health(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, PoolHealthOptions options) {
    var report = RunHealth(host, provider, remoteResolver, options.Pool, options.Fix);
    Console.WriteLine($"Pool '{options.Pool}' — {(report.Healthy ? "healthy" : "attention needed")}");
    Console.WriteLine($"  Under-duplicated files: {report.UnderDuplicatedFiles}");
    if (report.Corrected)
      Console.WriteLine($"  Copies repaired/created: {report.CopiesRepaired}");

    foreach (var issue in report.IntegrityIssues)
      Console.WriteLine($"  [{issue.Kind}] {issue.Path}: {issue.Message}");

    foreach (var member in report.Members)
      Console.WriteLine($"  {member.Member}: {member.Smart.Health}" +
        (member.Smart.TemperatureCelsius is { } t ? $", {t}°C" : "") +
        (member.Smart.ReallocatedSectors is { } r and > 0 ? $", {r} reallocated sectors" : ""));

    return report.Healthy ? 0 : 1;
  }

  public static int Restore(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, PoolRestoreOptions options) {
    var (pool, online, duplication) = _Open(host, provider, remoteResolver, options.Pool);
    var ios = online.Select(m => m.io).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));
    var report = new MediaLifecycle(ios, journal, duplication).RestorePool();
    Console.WriteLine($"Restored pool '{pool.Name}': {report.CopiesCreated} copy(ies) created/promoted to duplication level {duplication}.");
    return 0;
  }

  public static int RemoveMedia(IHostEnvironment host, ManifestStore store, IPoolProvider provider, PoolLifecycle lifecycle, BackendMemberResolver remoteResolver, PoolRemoveMediaOptions options) {
    var (pool, online, duplication) = _Open(host, provider, remoteResolver, options.Member == null ? options.Pool : options.Pool);
    var member = _FindMember(pool, options.Member);
    var ios = online.Select(m => m.io).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));

    new MediaLifecycle(ios, journal, duplication).ScatterAndRemove(member.MemberId);
    lifecycle.RemoveMember(pool.Manifest, member.MemberId); // drop from the manifest once its data is scattered
    Console.WriteLine($"Removed media '{member.Label ?? member.Path}' from pool '{pool.Name}'; data scattered over the remaining members.");
    return 0;
  }

  public static int ReplaceMedia(IHostEnvironment host, ManifestStore store, IPoolProvider provider, PoolLifecycle lifecycle, BackendMemberResolver remoteResolver, PoolReplaceMediaOptions options) {
    var (pool, online, duplication) = _Open(host, provider, remoteResolver, options.Pool);
    var oldMember = _FindMember(pool, options.Old);

    // the replacement is a fresh local member folder (created if missing)
    if (!host.DirectoryExists(options.New))
      host.CreateDirectory(options.New);
    var replacementId = Guid.NewGuid();
    IVolumeIO replacement = new LocalVolumeIO(replacementId, options.New, options.New, host.GetVolumeIdentity(options.New).PhysicalVolumeId);

    var ios = online.Select(m => m.io).Append(replacement).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));
    new MediaLifecycle(ios, journal, duplication).Replace(oldMember.MemberId, replacement);

    var withReplacement = lifecycle.AddMember(pool.Manifest, new(options.New), force: true);
    lifecycle.RemoveMember(withReplacement, oldMember.MemberId);
    Console.WriteLine($"Replaced media '{oldMember.Label ?? oldMember.Path}' with '{options.New}' in pool '{pool.Name}'.");
    return 0;
  }

  public sealed record BrowseMember(Guid Id, string Label);
  public sealed record BrowsePresence(Guid MemberId, bool Primary, bool Shadow);
  public sealed record BrowseEntry(string Name, bool IsDirectory, long Length, IReadOnlyList<BrowsePresence> Presence);
  public sealed record BrowseResult(string Path, IReadOnlyList<BrowseMember> Members, IReadOnlyList<BrowseEntry> Entries);

  /// <summary>
  /// Union directory listing across all online members with per-member placement: for every
  /// entry, which member holds a primary and which a shadow copy (FR-UI-MAP — "where exactly
  /// is my data"). Read-only; sidecars and shadow folders are hidden like in the mounted view.
  /// </summary>
  public static BrowseResult Browse(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, string poolNameOrId, string? relativePath) {
    var (_, online, _) = _Open(host, provider, remoteResolver, poolNameOrId);
    var rel = (relativePath ?? "").Replace('\\', '/').Trim('/');

    var union = new Dictionary<string, (bool dir, long len)>(StringComparer.OrdinalIgnoreCase);
    var presence = new Dictionary<string, Dictionary<Guid, (bool primary, bool shadow)>>(StringComparer.OrdinalIgnoreCase);

    foreach (var (def, io) in online)
    foreach (var shadow in new[] { false, true }) {
      VolumeEntry[] list;
      try {
        if (!io.FolderExists(rel, shadow))
          continue;

        list = [.. io.List(rel, shadow)];
      } catch (PoolFsException) {
        continue; // member unreadable right now — show what the others have
      }

      foreach (var entry in list) {
        if (PoolPaths.IsHiddenName(entry.Name))
          continue;

        union[entry.Name] = union.TryGetValue(entry.Name, out var known)
          ? (known.dir || entry.IsDirectory, Math.Max(known.len, entry.Length))
          : (entry.IsDirectory, entry.Length);

        if (!presence.TryGetValue(entry.Name, out var byMember))
          presence[entry.Name] = byMember = [];
        var flags = byMember.GetValueOrDefault(def.MemberId);
        byMember[def.MemberId] = (flags.primary || !shadow, flags.shadow || shadow);
      }
    }

    var members = online.Select(m => new BrowseMember(m.def.MemberId, m.def.Label ?? m.def.Path)).ToArray();
    var entries = union
      .OrderByDescending(e => e.Value.dir).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
      .Select(e => new BrowseEntry(e.Key, e.Value.dir, e.Value.len, [.. members.Select(m => {
        var flags = presence[e.Key].GetValueOrDefault(m.Id);
        return new BrowsePresence(m.Id, flags.primary, flags.shadow);
      })]))
      .ToArray();

    return new(rel, members, entries);
  }

  private static PoolMemberDefinition _FindMember(PoolRef pool, string? memberRef) {
    if (memberRef == null)
      throw new ManifestException("Specify the member with --member (path or id)");

    var member = Guid.TryParse(memberRef, out var id)
      ? pool.Manifest.FindMember(id)
      : pool.Manifest.Members.FirstOrDefault(m => m.Path.Equals(memberRef, StringComparison.OrdinalIgnoreCase));
    return member ?? throw new ManifestException($"Pool '{pool.Name}' has no member '{memberRef}'");
  }

}
