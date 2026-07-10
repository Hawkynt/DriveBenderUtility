using DivisonM.Backends;
using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;

namespace DivisonM.Mount;

/// <summary>
/// The mount host plumbing (CMP-HOST): resolves (manifest, target), builds the engine
/// over the resolved members, and hands it to the platform adapter — WinFsp on Windows
/// (FR-MOUNT-CLI, FR-MOUNT-WIN-CLI). Every invocation path is "mount this manifest at
/// this target".
/// </summary>
internal static class MountCommand {

  public static int Run(IHostEnvironment host, ManifestStore store, IPoolProvider provider, BackendMemberResolver remoteResolver, MountRegistry registry, MountOptions options) {
    // key any failure report by the requested pool id so the launching daemon can read the real
    // reason even when this child was elevated (its stderr can't be captured across the UAC boundary)
    Guid? errorKey = Guid.TryParse(options.Manifest, out var requestedId) ? requestedId : null;
    try {
      // resolve the manifest argument: a *.json file path, a pool id, or a pool name
      PoolRef? pool;
      if (File.Exists(options.Manifest)) {
        var manifest = ManifestSerializer.Parse(File.ReadAllText(options.Manifest));
        pool = new(manifest.PoolId, manifest.Name, manifest.IsVirtual, manifest);
      } else {
        var pools = provider.Discover();
        pool = Guid.TryParse(options.Manifest, out var id)
          ? pools.FirstOrDefault(p => p.PoolId == id)
          : pools.FirstOrDefault(p => p.Name.Equals(options.Manifest, StringComparison.OrdinalIgnoreCase));
      }

      if (pool == null)
        return _Fail(registry, errorKey, $"No pool or manifest '{options.Manifest}' found.", 2);

      errorKey = pool.PoolId;

      var target = options.Target ?? pool.Manifest.Mount?.Target;
      if (string.IsNullOrWhiteSpace(target))
        return _Fail(registry, errorKey, "No mount target — choose a drive letter (e.g. X:) or an empty folder to mount at.", 1);
      target = MemberSchemes.ExpandLocal(target); // a ~/… or %VAR% mount target resolves like a member path

      // resolve members and surface health before serving (SAFE-OFFLINE, SAFE-PHYS)
      var health = provider.Inspect(pool);
      foreach (var warning in health.Warnings)
        Console.WriteLine($"! {warning}");

      var members = health.OnlineMembers.Select(m => {
        var definition = pool.Manifest.FindMember(m.MemberId)!;
        IVolumeIO io = MemberSchemes.IsRemoteMember(definition)
          ? remoteResolver.OpenVolume(definition)
          : new LocalVolumeIO(m.MemberId, m.Label ?? m.ResolvedPath, m.ResolvedPath, m.PhysicalVolumeId);
        // measured so the auto-tier advisor and the dashboard see real per-member latency (FR-AUTO-TIER)
        return new EngineMember(new MeasuredVolumeIO(io), m.Role, m.ReserveBytes);
      }).ToArray();

      if (members.Length == 0)
        return _Fail(registry, errorKey, $"Pool '{pool.Name}' has no online member — refusing to mount.", 2);

      // effective config: built-in defaults ← global file ← the manifest's defaults block (§8)
      var globalConfigPath = Path.Combine(host.ConfigRoot, "config.json");
      var globalJson = host.FileExists(globalConfigPath) ? host.ReadAllText(globalConfigPath) : null;
      var poolJson = pool.Manifest.Defaults?.GetRawText();
      var config = ConfigResolver.ResolveEffective(globalJson, poolJson);
      ConfigValidator.Validate(config, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
      ConfigValidator.ValidateTierAssignments(pool.Manifest, config);

      var cacheConfig = config.Caches != null && config.Caches.TryGetValue(config.Cache?.Use ?? "global", out var found)
        ? found
        : new CacheInstanceConfig { Size = "4GiB" };
      var cache = new CacheInstance(config.Cache?.Use ?? "global", cacheConfig);

      var label = pool.Manifest.Mount?.VolumeLabel ?? pool.Name;
      var fs = new PoolFileSystem(pool.PoolId, members, cache, config);

      return _MountPlatform(host, store, fs, pool, [.. members.Select(m => m.Io)], config, target, label, registry, options);
    } catch (Exception e) {
      // record the reason before it bubbles to Program's stderr guard (invisible when elevated);
      // unwrap so a driver mismatch surfaces "incorrect dll version …", not the generic wrapper
      if (errorKey != null)
        registry.ReportError(errorKey.Value, _Unwrap(e));
      throw;
    }
  }

  /// <summary>The innermost message — TypeInitializationException/wrappers hide the real cause otherwise.</summary>
  private static string _Unwrap(Exception e) {
    var inner = e;
    while (inner.InnerException != null)
      inner = inner.InnerException;
    return inner.Message;
  }

  private static int _Fail(MountRegistry registry, Guid? errorKey, string message, int code) {
    Console.Error.WriteLine(message);
    if (errorKey != null)
      registry.ReportError(errorKey.Value, message);
    return code;
  }

  /// <summary>
  /// Returns a human-readable reason the Linux FUSE mountpoint can't be used (missing, not a
  /// directory, or not writable by this user), or null when it is fine. An unprivileged FUSE mount
  /// requires write access to the mountpoint — mounting on a root-owned dir like <c>/mnt</c> fails.
  /// </summary>
  private static string? _UnusableMountpoint(string target) {
    if (!Directory.Exists(target)) {
      if (File.Exists(target))
        return $"Mount target '{target}' is a file, not a directory. Pick an empty directory to mount at.";
      return $"Mount target '{target}' does not exist. Create the (empty) directory first — e.g. mkdir -p \"{target}\" — then mount.";
    }

    // fusermount3 needs the invoking user to be able to write the mountpoint; probe it directly
    try {
      var probe = Path.Combine(target, "." + Guid.NewGuid().ToString("N") + ".dbwritetest");
      using (File.Create(probe)) { }
      File.Delete(probe);
      return null;
    } catch (Exception e) when (e is UnauthorizedAccessException or IOException) {
      var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      return $"Mount target '{target}' is not writable by your user, so an unprivileged FUSE mount is refused "
        + $"(fusermount3: \"user has no write access to mountpoint\"). Choose a directory you own — e.g. "
        + $"'{Path.Combine(home, "pool")}' — or create and take ownership of a subdirectory: "
        + $"sudo mkdir -p \"{Path.Combine(target, "pool")}\" && sudo chown \"$USER\" \"{Path.Combine(target, "pool")}\", then mount there.";
    }
  }

  /// <summary>
  /// Re-reads the manifest + global config and applies them to the running mount (CFG.reload,
  /// requested cross-process by the daemon). A raised duplication level immediately creates
  /// the owed copies for existing files instead of waiting for the next write to each.
  /// Returns the new effective config (null when the reload failed).
  /// </summary>
  private static PoolConfig? _ReloadLive(IHostEnvironment host, ManifestStore store, PoolFileSystem fs, PoolRef pool, IVolumeIO[] ios) {
    try {
      var manifest = store.TryLoadRegistry(pool.PoolId) ?? pool.Manifest;
      var globalConfigPath = Path.Combine(host.ConfigRoot, "config.json");
      var globalJson = host.FileExists(globalConfigPath) ? host.ReadAllText(globalConfigPath) : null;
      var config = ConfigResolver.ResolveEffective(globalJson, manifest.Defaults?.GetRawText());
      fs.ReloadConfig(config);
      fs.UpdateMemberRoles(manifest.Members.ToDictionary(m => m.MemberId, m => m.Role)); // tier changes act on new writes immediately

      var duplication = Math.Max(1, config.Duplication ?? 1);
      if (duplication >= 2) {
        var allowSamePhysical = config.Placement?.ShadowNeverSamePhysical == false;
        var report = new MediaLifecycle(ios, fs.Journal, duplication, allowSamePhysical).RestorePool();
        if (report.CopiesCreated > 0)
          DriveBender.Logger($"Live reload: created {report.CopiesCreated} owed cop(ies) to reach duplication level {duplication}");
      }

      return config;
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]Live config reload failed: {e.Message}");
      return null;
    }
  }

  /// <summary>
  /// Auto landing-zone pass (FR-AUTO-TIER, placement.autoLandingZone): feeds measured member
  /// latencies to the advisor; on advice it re-tiers the manifest, applies the roles live and
  /// logs why — the landing zone follows the actually-fastest drive as load shifts.
  /// </summary>
  private static void _AutoTier(IHostEnvironment host, ManifestStore store, PoolFileSystem fs, PoolRef pool, IVolumeIO[] ios, AutoTierAdvisor advisor) {
    try {
      var manifest = store.TryLoadRegistry(pool.PoolId);
      if (manifest == null || manifest.IsVirtual)
        return;

      var speeds = ios.OfType<MeasuredVolumeIO>().Select(m => {
        var definition = manifest.FindMember(m.MemberId);
        return definition == null ? null : new MemberSpeed(m.MemberId, m.DisplayName, m.AverageLatencyMs, m.Samples, definition.Network, definition.Role);
      }).Where(s => s != null).Select(s => s!).ToArray();

      var advice = advisor.Advise(speeds);
      if (advice == null)
        return;

      var lifecycle = new PoolLifecycle(host, store);
      var updated = lifecycle.SetMemberRole(manifest, advice.PromoteToLanding, MemberRole.Landing);
      if (advice.DemoteToCapacity is { } demoted)
        updated = lifecycle.SetMemberRole(updated, demoted, MemberRole.Capacity);

      fs.UpdateMemberRoles(updated.Members.ToDictionary(m => m.MemberId, m => m.Role));
      fs.Activity.Publish(ActivityKind.Rebalance, "", reason: advice.Reason);
      DriveBender.Logger($"[AutoTier]{advice.Reason}");
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]auto-tier pass failed: {e.Message}");
    }
  }

  /// <summary>
  /// Executes a manager-filed operation INSIDE the pool's own process (the pool cares for its
  /// pool; the manager only relays): health scan, correcting fix, or duplication restore — all
  /// against the live engine's members, off the pump thread, result filed for the manager.
  /// </summary>
  private static void _RunPoolOp(PoolFileSystem fs, PoolRef pool, IVolumeIO[] ios, PoolConfig config, MountRegistry registry, string id, string op) {
    string json;
    try {
      var duplication = Math.Max(1, config.Duplication ?? 1);
      var allowSamePhysical = config.Placement?.ShadowNeverSamePhysical == false;
      var media = new MediaLifecycle(ios, fs.Journal, duplication, allowSamePhysical);
      switch (op) {
        case "health" or "health-deep" or "fix": {
          var service = new HealthService(ios, new SmartctlMonitor(), fs.Integrity, media);
          var report = op == "fix" ? service.CheckAndCorrect() : service.Check(deep: op == "health-deep");
          json = PoolOpsCommand.HealthReportJson(report);
          break;
        }
        case "restore": {
          var report = media.RestorePool();
          json = System.Text.Json.JsonSerializer.Serialize(new { ok = true, copiesCreated = report.CopiesCreated });
          break;
        }
        default:
          json = System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = $"unknown pool operation '{op}'" });
          break;
      }
    } catch (Exception e) {
      json = System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = e.Message });
    }

    registry.WriteOpResult(pool.PoolId, id, json);
  }

  private static int _MountPlatform(IHostEnvironment host, ManifestStore store, PoolFileSystem fs, PoolRef pool, IVolumeIO[] ios, PoolConfig config, string target, string label, MountRegistry registry, MountOptions options) {
#if WINDOWS
    // WinFsp preferred (richer semantics); Dokan (LGPL) as the no-extra-install fallback (§4.1)
    IDisposable mountHost;
    Action unmountAction;
    if (Windows.WinFspMountHost.IsWinFspAvailable()) {
      var winFsp = new Windows.WinFspMountHost();
      winFsp.Mount(fs, target, label, options.ReadOnly);
      mountHost = winFsp;
      unmountAction = winFsp.Unmount;
    } else if (Windows.DokanMountHost.IsDokanAvailable()) {
      var dokan = new Windows.DokanMountHost();
      dokan.Mount(fs, target, label, options.ReadOnly);
      mountHost = dokan;
      unmountAction = dokan.Unmount;
    } else {
      return _Fail(registry, pool.PoolId, "No filesystem driver found — install WinFsp (https://winfsp.dev) or Dokan (https://dokan-dev.github.io) and retry.", 3);
    }

    var backend = mountHost is Windows.WinFspMountHost ? "winfsp" : "dokan";
    var entry = new MountEntry { PoolId = pool.PoolId, Name = pool.Name, Target = target, ProcessId = Environment.ProcessId, Backend = backend, StartedUtc = DateTime.UtcNow.ToString("O") };
    registry.Register(entry);
    var metrics = new MetricsPublisher(host);
    using (mountHost) {
      var scheduler = fs.CreateScheduler();
      using var stop = new ManualResetEventSlim();
      var currentConfig = config;
      var advisor = new AutoTierAdvisor();
      var tick = 0L;
      using var pump = new Timer(_ => {
        // an unhandled exception in a Timer callback kills the process — the background pump
        // must never take the driver-event loop down with it (process-isolation hardening)
        try {
          scheduler.Pump();
          metrics.Publish(fs, entry, ios); // live snapshot for the serve daemon (§6.13)
          if (registry.ConsumeReload(pool.PoolId)) // the daemon changed settings — apply them live
            currentConfig = _ReloadLive(host, store, fs, pool, ios) ?? currentConfig;
          foreach (var (opId, op) in registry.ConsumeOps(pool.PoolId)) { // manager-filed pool work runs HERE, in the pool's process
            var cfg = currentConfig;
            new Thread(() => _RunPoolOp(fs, pool, ios, cfg, registry, opId, op)) { IsBackground = true }.Start();
          }
          if (++tick % 30 == 0 && currentConfig.Placement?.AutoLandingZone == true)
            _AutoTier(host, store, fs, pool, ios, advisor);
          if (registry.StopRequested(pool.PoolId)) // another dbmount asked for a clean unmount
            stop.Set();
        } catch (Exception e) {
          DriveBender.Logger($"[Warning]background pump tick failed: {e.Message}");
        }
      }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

      Console.WriteLine($"Pool mounted at '{target}'. Press Ctrl+C or run 'dbmount unmount {target}' to unmount.");
      Console.CancelKeyPress += (_, e) => {
        e.Cancel = true;
        stop.Set();
      };
      stop.Wait();

      Console.WriteLine("Unmounting (flushing dirty state)…");
      unmountAction();
      scheduler.Quiesce();
      fs.Unmount(); // clean unmount flushes everything (FR-CLEAN-UNMOUNT)
      registry.Unregister(pool.PoolId);
      metrics.Remove(pool.PoolId);
    }

    return 0;
#else
    if (!OperatingSystem.IsLinux())
      return _Fail(registry, pool.PoolId, "Mounting is supported on Windows (WinFsp build) and Linux (FUSE); this platform has no adapter.", 3);

    if (!Linux.LinuxFuseMountHost.IsFuseAvailable())
      return _Fail(registry, pool.PoolId, "FUSE is not available (/dev/fuse missing) — install fuse3 and retry.", 3);

    // Recover from a stale mount first: a previous mount whose process died without unmounting
    // leaves the target as a dead FUSE mount, and mounting over it fails ("fusermount3: failed to
    // access mountpoint … Permission denied"). Detach the dead mount so the remount can proceed.
    if (Linux.LinuxFuseMountHost.TryClearStaleMount(target))
      Console.WriteLine($"Cleared a stale mount left on '{target}' before remounting.");
    if (Linux.LinuxFuseMountHost.IsMountpoint(target))
      return _Fail(registry, pool.PoolId, $"Something is already mounted at '{target}'. Unmount it first — fusermount3 -u \"{target}\" — or pick another mount location.", 1);

    // Preflight the mountpoint BEFORE libfuse: an unprivileged FUSE mount needs write access to the
    // target, and fusermount3's refusal ("user has no write access to mountpoint") gets masked as a
    // bare EINTR when a runtime signal interrupts libfuse's waitpid. Catching it here yields a clear,
    // actionable message instead of "Interrupted system call (4)".
    if (_UnusableMountpoint(target) is { } reason)
      return _Fail(registry, pool.PoolId, reason, 1);

    var fuseEntry = new MountEntry { PoolId = pool.PoolId, Name = pool.Name, Target = target, ProcessId = Environment.ProcessId, Backend = "fuse", StartedUtc = DateTime.UtcNow.ToString("O") };
    var fuseMetrics = new MetricsPublisher(host);
    var fuseConfig = config;
    var fuseAdvisor = new AutoTierAdvisor();
    var fuseTick = 0L;
    return Linux.LinuxFuseMountHost.Run(fs, target, options.ReadOnly,
      onMounted: () => registry.Register(fuseEntry),
      stopRequested: () => registry.StopRequested(pool.PoolId),
      onUnmounted: () => { registry.Unregister(pool.PoolId); fuseMetrics.Remove(pool.PoolId); },
      onTick: () => {
        fuseMetrics.Publish(fs, fuseEntry, ios);
        if (registry.ConsumeReload(pool.PoolId))
          fuseConfig = _ReloadLive(host, store, fs, pool, ios) ?? fuseConfig;
        foreach (var (opId, op) in registry.ConsumeOps(pool.PoolId)) {
          var cfg = fuseConfig;
          new Thread(() => _RunPoolOp(fs, pool, ios, cfg, registry, opId, op)) { IsBackground = true }.Start();
        }
        if (++fuseTick % 30 == 0 && fuseConfig.Placement?.AutoLandingZone == true)
          _AutoTier(host, store, fs, pool, ios, fuseAdvisor);
      });
#endif
  }

}
