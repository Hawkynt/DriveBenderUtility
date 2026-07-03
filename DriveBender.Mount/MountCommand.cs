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

      // resolve members and surface health before serving (SAFE-OFFLINE, SAFE-PHYS)
      var health = provider.Inspect(pool);
      foreach (var warning in health.Warnings)
        Console.WriteLine($"! {warning}");

      var members = health.OnlineMembers.Select(m => {
        var definition = pool.Manifest.FindMember(m.MemberId)!;
        IVolumeIO io = MemberSchemes.IsRemoteMember(definition)
          ? remoteResolver.OpenVolume(definition)
          : new LocalVolumeIO(m.MemberId, m.Label ?? m.ResolvedPath, m.ResolvedPath, m.PhysicalVolumeId);
        return new EngineMember(io, m.Role, m.ReserveBytes);
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

      return _MountPlatform(host, store, fs, pool, [.. members.Select(m => m.Io)], target, label, registry, options);
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
  /// Re-reads the manifest + global config and applies them to the running mount (CFG.reload,
  /// requested cross-process by the daemon). A raised duplication level immediately creates
  /// the owed copies for existing files instead of waiting for the next write to each.
  /// </summary>
  private static void _ReloadLive(IHostEnvironment host, ManifestStore store, PoolFileSystem fs, PoolRef pool, IVolumeIO[] ios) {
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
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]Live config reload failed: {e.Message}");
    }
  }

  private static int _MountPlatform(IHostEnvironment host, ManifestStore store, PoolFileSystem fs, PoolRef pool, IVolumeIO[] ios, string target, string label, MountRegistry registry, MountOptions options) {
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
      using var pump = new Timer(_ => {
        scheduler.Pump();
        metrics.Publish(fs, entry); // live snapshot for the serve daemon (§6.13)
        if (registry.ConsumeReload(pool.PoolId)) // the daemon changed settings — apply them live
          _ReloadLive(host, store, fs, pool, ios);
        if (registry.StopRequested(pool.PoolId)) // another dbmount asked for a clean unmount
          stop.Set();
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

    var fuseEntry = new MountEntry { PoolId = pool.PoolId, Name = pool.Name, Target = target, ProcessId = Environment.ProcessId, Backend = "fuse", StartedUtc = DateTime.UtcNow.ToString("O") };
    var fuseMetrics = new MetricsPublisher(host);
    return Linux.LinuxFuseMountHost.Run(fs, target, options.ReadOnly,
      onMounted: () => registry.Register(fuseEntry),
      stopRequested: () => registry.StopRequested(pool.PoolId),
      onUnmounted: () => { registry.Unregister(pool.PoolId); fuseMetrics.Remove(pool.PoolId); },
      onTick: () => {
        fuseMetrics.Publish(fs, fuseEntry);
        if (registry.ConsumeReload(pool.PoolId))
          _ReloadLive(host, store, fs, pool, ios);
      });
#endif
  }

}
