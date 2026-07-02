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

  public static int Health(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, PoolHealthOptions options) {
    var (pool, online, duplication) = _Open(host, provider, remoteResolver, options.Pool);
    var ios = online.Select(m => m.io).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));
    var service = new HealthService(ios, new SmartctlMonitor(), new IntegrityService(ios), new MediaLifecycle(ios, journal, duplication));

    var report = options.Fix ? service.CheckAndCorrect() : service.Check();
    Console.WriteLine($"Pool '{pool.Name}' — {(report.Healthy ? "healthy" : "attention needed")}");
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

  private static PoolMemberDefinition _FindMember(PoolRef pool, string? memberRef) {
    if (memberRef == null)
      throw new ManifestException("Specify the member with --member (path or id)");

    var member = Guid.TryParse(memberRef, out var id)
      ? pool.Manifest.FindMember(id)
      : pool.Manifest.Members.FirstOrDefault(m => m.Path.Equals(memberRef, StringComparison.OrdinalIgnoreCase));
    return member ?? throw new ManifestException($"Pool '{pool.Name}' has no member '{memberRef}'");
  }

}
