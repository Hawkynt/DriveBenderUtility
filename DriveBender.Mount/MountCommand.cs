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

  public static int Run(IHostEnvironment host, ManifestStore store, IPoolProvider provider, BackendMemberResolver remoteResolver, MountOptions options) {
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

    if (pool == null) {
      Console.Error.WriteLine($"No pool or manifest '{options.Manifest}' found.");
      return 2;
    }

    var target = options.Target ?? pool.Manifest.Mount?.Target;
    if (string.IsNullOrWhiteSpace(target)) {
      Console.Error.WriteLine("No mount target: pass --target or set mount.target in the manifest.");
      return 1;
    }

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

    if (members.Length == 0) {
      Console.Error.WriteLine($"Pool '{pool.Name}' has no online member — refusing to mount.");
      return 2;
    }

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

    return _MountPlatform(fs, target, label, options);
  }

  private static int _MountPlatform(PoolFileSystem fs, string target, string label, MountOptions options) {
#if WINDOWS
    if (!Windows.WinFspMountHost.IsWinFspAvailable()) {
      Console.Error.WriteLine("WinFsp is not installed — get it from https://winfsp.dev and retry.");
      return 3;
    }

    using var mountHost = new Windows.WinFspMountHost();
    mountHost.Mount(fs, target, label, options.ReadOnly);

    var scheduler = fs.CreateScheduler();
    using var pump = new Timer(_ => scheduler.Pump(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    Console.WriteLine($"Pool mounted at '{target}'. Press Ctrl+C to unmount.");
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => {
      e.Cancel = true;
      stop.Set();
    };
    stop.Wait();

    Console.WriteLine("Unmounting (flushing dirty state)…");
    mountHost.Unmount();
    scheduler.Quiesce();
    fs.Unmount(); // clean unmount flushes everything (FR-CLEAN-UNMOUNT)
    return 0;
#else
    Console.Error.WriteLine("Mounting on this platform requires the FUSE adapter (pending milestone); Windows builds use WinFsp.");
    return 3;
#endif
  }

}
