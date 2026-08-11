using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DivisonM.Vfs;

/// <summary>A byte size ("4GiB", "1024") or a percentage of some baseline ("50%").</summary>
public readonly record struct SizeSpec {

  private static readonly (string suffix, long factor)[] _UNITS = [
    ("EIB", 1L << 60), ("PIB", 1L << 50), ("TIB", 1L << 40), ("GIB", 1L << 30), ("MIB", 1L << 20), ("KIB", 1L << 10),
    ("EB", 1000000000000000000L), ("PB", 1000000000000000L), ("TB", 1000000000000L), ("GB", 1000000000L), ("MB", 1000000L), ("KB", 1000L),
    ("B", 1L),
  ];

  public long? Bytes { get; private init; }
  public double? Percent { get; private init; }

  public static SizeSpec FromBytes(long bytes) => new() { Bytes = bytes };
  public static SizeSpec FromPercent(double percent) => new() { Percent = percent };

  public static SizeSpec Parse(string text) {
    if (string.IsNullOrWhiteSpace(text))
      throw new ManifestException("Size must not be empty");

    var trimmed = text.Trim();
    if (trimmed.EndsWith('%')) {
      if (!double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || percent < 0 || percent > 100)
        throw new ManifestException($"Invalid percentage '{text}' (expected 0..100%)");

      return FromPercent(percent);
    }

    return FromBytes(ParseBytes(trimmed));
  }

  public static long ParseBytes(string text) {
    if (string.IsNullOrWhiteSpace(text))
      throw new ManifestException("Size must not be empty");

    var trimmed = text.Trim();
    var upper = trimmed.ToUpperInvariant();
    foreach (var (suffix, factor) in _UNITS) {
      if (!upper.EndsWith(suffix, StringComparison.Ordinal))
        continue;

      var numberText = trimmed[..^suffix.Length].Trim();
      if (numberText.Length == 0)
        break;

      if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        throw new ManifestException($"Invalid size '{text}'");

      // a too-large size ("20EiB") must surface as a config error, not an uncaught OverflowException
      var scaled = value * factor;
      if (scaled > long.MaxValue)
        throw new ManifestException($"Size '{text}' is too large (exceeds {long.MaxValue} bytes)");

      return (long)scaled;
    }

    if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0)
      return bytes;

    throw new ManifestException($"Invalid size '{text}' (expected e.g. '4GiB', '512MiB' or a byte count)");
  }

  /// <summary>Absolute bytes, resolving a percentage against <paramref name="baseline"/>.</summary>
  public long ResolveBytes(long baseline) => this.Bytes ?? (long)(baseline * (this.Percent ?? 0) / 100.0);

  public override string ToString() => this.Percent is { } percent
    ? percent.ToString(CultureInfo.InvariantCulture) + "%"
    : (this.Bytes ?? 0).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Parses durations like "500ms", "5s", "2m", "1h", "7d".</summary>
public static class DurationSpec {
  public static TimeSpan Parse(string text) {
    if (string.IsNullOrWhiteSpace(text))
      throw new ManifestException("Duration must not be empty");

    var trimmed = text.Trim().ToLowerInvariant();
    (string suffix, Func<double, TimeSpan> map)[] units = [
      ("ms", TimeSpan.FromMilliseconds),
      ("s", TimeSpan.FromSeconds),
      ("m", TimeSpan.FromMinutes),
      ("h", TimeSpan.FromHours),
      ("d", TimeSpan.FromDays),
    ];

    foreach (var (suffix, map) in units) {
      if (!trimmed.EndsWith(suffix, StringComparison.Ordinal))
        continue;

      var numberText = trimmed[..^suffix.Length].Trim();
      if (numberText.Length == 0)
        continue;

      if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        throw new ManifestException($"Invalid duration '{text}'");

      return map(value);
    }

    throw new ManifestException($"Invalid duration '{text}' (expected e.g. '500ms', '5s', '2m', '1h', '7d')");
  }
}

[JsonConverter(typeof(JsonStringEnumConverter<WritePolicy>))]
public enum WritePolicy {
  [JsonStringEnumMemberName("write-through")] WriteThrough,
  [JsonStringEnumMemberName("write-back")] WriteBack,
  [JsonStringEnumMemberName("deferred")] Deferred,
  [JsonStringEnumMemberName("performance")] Performance,
}

[JsonConverter(typeof(JsonStringEnumConverter<EvictionPolicy>))]
public enum EvictionPolicy {
  [JsonStringEnumMemberName("lru")] Lru,
  [JsonStringEnumMemberName("arc")] Arc,
  [JsonStringEnumMemberName("fifo")] Fifo,
  [JsonStringEnumMemberName("lfu")] Lfu,
  [JsonStringEnumMemberName("clock")] Clock,
  [JsonStringEnumMemberName("clock-pro")] ClockPro,
  [JsonStringEnumMemberName("slru")] Slru,
  [JsonStringEnumMemberName("2q")] TwoQueue,
  [JsonStringEnumMemberName("mru")] Mru,
  [JsonStringEnumMemberName("random")] Random,
}

[JsonConverter(typeof(JsonStringEnumConverter<CacheSplitMode>))]
public enum CacheSplitMode {
  [JsonStringEnumMemberName("shared-auto")] SharedAuto,
  [JsonStringEnumMemberName("shared-fixed")] SharedFixed,
  [JsonStringEnumMemberName("separate")] Separate,
}

[JsonConverter(typeof(JsonStringEnumConverter<PlacementStrategy>))]
public enum PlacementStrategy {
  [JsonStringEnumMemberName("most-free-space")] MostFreeSpace,
  [JsonStringEnumMemberName("round-robin")] RoundRobin,
  [JsonStringEnumMemberName("least-used")] LeastUsed,

  /// <summary>Places new primaries on the member with the lowest measured I/O latency (live EWMA).</summary>
  [JsonStringEnumMemberName("lowest-latency")] LowestLatency,
}

[JsonConverter(typeof(JsonStringEnumConverter<ExternalEditPolicy>))]
public enum ExternalEditPolicy {
  [JsonStringEnumMemberName("accept-newest")] AcceptNewest,
  [JsonStringEnumMemberName("conflict-only")] ConflictOnly,
  [JsonStringEnumMemberName("read-only-until-reconciled")] ReadOnlyUntilReconciled,
}

/// <summary>What the mounted view does when a member drops out live (§10 SAFE-DEGRADE).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemberLossPolicy>))]
public enum MemberLossPolicy {
  /// <summary>Keep serving complete metadata from the in-memory shadow namespace; reads of vanished data fail cleanly.</summary>
  [JsonStringEnumMemberName("retain-metadata")] RetainMetadata,

  /// <summary>Immediately drop files/folders with no surviving copy from the mounted namespace.</summary>
  [JsonStringEnumMemberName("discard-inaccessible")] DiscardInaccessible,
}

public sealed record WriteConfig {
  [JsonPropertyName("policy")] public WritePolicy? Policy { get; init; }
  [JsonPropertyName("minCopiesBeforeAck")] public int? MinCopiesBeforeAck { get; init; }
  [JsonPropertyName("deferWindow")] public string? DeferWindow { get; init; }
  [JsonPropertyName("maxDeferSeconds")] public double? MaxDeferSeconds { get; init; }
  [JsonPropertyName("acceptVolatileAck")] public bool? AcceptVolatileAck { get; init; }
  [JsonPropertyName("fsyncIsDurable")] public bool? FsyncIsDurable { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record CacheSplitConfig {
  [JsonPropertyName("mode")] public CacheSplitMode? Mode { get; init; }
  [JsonPropertyName("read")] public string? Read { get; init; }
  [JsonPropertyName("write")] public string? Write { get; init; }
  [JsonPropertyName("readCacheMax")] public string? ReadCacheMax { get; init; }
  [JsonPropertyName("writeBufferMax")] public string? WriteBufferMax { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record CacheInstanceConfig {
  [JsonPropertyName("profile")] public string? Profile { get; init; }
  [JsonPropertyName("size")] public string? Size { get; init; }
  [JsonPropertyName("split")] public CacheSplitConfig? Split { get; init; }
  [JsonPropertyName("blockSize")] public string? BlockSize { get; init; }
  [JsonPropertyName("readEviction")] public EvictionPolicy? ReadEviction { get; init; }
  [JsonPropertyName("metadataEntries")] public int? MetadataEntries { get; init; }
  [JsonPropertyName("metadataTtl")] public string? MetadataTtl { get; init; }
  [JsonPropertyName("metadataEviction")] public EvictionPolicy? MetadataEviction { get; init; }
  [JsonPropertyName("dynamic")] public bool? Dynamic { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record CacheHostConfig {
  [JsonPropertyName("maxTotal")] public string? MaxTotal { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record CacheAttachmentConfig {
  [JsonPropertyName("use")] public string? Use { get; init; }
  [JsonPropertyName("weight")] public double? Weight { get; init; }
  [JsonPropertyName("dedicated")] public CacheInstanceConfig? Dedicated { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ReadAheadConfig {
  [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
  [JsonPropertyName("minWindow")] public string? MinWindow { get; init; }
  [JsonPropertyName("maxWindow")] public string? MaxWindow { get; init; }
  [JsonPropertyName("adaptive")] public bool? Adaptive { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record IoConfig {
  [JsonPropertyName("queueDepthPerVolume")] public Dictionary<string, int>? QueueDepthPerVolume { get; init; }
  [JsonPropertyName("mirrorReadSplitThreshold")] public string? MirrorReadSplitThreshold { get; init; }
  [JsonPropertyName("elevatorOrdering")] public bool? ElevatorOrdering { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record PlacementConfig {
  [JsonPropertyName("strategy")] public PlacementStrategy? Strategy { get; init; }
  [JsonPropertyName("shadowNeverSamePhysical")] public bool? ShadowNeverSamePhysical { get; init; }

  /// <summary>FR-AUTO-TIER: measure member latency and re-tier the landing zone live when a drive gets slow/busy.</summary>
  [JsonPropertyName("autoLandingZone")] public bool? AutoLandingZone { get; init; }

  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record TierConfig {
  [JsonPropertyName("members")] public IReadOnlyList<string>? Members { get; init; }
  [JsonPropertyName("highWatermark")] public string? HighWatermark { get; init; }
  [JsonPropertyName("lowWatermark")] public string? LowWatermark { get; init; }
  [JsonPropertyName("drainConcurrency")] public int? DrainConcurrency { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record BackgroundConfig {
  [JsonPropertyName("maxThroughput")] public string? MaxThroughput { get; init; }
  [JsonPropertyName("balancerEnabled")] public bool? BalancerEnabled { get; init; }
  [JsonPropertyName("duplicatorEnabled")] public bool? DuplicatorEnabled { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record SafetyConfig {
  [JsonPropertyName("journalEnabled")] public bool? JournalEnabled { get; init; }
  [JsonPropertyName("refuseMountOnUnrecoverable")] public bool? RefuseMountOnUnrecoverable { get; init; }
  [JsonPropertyName("verifyDrainWithChecksum")] public bool? VerifyDrainWithChecksum { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ResilienceConfig {
  /// <summary>Behaviour when a mounted pool loses a member live (§10 SAFE-DEGRADE); default retain-metadata.</summary>
  [JsonPropertyName("onMemberLoss")] public MemberLossPolicy? OnMemberLoss { get; init; }

  /// <summary>
  /// Accept writes when fewer copies than <c>write.minCopiesBeforeAck</c> are reachable
  /// (default true): one lost drive degrades redundancy, not availability — the owed copies
  /// heal automatically when the member returns. Set false to refuse acks instead.
  /// </summary>
  [JsonPropertyName("acceptDegradedWrites")] public bool? AcceptDegradedWrites { get; init; }

  /// <summary>How often the member watcher polls member reachability while mounted.</summary>
  [JsonPropertyName("memberPollSeconds")] public double? MemberPollSeconds { get; init; }

  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record IntegrityConfig {
  [JsonPropertyName("checksumDb")] public bool? ChecksumDb { get; init; }
  [JsonPropertyName("fastHash")] public string? FastHash { get; init; }
  [JsonPropertyName("strongHash")] public string? StrongHash { get; init; }
  [JsonPropertyName("onExternalEdit")] public ExternalEditPolicy? OnExternalEdit { get; init; }
  [JsonPropertyName("scrubberSchedule")] public string? ScrubberSchedule { get; init; }
  [JsonPropertyName("deepScrubSchedule")] public string? DeepScrubSchedule { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record TrashConfig {
  [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
  [JsonPropertyName("retention")] public string? Retention { get; init; }
  [JsonPropertyName("maxSize")] public string? MaxSize { get; init; }
  [JsonPropertyName("dropDuplicatesInTrash")] public bool? DropDuplicatesInTrash { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ObservabilityConfig {
  [JsonPropertyName("logLevel")] public string? LogLevel { get; init; }
  [JsonPropertyName("metrics")] public JsonElement? Metrics { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>
/// One resolution layer of the §8 configuration; all keys optional — omitted keys
/// inherit from the next-lower layer (folder override → pool → global → built-in).
/// </summary>
public sealed record PoolConfig {
  [JsonPropertyName("pool")] public string? Pool { get; init; }
  [JsonPropertyName("mount")] public MountSpec? Mount { get; init; }
  [JsonPropertyName("write")] public WriteConfig? Write { get; init; }
  [JsonPropertyName("cacheHost")] public CacheHostConfig? CacheHost { get; init; }
  [JsonPropertyName("caches")] public Dictionary<string, CacheInstanceConfig>? Caches { get; init; }
  [JsonPropertyName("cache")] public CacheAttachmentConfig? Cache { get; init; }
  [JsonPropertyName("readAhead")] public ReadAheadConfig? ReadAhead { get; init; }
  [JsonPropertyName("io")] public IoConfig? Io { get; init; }
  [JsonPropertyName("placement")] public PlacementConfig? Placement { get; init; }
  [JsonPropertyName("tiers")] public Dictionary<string, TierConfig>? Tiers { get; init; }
  [JsonPropertyName("memberOverrides")] public Dictionary<string, JsonElement>? MemberOverrides { get; init; }
  [JsonPropertyName("background")] public BackgroundConfig? Background { get; init; }
  [JsonPropertyName("safety")] public SafetyConfig? Safety { get; init; }
  [JsonPropertyName("resilience")] public ResilienceConfig? Resilience { get; init; }
  [JsonPropertyName("integrity")] public IntegrityConfig? Integrity { get; init; }
  [JsonPropertyName("trash")] public TrashConfig? Trash { get; init; }
  [JsonPropertyName("locale")] public string? Locale { get; init; }

  /// <summary>Per-folder overrides keyed by glob (e.g. "Documents/**"); duplication level D lives here too.</summary>
  [JsonPropertyName("folders")] public Dictionary<string, JsonElement>? Folders { get; init; }

  [JsonPropertyName("duplication")] public int? Duplication { get; init; }
  [JsonPropertyName("observability")] public ObservabilityConfig? Observability { get; init; }
  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>
/// Hierarchical configuration resolver (CMP-CFG): effective value = folder override →
/// pool → global → built-in performance-tuned defaults (CFG-DEFAULT). Merging happens
/// key-wise on the JSON level so any omitted key inherits.
/// </summary>
public static class ConfigResolver {

  /// <summary>The §8 out-of-box defaults: a zero-config mount must be safe and fast (CFG-DEFAULT).</summary>
  public const string BuiltInDefaultsJson = """
  {
    "write": {
      "policy": "write-back",
      "minCopiesBeforeAck": 2,
      "deferWindow": "5s",
      "maxDeferSeconds": 30,
      "acceptVolatileAck": false,
      "fsyncIsDurable": true
    },
    "cacheHost": { "maxTotal": "50%" },
    "caches": {
      "global": {
        "profile": "balanced",
        "size": "4GiB",
        "split": { "mode": "shared-auto" },
        "blockSize": "1MiB",
        "readEviction": "arc",
        "metadataEntries": 100000,
        "metadataTtl": "30s",
        "metadataEviction": "lru",
        "dynamic": true
      }
    },
    "cache": { "use": "global", "weight": 1.0, "dedicated": null },
    "readAhead": { "enabled": true, "minWindow": "1MiB", "maxWindow": "8MiB", "adaptive": true },
    "io": {
      "queueDepthPerVolume": { "hdd": 2, "ssd": 8 },
      "mirrorReadSplitThreshold": "8MiB",
      "elevatorOrdering": true
    },
    "placement": { "strategy": "most-free-space", "shadowNeverSamePhysical": true },
    "tiers": {
      "fast": { "members": [], "highWatermark": "90%", "lowWatermark": "75%", "drainConcurrency": 2 },
      "capacity": { "members": ["*"], "highWatermark": "95%", "lowWatermark": "85%" }
    },
    "background": { "maxThroughput": "50%", "balancerEnabled": true, "duplicatorEnabled": true },
    "safety": { "journalEnabled": true, "refuseMountOnUnrecoverable": true, "verifyDrainWithChecksum": true },
    "resilience": { "onMemberLoss": "retain-metadata", "memberPollSeconds": 5 },
    "integrity": {
      "checksumDb": true,
      "fastHash": "xxh3",
      "strongHash": null,
      "onExternalEdit": "accept-newest",
      "scrubberSchedule": "idle-weekly",
      "deepScrubSchedule": null
    },
    "trash": { "enabled": false, "retention": "7d", "maxSize": "5%", "dropDuplicatesInTrash": true },
    "locale": "auto",
    "duplication": 1,
    "observability": { "logLevel": "info", "metrics": { "enabled": true, "endpoint": "127.0.0.1:9723" } }
  }
  """;

  public static PoolConfig ParseConfig(string json) {
    try {
      return JsonSerializer.Deserialize<PoolConfig>(json, ManifestSerializer.Options) ?? new PoolConfig();
    } catch (JsonException e) {
      throw new ManifestException($"Configuration is not valid JSON: {e.Message}", e);
    }
  }

  /// <summary>Deep-merges JSON objects; keys in <paramref name="overlay"/> win, objects merge recursively, null overlay values reset to overlay.</summary>
  internal static JsonNode? Merge(JsonNode? baseline, JsonNode? overlay) {
    if (overlay is null)
      return baseline?.DeepClone();
    if (baseline is not JsonObject baseObject || overlay is not JsonObject overlayObject)
      return overlay.DeepClone();

    var result = (JsonObject)baseObject.DeepClone();
    foreach (var (key, value) in overlayObject)
      result[key] = result.TryGetPropertyValue(key, out var existing)
        ? Merge(existing, value)
        : value?.DeepClone();

    return result;
  }

  /// <summary>Resolves the effective pool-level config: built-in defaults ← global file ← pool config.</summary>
  public static PoolConfig ResolveEffective(string? globalJson, string? poolJson) {
    var merged = JsonNode.Parse(BuiltInDefaultsJson, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip });
    if (!string.IsNullOrWhiteSpace(globalJson))
      merged = Merge(merged, ParseNode(globalJson));
    if (!string.IsNullOrWhiteSpace(poolJson))
      merged = Merge(merged, ParseNode(poolJson));

    return DeserializeMerged(merged);
  }

  // ResolveForFolder sits on the WRITE hot path: PoolFileSystem.Write calls it once directly and
  // once more inside the ack-quorum check, Unlink calls it, and every placement/heal decision
  // calls it through DuplicationLevelFor. Building the answer serialises the ENTIRE pool config
  // to JSON, deep-merges each matching override and deserialises the result — hundreds of
  // allocations for a value that only changes when the configuration does.
  //
  // Which overrides match is cheap (glob matching, no allocation); the merge is not. So only the
  // merge is memoised, keyed by the SET OF MATCHED GLOBS rather than by the folder path: the
  // number of cached entries is then bounded by how many distinct override combinations the
  // configuration can produce, not by how many folders the pool contains — a pool with millions
  // of directories still holds a handful of entries. Keying on the PoolConfig instance means a
  // live reload (which builds a new instance) drops the old table with it; nothing to invalidate.
  private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PoolConfig, string[]> _orderedGlobs = new();
  private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PoolConfig, System.Collections.Concurrent.ConcurrentDictionary<ulong, PoolConfig>> _mergedByMatchedGlobs = new();

  /// <summary>Beyond this many override globs the match set no longer fits one bitmask key; such a config resolves uncached.</summary>
  private const int _MAX_MEMOISED_GLOBS = 64;

  /// <summary>Resolves the effective config for one pool-relative folder path, applying all matching "folders" glob overrides (most-specific last).</summary>
  public static PoolConfig ResolveForFolder(PoolConfig effectivePoolConfig, string folderPath) {
    if (effectivePoolConfig.Folders is not { Count: > 0 } folders)
      return effectivePoolConfig;

    var normalized = PoolPaths.Normalize(folderPath);
    var ordered = _orderedGlobs.GetValue(effectivePoolConfig, static config => [.. config.Folders!.Keys.OrderBy(key => key.Length)]);
    if (ordered.Length > _MAX_MEMOISED_GLOBS)
      return _MergeMatchingOverrides(effectivePoolConfig, folders, ordered, normalized);

    // the match set is a BITMASK over the fixed, ordered glob list -- an exact key that no folder
    // name can forge, unlike a delimited string of globs that may themselves contain any character
    ulong matched = 0;
    for (var index = 0; index < ordered.Length; ++index)
      if (GlobMatches(ordered[index], normalized))
        matched |= 1UL << index;

    if (matched == 0)
      return effectivePoolConfig; // no override applies -- the common case, and allocation-free

    var memo = _mergedByMatchedGlobs.GetValue(effectivePoolConfig, static _ => new());
    return memo.GetOrAdd(matched, static (mask, state) => _MergeMatchedOverrides(state.config, state.folders, state.ordered, mask),
      (config: effectivePoolConfig, folders, ordered));
  }

  private static PoolConfig _MergeMatchedOverrides(PoolConfig effectivePoolConfig, Dictionary<string, JsonElement> folders, string[] ordered, ulong matched) {
    var baseNode = JsonSerializer.SerializeToNode(effectivePoolConfig, ManifestSerializer.Options);
    for (var index = 0; index < ordered.Length; ++index)
      if ((matched & (1UL << index)) != 0)
        baseNode = Merge(baseNode, JsonSerializer.SerializeToNode(folders[ordered[index]], ManifestSerializer.Options));

    return DeserializeMerged(baseNode);
  }

  /// <summary>The uncached path, for a configuration with more override globs than fit one bitmask.</summary>
  private static PoolConfig _MergeMatchingOverrides(PoolConfig effectivePoolConfig, Dictionary<string, JsonElement> folders, string[] ordered, string normalized) {
    var baseNode = JsonSerializer.SerializeToNode(effectivePoolConfig, ManifestSerializer.Options);
    var applied = false;
    foreach (var glob in ordered) {
      if (!GlobMatches(glob, normalized))
        continue;

      baseNode = Merge(baseNode, JsonSerializer.SerializeToNode(folders[glob], ManifestSerializer.Options));
      applied = true;
    }

    return applied ? DeserializeMerged(baseNode) : effectivePoolConfig;
  }

  private static JsonNode? ParseNode(string json) {
    try {
      return JsonNode.Parse(json, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    } catch (JsonException e) {
      throw new ManifestException($"Configuration is not valid JSON: {e.Message}", e);
    }
  }

  private static PoolConfig DeserializeMerged(JsonNode? merged) {
    try {
      return merged.Deserialize<PoolConfig>(ManifestSerializer.Options) ?? new PoolConfig();
    } catch (JsonException e) {
      throw new ManifestException($"Configuration is invalid: {e.Message}", e);
    }
  }

  // Translating a glob to a regex costs a StringBuilder, a Regex.Escape allocation PER CHARACTER
  // and a fresh pattern string -- and GlobMatches is called for every override glob on every
  // folder resolution, which is on the write path. The globs come from configuration, so there
  // are only ever a handful of distinct ones: the compiled matcher is cached per glob and the
  // translation happens once. Bounded, because the method is public and callers are not.
  private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex> _globMatchers = new(StringComparer.Ordinal);

  private const int _MAX_CACHED_GLOB_MATCHERS = 1024;

  /// <summary>Glob matching for folder overrides: ** spans segments, * stays within one, ? is a single character.</summary>
  public static bool GlobMatches(string glob, string normalizedPath) {
    if (_globMatchers.TryGetValue(glob, out var matcher))
      return matcher.IsMatch(normalizedPath);

    matcher = new(_GlobToPattern(glob), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    if (_globMatchers.Count < _MAX_CACHED_GLOB_MATCHERS)
      _globMatchers.TryAdd(glob, matcher);

    return matcher.IsMatch(normalizedPath);
  }

  private static string _GlobToPattern(string glob) {
    var pattern = new System.Text.StringBuilder("^");
    var globNormalized = glob.Replace('\\', '/').Trim('/');
    for (var i = 0; i < globNormalized.Length; ++i) {
      var c = globNormalized[i];
      switch (c) {
        case '*' when i + 1 < globNormalized.Length && globNormalized[i + 1] == '*':
          pattern.Append(".*");
          ++i;
          if (i + 1 < globNormalized.Length && globNormalized[i + 1] == '/')
            ++i;
          break;
        case '*':
          pattern.Append("[^/]*");
          break;
        case '?':
          pattern.Append("[^/]");
          break;
        default:
          pattern.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
          break;
      }
    }

    return pattern.Append('$').ToString();
  }

}
