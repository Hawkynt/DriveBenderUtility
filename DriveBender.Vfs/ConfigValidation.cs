namespace DivisonM.Vfs;

/// <summary>A configuration value violates CFG-VALIDATE; the mount must refuse to start.</summary>
public sealed class ConfigValidationException(string message) : ManifestException(message);

/// <summary>
/// Validates effective configurations (CFG-VALIDATE): invalid values are rejected with a
/// precise message rather than silently falling back to unsafe behaviour. Includes the
/// safety floors (CFG-SAFE-FLOOR), the volatile-ack coupling, and the machine-wide RAM
/// ceiling (SAFE-RAM-BUDGET).
/// </summary>
public static class ConfigValidator {

  /// <summary>Validates one effective (fully merged) config layer, e.g. the pool level or one folder's resolved config.</summary>
  public static void Validate(PoolConfig config, long totalPhysicalRamBytes = 0) {
    ValidateWrite(config.Write, config.Duplication);
    _ValidateSafety(config.Safety);
    _ValidateReadAhead(config.ReadAhead);
    _ValidatePlacementAndIo(config);
    _ValidateTiers(config.Tiers);
    _ValidateCaches(config, totalPhysicalRamBytes);
    _ValidateIntegrity(config.Integrity);
    _ValidateTrash(config.Trash);
    _ValidateFolderOverrides(config);
  }

  /// <summary>CFG-SAFE-FLOOR: 1 ≤ minCopiesBeforeAck ≤ D; for duplicated folders (D≥2) the floor is min(2, D). acceptVolatileAck requires the performance policy; fsync durability cannot be disabled.</summary>
  public static void ValidateWrite(WriteConfig? write, int? duplication) {
    if (write == null)
      return;

    var duplicationLevel = duplication ?? 1;
    if (duplicationLevel < 1)
      throw new ConfigValidationException($"'duplication' is the total number of copies and must be at least 1, got {duplicationLevel}");

    if (write.MinCopiesBeforeAck is { } minCopies) {
      if (minCopies < 1)
        throw new ConfigValidationException($"'write.minCopiesBeforeAck' must be at least 1, got {minCopies}");

      // for non-duplicated folders (D=1) the §8 default of 2 clamps to 1 at runtime
      // (see EffectiveMinCopiesBeforeAck); explicit inconsistency is only possible for D≥2
      if (duplicationLevel >= 2 && minCopies > duplicationLevel)
        throw new ConfigValidationException($"'write.minCopiesBeforeAck' ({minCopies}) must not exceed the duplication level D ({duplicationLevel})");
      if (duplicationLevel >= 2 && minCopies < 2 && write.AcceptVolatileAck != true)
        throw new ConfigValidationException($"'write.minCopiesBeforeAck' ({minCopies}) is below the safety floor min(2, D)={Math.Min(2, duplicationLevel)} for duplicated folders");
    }

    if (write.AcceptVolatileAck == true && write.Policy != WritePolicy.Performance)
      throw new ConfigValidationException("'write.acceptVolatileAck' is only allowed with 'write.policy': 'performance' (SAFE-RAM)");

    if (write.FsyncIsDurable == false)
      throw new ConfigValidationException("'write.fsyncIsDurable' cannot be disabled — fsync is an absolute durability barrier (SAFE-FSYNC)");

    if (write.DeferWindow is { } deferWindow && write.MaxDeferSeconds is { } maxDefer) {
      var window = DurationSpec.Parse(deferWindow);
      if (maxDefer < 0)
        throw new ConfigValidationException($"'write.maxDeferSeconds' must not be negative, got {maxDefer}");
      if (window > TimeSpan.FromSeconds(maxDefer))
        throw new ConfigValidationException($"'write.deferWindow' ({deferWindow}) must not exceed 'write.maxDeferSeconds' ({maxDefer}s)");
    }
  }

  /// <summary>The copies that must exist before an ack: the configured value clamped into [1, D] (CFG-SAFE-FLOOR).</summary>
  public static int EffectiveMinCopiesBeforeAck(WriteConfig? write, int? duplication) {
    var duplicationLevel = Math.Max(1, duplication ?? 1);
    var configured = write?.MinCopiesBeforeAck ?? (duplicationLevel >= 2 ? 2 : 1);
    return Math.Clamp(configured, 1, duplicationLevel);
  }

  private static void _ValidateSafety(SafetyConfig? safety) {
    if (safety?.JournalEnabled == false)
      throw new ConfigValidationException("'safety.journalEnabled' cannot be disabled (SAFE-WAL is Must-tier)");
  }

  private static void _ValidateReadAhead(ReadAheadConfig? readAhead) {
    if (readAhead == null)
      return;

    var min = readAhead.MinWindow is { } minText ? SizeSpec.ParseBytes(minText) : (long?)null;
    var max = readAhead.MaxWindow is { } maxText ? SizeSpec.ParseBytes(maxText) : (long?)null;
    if (min is { } minValue && max is { } maxValue && minValue > maxValue)
      throw new ConfigValidationException($"'readAhead.minWindow' ({min}) must not exceed 'readAhead.maxWindow' ({max})");
  }

  private static void _ValidatePlacementAndIo(PoolConfig config) {
    if (config.Io?.QueueDepthPerVolume is { } depths)
      foreach (var (kind, depth) in depths)
        if (depth < 1)
          throw new ConfigValidationException($"'io.queueDepthPerVolume.{kind}' must be at least 1, got {depth}");

    if (config.Io?.MirrorReadSplitThreshold is { } threshold)
      SizeSpec.ParseBytes(threshold);
  }

  private static void _ValidateTiers(Dictionary<string, TierConfig>? tiers) {
    if (tiers == null)
      return;

    foreach (var (name, tier) in tiers) {
      var high = _ParsePercent($"tiers.{name}.highWatermark", tier.HighWatermark);
      var low = _ParsePercent($"tiers.{name}.lowWatermark", tier.LowWatermark);
      if (high is { } highValue && low is { } lowValue && lowValue >= highValue)
        throw new ConfigValidationException($"'tiers.{name}.lowWatermark' ({tier.LowWatermark}) must be below 'highWatermark' ({tier.HighWatermark})");

      if (tier.DrainConcurrency is { } concurrency && concurrency < 1)
        throw new ConfigValidationException($"'tiers.{name}.drainConcurrency' must be at least 1, got {concurrency}");
    }
  }

  private static double? _ParsePercent(string key, string? text) {
    if (text == null)
      return null;

    var spec = SizeSpec.Parse(text);
    if (spec.Percent is not { } percent)
      throw new ConfigValidationException($"'{key}' must be a percentage (e.g. \"90%\"), got '{text}'");
    if (percent is <= 0 or > 100)
      throw new ConfigValidationException($"'{key}' must be within (0, 100], got '{text}'");

    return percent;
  }

  /// <summary>SAFE-RAM-BUDGET: the sum of all cache instances is validated against the host ceiling and may never over-commit.</summary>
  private static void _ValidateCaches(PoolConfig config, long totalPhysicalRamBytes) {
    if (config.Cache is { } attachment) {
      if (attachment.Weight is { } weight && weight <= 0)
        throw new ConfigValidationException($"'cache.weight' must be positive, got {weight}");
      if (attachment.Use != null && attachment.Dedicated != null)
        throw new ConfigValidationException("'cache.use' and 'cache.dedicated' are mutually exclusive — attach to a named instance or define a private one");
      if (attachment.Use != null && config.Caches != null && !config.Caches.ContainsKey(attachment.Use))
        throw new ConfigValidationException($"'cache.use' references unknown cache instance '{attachment.Use}'");
    }

    var instances = new List<(string name, CacheInstanceConfig instance)>();
    if (config.Caches != null)
      instances.AddRange(config.Caches.Select(pair => (pair.Key, pair.Value)));
    if (config.Cache?.Dedicated is { } dedicated)
      instances.Add(("cache.dedicated", dedicated));

    long fixedTotal = 0;
    foreach (var (name, instance) in instances) {
      if (instance.BlockSize is { } blockSize && SizeSpec.ParseBytes(blockSize) < 1)
        throw new ConfigValidationException($"'caches.{name}.blockSize' must be positive");
      if (instance.MetadataEntries is { } entries && entries < 0)
        throw new ConfigValidationException($"'caches.{name}.metadataEntries' must not be negative, got {entries}");
      if (instance.MetadataTtl is { } ttl)
        DurationSpec.Parse(ttl);

      _ValidateSplit(name, instance.Split);

      if (instance.Size is { } size)
        fixedTotal += SizeSpec.ParseBytes(size);
      else if (instance.Split is { Mode: CacheSplitMode.Separate } split) {
        if (split.ReadCacheMax is { } readMax)
          fixedTotal += SizeSpec.ParseBytes(readMax);
        if (split.WriteBufferMax is { } writeMax)
          fixedTotal += SizeSpec.ParseBytes(writeMax);
      }
    }

    if (config.CacheHost?.MaxTotal is { } maxTotalText && totalPhysicalRamBytes > 0) {
      var ceiling = SizeSpec.Parse(maxTotalText).ResolveBytes(totalPhysicalRamBytes);
      if (ceiling < 1)
        throw new ConfigValidationException($"'cacheHost.maxTotal' resolves to {ceiling} bytes — no cache could exist");
      if (fixedTotal > ceiling)
        throw new ConfigValidationException($"Cache instances total {fixedTotal} bytes and over-commit the 'cacheHost.maxTotal' ceiling of {ceiling} bytes (SAFE-RAM-BUDGET)");
    }
  }

  private static void _ValidateSplit(string name, CacheSplitConfig? split) {
    if (split == null)
      return;

    switch (split.Mode) {
      case CacheSplitMode.SharedFixed: {
        if (split.Read == null || split.Write == null)
          throw new ConfigValidationException($"'caches.{name}.split' with mode 'shared-fixed' requires both 'read' and 'write' percentages");

        var read = SizeSpec.Parse(split.Read).Percent ?? throw new ConfigValidationException($"'caches.{name}.split.read' must be a percentage");
        var write = SizeSpec.Parse(split.Write).Percent ?? throw new ConfigValidationException($"'caches.{name}.split.write' must be a percentage");
        if (Math.Abs(read + write - 100) > 0.001)
          throw new ConfigValidationException($"'caches.{name}.split' read ({split.Read}) and write ({split.Write}) must sum to 100%");
        break;
      }
      case CacheSplitMode.Separate:
        if (split.ReadCacheMax == null || split.WriteBufferMax == null)
          throw new ConfigValidationException($"'caches.{name}.split' with mode 'separate' requires 'readCacheMax' and 'writeBufferMax'");

        SizeSpec.ParseBytes(split.ReadCacheMax);
        SizeSpec.ParseBytes(split.WriteBufferMax);
        break;
    }
  }

  private static void _ValidateIntegrity(IntegrityConfig? integrity) {
    if (integrity == null)
      return;

    if (integrity.FastHash is { } fastHash && fastHash is not ("xxh3" or "blake3"))
      throw new ConfigValidationException($"'integrity.fastHash' must be 'xxh3' or 'blake3', got '{fastHash}'");
    if (integrity.StrongHash is { } strongHash && strongHash is not ("blake3" or "sha256"))
      throw new ConfigValidationException($"'integrity.strongHash' must be 'blake3' or 'sha256', got '{strongHash}'");
  }

  private static void _ValidateTrash(TrashConfig? trash) {
    if (trash == null)
      return;

    if (trash.Retention is { } retention)
      DurationSpec.Parse(retention);
    if (trash.MaxSize is { } maxSize)
      SizeSpec.Parse(maxSize);
  }

  private static void _ValidateFolderOverrides(PoolConfig config) {
    if (config.Folders is not { Count: > 0 } folders)
      return;

    foreach (var glob in folders.Keys) {
      var resolved = ConfigResolver.ResolveForFolder(config, glob.Replace("**", "probe").Replace("*", "probe").Replace("?", "x"));
      ValidateWrite(resolved.Write, resolved.Duplication);
    }
  }

  /// <summary>
  /// Tier membership may be expressed by a member's role or by tiers[*].members; all
  /// sources for one member must agree, else the config is rejected (CFG-VALIDATE §8).
  /// </summary>
  public static void ValidateTierAssignments(PoolManifest manifest, PoolConfig config) {
    if (config.Tiers == null)
      return;

    foreach (var member in manifest.Members) {
      var roleTier = member.Role switch {
        MemberRole.Landing => "fast",
        MemberRole.Capacity => "capacity",
        _ => null,
      };

      foreach (var (tierName, tier) in config.Tiers) {
        if (tier.Members == null || tier.Members.Contains("*"))
          continue;

        var listed = tier.Members.Any(m =>
          m.Equals(member.Label, StringComparison.OrdinalIgnoreCase)
          || m.Equals(member.Path, StringComparison.OrdinalIgnoreCase)
          || (Guid.TryParse(m, out var id) && id == member.MemberId));

        if (listed && roleTier != null && tierName != roleTier)
          throw new ConfigValidationException($"Member '{member.Label ?? member.Path}' has role '{member.Role}' (tier '{roleTier}') but is listed in tier '{tierName}' — all tier sources must agree");
      }
    }
  }

}
