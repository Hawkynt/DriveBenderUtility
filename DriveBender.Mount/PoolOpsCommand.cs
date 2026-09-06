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

  /// <summary>Refused: the operation was not attempted and nothing was changed.</summary>
  private const int ExitError = 1;


  /// <summary>
  /// Charges a member for a chunk of copying against the limits its manifest entry carries.
  ///
  /// These verbs run against an UNMOUNTED pool, so there is no engine to borrow a limiter from and
  /// one is built here from the same manifest the mount would read. Skipping it would mean the very
  /// operations an operator most wants held down — a replace copies a whole disk — are the only
  /// ones that ignore the limit, and only when run from the CLI, which is where they usually are.
  /// </summary>
  private static Action<IVolumeIO, long> _Limiter(PoolConfig config, IReadOnlyList<(PoolMemberDefinition def, IVolumeIO io)> online) {
    var queues = new VolumeQueues(config, online.ToDictionary(m => m.def.MemberId, m => m.def.Role));
    queues.SetThrottles(online.Select(m => (m.def.MemberId, m.def.EffectiveLimits)));
    return (member, bytes) => queues.Enter(member, IoKind.Background, bytes).Dispose();
  }

  private static (PoolRef pool, IReadOnlyList<(PoolMemberDefinition def, IVolumeIO io)> online, int duplication, bool allowSamePhysical, Action<IVolumeIO, long> admit) _Open(
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
    return (pool, online, Math.Max(1, config.Duplication ?? 1), config.Placement?.ShadowNeverSamePhysical == false, _Limiter(config, online));
  }

  /// <summary>Runs the health scan (optionally deep / correcting) and returns the structured report — shared by the CLI verb and the daemon's API.</summary>
  public static HealthReport RunHealth(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, string poolNameOrId, bool fix, bool deep = false) {
    var (_, online, duplication, allowSamePhysical, admit) = _Open(host, provider, remoteResolver, poolNameOrId);
    var ios = online.Select(m => m.io).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));
    var service = new HealthService(ios, new SmartctlMonitor(), new IntegrityService(ios, admit: admit), new MediaLifecycle(ios, journal, duplication, allowSamePhysical, admit));
    return fix ? service.CheckAndCorrect() : service.Check(deep);
  }

  /// <summary>The wire shape shared by the daemon relay, the pool process and the transient worker.</summary>
  public static string HealthReportJson(HealthReport report) => System.Text.Json.JsonSerializer.Serialize(new {
    ok = true,
    healthy = report.Healthy,
    corrected = report.Corrected,
    deep = report.DeepScan,
    underDuplicatedFiles = report.UnderDuplicatedFiles,
    copiesRepaired = report.CopiesRepaired,
    issues = report.IntegrityIssues.Select(i => new { kind = i.Kind.ToString(), path = i.Path, message = i.Message }),
    members = report.Members.Select(m => new {
      name = m.Member,
      health = m.Smart.Health.ToString(),
      temperatureC = m.Smart.TemperatureCelsius,
      reallocatedSectors = m.Smart.ReallocatedSectors,
      model = m.Smart.Model,
      detail = m.Smart.Detail,
    }),
  });

  /// <summary>
  /// Takes the pool's cross-process engine lock, or explains why the operation is refused.
  ///
  /// Mounting takes this same lock because "two engines over one member set race and corrupt each
  /// other" — and every administrative verb below opens those members and rewrites those files
  /// while never going near it. Running `pool-restore` against a mounted pool started a second
  /// engine relocating copies, journalling to the same log and invalidating caches the first one
  /// could not see. It reported success.
  ///
  /// Held for the whole operation rather than merely checked, so a mount cannot start half way
  /// through either. Read-only verbs do not take it: reading a member while the mount serves from
  /// it is what the mount already does for itself.
  /// </summary>
  private static IDisposable? _TakeEngineLock(MountRegistry registry, PoolRef pool) {
    var held = registry.TryAcquireMountLock(pool.PoolId);
    if (held == null)
      Console.Error.WriteLine(
        $"Pool '{pool.Name}' is mounted, so this would be a second engine writing the same members — refusing. "
        + $"Unmount it first (dbmount unmount {pool.Name}), or run the operation from the manager, "
        + $"which files it through the process that already owns the pool.");

    return held;
  }

  /// <summary>
  /// Hands an operation to the process that already owns a mounted pool, and waits for its answer.
  ///
  /// Refusing when the pool is mounted is correct and, on its own, a dead end: repairing bit rot or
  /// restoring duplication is most wanted on a pool that is up and serving, and "unmount your backup
  /// target first" is not an answer. The mount process already runs exactly these operations for the
  /// manager, over a channel built for it — so the verb routes there instead of doing the work in a
  /// second engine. Same command, same output, executed where it is safe.
  ///
  /// Returns null when nothing is mounted, which is the caller's signal to do the work itself.
  /// </summary>
  private static string? _RelayToMount(MountRegistry registry, PoolRef pool, string op, TimeSpan timeout) {
    if (registry.Find(pool.Name) == null && registry.Find(pool.PoolId.ToString()) == null)
      return null;

    var id = Guid.NewGuid().ToString("N");
    registry.RequestOp(pool.PoolId, id, op);
    Console.WriteLine($"Pool '{pool.Name}' is mounted — running '{op}' inside the process that owns it.");

    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline) {
      if (registry.TryTakeOpResult(pool.PoolId, id) is { } json)
        return json;

      Thread.Sleep(250);
    }

    registry.ClearOp(pool.PoolId, id);
    throw new PoolFsException(PoolFsError.IoError,
      $"The mounted pool '{pool.Name}' did not answer '{op}' within {timeout.TotalMinutes:F0} minutes.");
  }

  public static int Health(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, MountRegistry registry, PoolHealthOptions options) {
    // Only --fix writes; a plain check reads the members, which is what the mount is doing anyway.
    IDisposable? exclusive = null;
    if (options.Fix) {
      var pools = provider.Discover();
      var target = Guid.TryParse(options.Pool, out var id)
        ? pools.FirstOrDefault(p => p.PoolId == id)
        : pools.FirstOrDefault(p => p.Name.Equals(options.Pool, StringComparison.OrdinalIgnoreCase));

      if (target != null) {
        // always "fix": CheckAndCorrect already scrubs everything and reports DeepScan, whereas
        // "health-deep" is the READ-ONLY deep check. Relaying --fix --deep to that one would have
        // reported the rot in detail and repaired none of it.
        if (_RelayToMount(registry, target, "fix", TimeSpan.FromHours(12)) is { } relayed) {
          Console.WriteLine(relayed);
          return 0;
        }

        if ((exclusive = _TakeEngineLock(registry, target)) == null)
          return ExitError;
      }
    }

    using var held = exclusive;
    var report = RunHealth(host, provider, remoteResolver, options.Pool, options.Fix, options.Deep);
    if (options.Json) {
      Console.WriteLine(HealthReportJson(report));
      return report.Healthy ? 0 : 1;
    }

    Console.WriteLine($"Pool '{options.Pool}' — {(report.Healthy ? "healthy" : "attention needed")}{(report.DeepScan ? " (deep scan)" : "")}");
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

  public static int Restore(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, MountRegistry registry, PoolRestoreOptions options) {
    var (pool, online, duplication, allowSamePhysical, admit) = _Open(host, provider, remoteResolver, options.Pool);
    if (_RelayToMount(registry, pool, "restore", TimeSpan.FromHours(12)) is { } relayed) {
      Console.WriteLine(relayed);
      return 0;
    }

    using var exclusive = _TakeEngineLock(registry, pool);
    if (exclusive == null)
      return ExitError;

    var ios = online.Select(m => m.io).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));
    var report = new MediaLifecycle(ios, journal, duplication, allowSamePhysical, admit).RestorePool();
    if (options.Json) {
      Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ok = true, copiesCreated = report.CopiesCreated }));
      return 0;
    }

    Console.WriteLine($"Restored pool '{pool.Name}': {report.CopiesCreated} copy(ies) created/promoted to duplication level {duplication}.");
    return 0;
  }

  /// <summary>
  /// The recycle bin's three verbs (§6.14).
  ///
  /// The engine has held a trash since deletes were first journalled — it moves a deleted file
  /// aside instead of unlinking it, keeps a sidecar saying where it came from, and applies a
  /// retention and size policy in the background. None of it was reachable: no verb, no endpoint,
  /// no screen. A recycle bin nobody can open is a disk-space cost with no benefit, and for a pool
  /// used as a backup target it is the single most likely thing an operator needs in a hurry.
  /// </summary>
  public static int TrashList(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, PoolTrashListOptions options) {
    var (pool, online, _, _, _) = _Open(host, provider, remoteResolver, options.Pool);
    var ios = online.Select(m => m.io).ToArray();
    var entries = new PoolTrash(ios, new Journal(new MemberJournalStore(ios)), () => DateTime.UtcNow).List();

    if (options.Json) {
      Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
        ok = true,
        entries = entries.Select(e => new { path = e.OriginalPath, deletedUtc = e.DeletedUtc, length = e.Length }),
      }));
      return 0;
    }

    if (entries.Count == 0) {
      Console.WriteLine($"Pool '{pool.Name}': the recycle bin is empty.");
      return 0;
    }

    Console.WriteLine($"Pool '{pool.Name}': {entries.Count} item(s) in the recycle bin.");
    foreach (var entry in entries.OrderByDescending(e => e.DeletedUtc))
      Console.WriteLine($"  {entry.DeletedUtc:yyyy-MM-dd HH:mm:ss}  {entry.Length,12:N0}  {entry.OriginalPath}");

    return 0;
  }

  public static int TrashRestore(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, MountRegistry registry, PoolTrashRestoreOptions options) {
    var (pool, online, _, _, _) = _Open(host, provider, remoteResolver, options.Pool);
    using var exclusive = _TakeEngineLock(registry, pool);
    if (exclusive == null)
      return ExitError;

    var ios = online.Select(m => m.io).ToArray();
    var trash = new PoolTrash(ios, new Journal(new MemberJournalStore(ios)), () => DateTime.UtcNow);
    var normalized = PoolPaths.Normalize(options.Path);
    if (trash.Restore(normalized) is not { } restored) {
      Console.Error.WriteLine($"No recycle-bin entry for '{options.Path}' in pool '{pool.Name}'. Run pool-trash-list to see what is there.");
      return ExitError;
    }

    // Restored as a single copy on the member that was holding it. Duplication is re-established by
    // the healer on the next mount, or by pool-restore now — said plainly rather than left for the
    // operator to discover that their recovered file is not yet redundant.
    Console.WriteLine($"Restored '{restored.restoredPath}' to pool '{pool.Name}' from '{restored.member.DisplayName}'.");
    Console.WriteLine("  It is back as a single copy; mounting the pool (or pool-restore) re-establishes its duplication level.");
    return 0;
  }

  public static int TrashPurge(IHostEnvironment host, IPoolProvider provider, BackendMemberResolver remoteResolver, MountRegistry registry, PoolTrashPurgeOptions options) {
    var (pool, online, _, _, _) = _Open(host, provider, remoteResolver, options.Pool);
    using var exclusive = _TakeEngineLock(registry, pool);
    if (exclusive == null)
      return ExitError;

    var ios = online.Select(m => m.io).ToArray();
    var config = ConfigResolver.ResolveEffective(
      host.FileExists(System.IO.Path.Combine(host.ConfigRoot, "config.json")) ? host.ReadAllText(System.IO.Path.Combine(host.ConfigRoot, "config.json")) : null,
      pool.Manifest.Defaults?.GetRawText());

    var retention = DurationSpec.Parse(config.Trash?.Retention ?? "7d");
    var total = ios.Where(io => io.BytesTotal > 0).Sum(io => io.BytesTotal);
    var maxSize = SizeSpec.Parse(config.Trash?.MaxSize ?? "5%").ResolveBytes(total);
    var purged = new PoolTrash(ios, new Journal(new MemberJournalStore(ios)), () => DateTime.UtcNow).Purge(retention, maxSize);

    Console.WriteLine($"Pool '{pool.Name}': purged {purged} item(s) past a {retention} retention or a {maxSize:N0}-byte bin.");
    return 0;
  }

  public static int RemoveMedia(IHostEnvironment host, ManifestStore store, IPoolProvider provider, PoolLifecycle lifecycle, BackendMemberResolver remoteResolver, MountRegistry registry, PoolRemoveMediaOptions options) {
    var (pool, online, duplication, allowSamePhysical, admit) = _Open(host, provider, remoteResolver, options.Member == null ? options.Pool : options.Pool);
    using var exclusive = _TakeEngineLock(registry, pool);
    if (exclusive == null)
      return ExitError;

    var member = _FindMember(pool, options.Member);
    var ios = online.Select(m => m.io).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));

    new MediaLifecycle(ios, journal, duplication, allowSamePhysical, admit).ScatterAndRemove(member.MemberId);
    lifecycle.RemoveMember(pool.Manifest, member.MemberId); // drop from the manifest once its data is scattered
    Console.WriteLine($"Removed media '{member.Label ?? member.Path}' from pool '{pool.Name}'; data scattered over the remaining members.");
    return 0;
  }

  public static int ReplaceMedia(IHostEnvironment host, ManifestStore store, IPoolProvider provider, PoolLifecycle lifecycle, BackendMemberResolver remoteResolver, MountRegistry registry, PoolReplaceMediaOptions options) {
    var (pool, online, duplication, allowSamePhysical, admit) = _Open(host, provider, remoteResolver, options.Pool);
    using var exclusive = _TakeEngineLock(registry, pool);
    if (exclusive == null)
      return ExitError;

    var oldMember = _FindMember(pool, options.Old);

    // the replacement is a fresh local member folder (created if missing)
    if (!host.DirectoryExists(options.New))
      host.CreateDirectory(options.New);
    var replacementId = Guid.NewGuid();
    IVolumeIO replacement = new LocalVolumeIO(replacementId, options.New, options.New, host.GetVolumeIdentity(options.New).PhysicalVolumeId);

    var ios = online.Select(m => m.io).Append(replacement).ToArray();
    var journal = new Journal(new MemberJournalStore(ios));
    new MediaLifecycle(ios, journal, duplication, allowSamePhysical, admit).Replace(oldMember.MemberId, replacement);

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
    var (_, online, _, _, _) = _Open(host, provider, remoteResolver, poolNameOrId);
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
