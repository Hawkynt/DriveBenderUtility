using System.Text.Json;
using CommandLine;
using DivisonM;
using DivisonM.Vfs;

namespace DivisonM.Mount;

internal static class Program {

  private const int ExitOk = 0;
  private const int ExitError = 1;
  private const int ExitNotFound = 2;
  private const int ExitNotImplemented = 3;

  private static int Main(string[] args) {
    DriveBender.Logger = Console.WriteLine;

    var host = new RealHostEnvironment();
    var store = new ManifestStore(host);
    var provider = new PoolProvider(host, store, [new JsonManifestSource(store), new NativeScanSource(host)]);
    var lifecycle = new PoolLifecycle(host, store);

    return Parser.Default.ParseArguments<
      PoolCreateOptions,
      PoolImportOptions,
      PoolExportOptions,
      PoolListOptions,
      PoolAddMemberOptions,
      PoolRemoveMemberOptions,
      PoolAdoptOptions,
      PoolRepairManifestOptions,
      MountOptions,
      UnmountOptions,
      StatusOptions,
      ListOptions
    >(_TranslateVerbs(args)).MapResult(
      (PoolCreateOptions o) => _Guard(() => _PoolCreate(lifecycle, o)),
      (PoolImportOptions o) => _Guard(() => _PoolImport(lifecycle, o)),
      (PoolExportOptions o) => _Guard(() => _PoolExport(provider, lifecycle, o)),
      (PoolListOptions o) => _Guard(() => _PoolList(provider, o.Json)),
      (PoolAddMemberOptions o) => _Guard(() => _PoolAddMember(provider, lifecycle, o)),
      (PoolRemoveMemberOptions o) => _Guard(() => _PoolRemoveMember(provider, lifecycle, o)),
      (PoolAdoptOptions o) => _Guard(() => _PoolAdopt(provider, lifecycle, o)),
      (PoolRepairManifestOptions o) => _Guard(() => _PoolRepairManifest(provider, store, o)),
      (MountOptions o) => _Guard(() => MountCommand.Run(host, store, provider, o)),
      (UnmountOptions o) => _NotImplemented("unmount"),
      (StatusOptions o) => _NotImplemented("status"),
      (ListOptions o) => _Guard(() => _PoolList(provider, o.Json)),
      _ => ExitError
    );
  }

  /// <summary>Accepts the documented two-token form (`dbmount pool create …`) by folding it into the internal "pool-create" verb.</summary>
  private static string[] _TranslateVerbs(string[] args) {
    if (args.Length >= 2 && args[0].Equals("pool", StringComparison.OrdinalIgnoreCase) && !args[1].StartsWith('-'))
      args = ["pool-" + args[1], .. args.Skip(2)];

    return _GroupRepeatedOptions(args, new() {
      ["--member"] = "--member",
      ["-m"] = "--member",
      ["--landing"] = "--landing",
      ["-l"] = "--landing",
    });
  }

  /// <summary>
  /// The documented CLI repeats multi-value options (`--member A --member B`, §6.0.6);
  /// CommandLineParser wants one occurrence with several values, so repeats are folded.
  /// </summary>
  private static string[] _GroupRepeatedOptions(string[] args, Dictionary<string, string> aliasToCanonical) {
    var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var result = new List<string>();
    var firstOccurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < args.Length; ++i) {
      var arg = args[i];
      if (!aliasToCanonical.TryGetValue(arg, out var canonical)) {
        result.Add(arg);
        continue;
      }

      if (!values.TryGetValue(canonical, out var list)) {
        values.Add(canonical, list = []);
        firstOccurrence.Add(canonical, result.Count);
        result.Add(canonical);
      }

      while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
        list.Add(args[++i]);
    }

    // splice collected values in after each option's first occurrence, last first so indices stay valid
    foreach (var (canonical, index) in firstOccurrence.OrderByDescending(pair => pair.Value))
      result.InsertRange(index + 1, values[canonical]);

    return [.. result];
  }

  private static int _Guard(Func<int> action) {
    try {
      return action();
    } catch (NonDestructiveViolationException e) {
      Console.Error.WriteLine($"Refused: {e.Message}");
      return ExitError;
    } catch (ManifestException e) {
      Console.Error.WriteLine($"Error: {e.Message}");
      return ExitError;
    } catch (PoolFsException e) {
      Console.Error.WriteLine($"Error ({e.Error}): {e.Message}");
      return ExitError;
    } catch (IOException e) {
      Console.Error.WriteLine($"I/O error: {e.Message}");
      return ExitError;
    } catch (UnauthorizedAccessException e) {
      Console.Error.WriteLine($"Access denied: {e.Message}");
      return ExitError;
    }
  }

  private static int _NotImplemented(string verb) {
    Console.Error.WriteLine($"'{verb}' requires the mount engine (milestone M1) and is not available yet.");
    return ExitNotImplemented;
  }

  private static PoolRef? _FindPool(IPoolProvider provider, string nameOrId) {
    var pools = provider.Discover();
    if (Guid.TryParse(nameOrId, out var id))
      return pools.FirstOrDefault(p => p.PoolId == id);

    return pools.FirstOrDefault(p => p.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
  }

  private static int _PoolCreate(PoolLifecycle lifecycle, PoolCreateOptions options) {
    var landing = new HashSet<string>(options.LandingZones, StringComparer.OrdinalIgnoreCase);
    var members = options.Members
      .Concat(options.LandingZones)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Select(path => new PoolLifecycle.MemberSpec(path, landing.Contains(path) ? MemberRole.Landing : MemberRole.Capacity))
      .ToArray();

    var manifest = lifecycle.Create(options.Name, members, options.MountTarget, options.Force);
    Console.WriteLine($"Created pool '{manifest.Name}' ({manifest.PoolId}) with {manifest.Members.Count} member(s).");
    return ExitOk;
  }

  private static int _PoolImport(PoolLifecycle lifecycle, PoolImportOptions options) {
    if (!File.Exists(options.ManifestPath)) {
      Console.Error.WriteLine($"Manifest file not found: {options.ManifestPath}");
      return ExitNotFound;
    }

    var manifest = lifecycle.Import(File.ReadAllText(options.ManifestPath), options.Force);
    Console.WriteLine($"Imported pool '{manifest.Name}' ({manifest.PoolId}).");
    return ExitOk;
  }

  private static int _PoolExport(IPoolProvider provider, PoolLifecycle lifecycle, PoolExportOptions options) {
    var pool = _FindPool(provider, options.Pool);
    if (pool == null) {
      Console.Error.WriteLine($"No pool named '{options.Pool}'.");
      return ExitNotFound;
    }

    var json = lifecycle.Export(pool.Manifest);
    if (options.Output == null)
      Console.WriteLine(json);
    else {
      File.WriteAllText(options.Output, json);
      Console.WriteLine($"Exported '{pool.Name}' to {options.Output}.");
    }

    return ExitOk;
  }

  private static int _PoolList(IPoolProvider provider, bool json) {
    var pools = provider.Discover();
    if (json) {
      var report = pools.Select(p => {
        var health = provider.Inspect(p);
        return new {
          poolId = p.PoolId,
          name = p.Name,
          source = p.IsVirtual ? "native-scan" : "manifest",
          degraded = health.IsDegraded,
          bytesFree = health.BytesFree,
          bytesTotal = health.BytesTotal,
          members = health.Members.Select(m => new {
            memberId = m.MemberId,
            path = m.ResolvedPath,
            role = m.Role.ToString().ToLowerInvariant(),
            online = m.Online,
            physicalVolume = m.PhysicalVolumeId,
          }),
          warnings = health.Warnings,
        };
      });
      Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
      return ExitOk;
    }

    if (pools.Count == 0) {
      Console.WriteLine("No pools found.");
      return ExitOk;
    }

    foreach (var pool in pools) {
      var health = provider.Inspect(pool);
      Console.WriteLine($"Pool: {pool.Name} ({pool.PoolId})");
      Console.WriteLine($"  Source: {(pool.IsVirtual ? "native scan (adopt with 'pool adopt' to edit)" : "manifest")}");
      Console.WriteLine($"  Free: {DriveBender.SizeFormatter.Format((ulong)Math.Max(0, health.BytesFree))} / {DriveBender.SizeFormatter.Format((ulong)Math.Max(0, health.BytesTotal))}");
      foreach (var member in health.Members)
        Console.WriteLine($"    - [{member.Role,-8}] {member.ResolvedPath} {(member.Online ? "" : "(OFFLINE)")}");
      foreach (var warning in health.Warnings)
        Console.WriteLine($"  ! {warning}");
      Console.WriteLine();
    }

    return ExitOk;
  }

  private static int _PoolAddMember(IPoolProvider provider, PoolLifecycle lifecycle, PoolAddMemberOptions options) {
    var pool = _FindPool(provider, options.Pool);
    if (pool == null) {
      Console.Error.WriteLine($"No pool named '{options.Pool}'.");
      return ExitNotFound;
    }

    var role = options.Role.ToLowerInvariant() switch {
      "capacity" => MemberRole.Capacity,
      "landing" => MemberRole.Landing,
      "readonly" => MemberRole.ReadOnly,
      _ => throw new ManifestException($"Unknown role '{options.Role}' (capacity | landing | readonly)"),
    };

    var reserve = options.Reserve == null ? 0 : SizeSpec.ParseBytes(options.Reserve);
    var manifest = lifecycle.AddMember(pool.Manifest, new(options.Member, role, ReserveBytes: reserve), options.Force);
    Console.WriteLine($"Added '{options.Member}' to pool '{manifest.Name}' ({manifest.Members.Count} members).");
    return ExitOk;
  }

  private static int _PoolRemoveMember(IPoolProvider provider, PoolLifecycle lifecycle, PoolRemoveMemberOptions options) {
    var pool = _FindPool(provider, options.Pool);
    if (pool == null) {
      Console.Error.WriteLine($"No pool named '{options.Pool}'.");
      return ExitNotFound;
    }

    var member = Guid.TryParse(options.Member, out var memberId)
      ? pool.Manifest.FindMember(memberId)
      : pool.Manifest.Members.FirstOrDefault(m => m.Path.Equals(options.Member, StringComparison.OrdinalIgnoreCase));

    if (member == null) {
      Console.Error.WriteLine($"Pool '{pool.Name}' has no member '{options.Member}'.");
      return ExitNotFound;
    }

    var manifest = lifecycle.RemoveMember(pool.Manifest, member.MemberId);
    Console.WriteLine($"Removed '{member.Path}' from pool '{manifest.Name}'; its data stays in place.");
    return ExitOk;
  }

  private static int _PoolAdopt(IPoolProvider provider, PoolLifecycle lifecycle, PoolAdoptOptions options) {
    var pool = _FindPool(provider, options.Pool);
    if (pool == null) {
      Console.Error.WriteLine($"No pool named '{options.Pool}'.");
      return ExitNotFound;
    }

    var manifest = lifecycle.Adopt(pool);
    Console.WriteLine($"Adopted native pool '{manifest.Name}' ({manifest.PoolId}) into an explicit manifest — no data was moved.");
    return ExitOk;
  }

  private static int _PoolRepairManifest(IPoolProvider provider, ManifestStore store, PoolRepairManifestOptions options) {
    var pool = _FindPool(provider, options.Pool);
    if (pool == null) {
      Console.Error.WriteLine($"No pool named '{options.Pool}'.");
      return ExitNotFound;
    }

    var winner = store.Reconcile(pool.PoolId, pool.Manifest.Members.Select(m => m.Path));
    if (winner == null) {
      Console.Error.WriteLine($"No manifest copy of '{pool.Name}' found anywhere.");
      return ExitNotFound;
    }

    Console.WriteLine($"Reconciled manifest of '{winner.Name}' at version {winner.Version}; stale copies refreshed.");
    return ExitOk;
  }

}
