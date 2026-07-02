using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisonM.Vfs;

/// <summary>A manifest, marker or config file is structurally invalid (CFG-VALIDATE / CFG-SCHEMA).</summary>
public class ManifestException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Tier shorthand of a member (§6.0.1): landing ⇒ fast tier, capacity ⇒ capacity tier.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemberRole>))]
public enum MemberRole {
  [JsonStringEnumMemberName("capacity")] Capacity,
  [JsonStringEnumMemberName("landing")] Landing,
  [JsonStringEnumMemberName("readonly")] ReadOnly,
}

/// <summary>
/// One member of a pool manifest (§6.0.1). <see cref="Path"/> is a hint, not the
/// identity — the stable identity is <see cref="MemberId"/>, resolved at mount time by
/// marker content (FR-RESOLVE-MEMBER).
/// </summary>
public sealed record PoolMemberDefinition {
  [JsonPropertyName("memberId")] public required Guid MemberId { get; init; }
  [JsonPropertyName("path")] public required string Path { get; init; }
  [JsonPropertyName("role")] public MemberRole Role { get; init; } = MemberRole.Capacity;
  [JsonPropertyName("label")] public string? Label { get; init; }

  /// <summary>Bytes the pool must never consume on this member's volume (subfolder members sharing a disk with foreign data, §6.0.4).</summary>
  [JsonPropertyName("reserveBytes")]
  [JsonConverter(typeof(ByteSizeJsonConverter))]
  public long ReserveBytes { get; init; }

  /// <summary>Indirect OS-credential-store reference for network members — never a plaintext secret (SEC-CRED).</summary>
  [JsonPropertyName("credential")] public string? Credential { get; init; }

  [JsonPropertyName("network")] public bool Network { get; init; }
  [JsonPropertyName("scheme")] public string? Scheme { get; init; }

  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record MountSpec {
  [JsonPropertyName("target")] public string? Target { get; init; }
  [JsonPropertyName("volumeLabel")] public string? VolumeLabel { get; init; }
  [JsonPropertyName("readOnly")] public bool ReadOnly { get; init; }

  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>
/// The one true pool model (§1.3): a set of member folders plus tuning. Explicit JSON
/// manifests and the native drive scan both yield this shape; nothing downstream
/// branches on the definition source.
/// </summary>
public sealed record PoolManifest {
  public const string CurrentSchema = "drivebender-pool/1";
  public const int CurrentSchemaMajor = 1;

  [JsonPropertyName("schema")] public string Schema { get; init; } = CurrentSchema;
  [JsonPropertyName("poolId")] public required Guid PoolId { get; init; }
  [JsonPropertyName("name")] public required string Name { get; init; }

  /// <summary>Monotonically increasing on every write; the highest version wins on redundancy conflicts (SAFE-MANIFEST).</summary>
  [JsonPropertyName("version")] public int Version { get; init; }

  [JsonPropertyName("members")] public IReadOnlyList<PoolMemberDefinition> Members { get; init; } = [];
  [JsonPropertyName("mount")] public MountSpec? Mount { get; init; }
  [JsonPropertyName("automount")] public bool AutoMount { get; init; }

  /// <summary>The §8 config block; parsed by the configuration layer, kept raw here so unknown keys survive rewrites (CFG-SCHEMA).</summary>
  [JsonPropertyName("defaults")] public JsonElement? Defaults { get; init; }

  [JsonPropertyName("folders")] public JsonElement? Folders { get; init; }
  [JsonPropertyName("memberOverrides")] public JsonElement? MemberOverrides { get; init; }

  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }

  /// <summary>True when synthesized by a discovery adapter (native drive scan) instead of read from JSON; never persisted.</summary>
  [JsonIgnore] public bool IsVirtual { get; init; }

  public PoolMemberDefinition? FindMember(Guid memberId) => this.Members.FirstOrDefault(m => m.MemberId == memberId);
}

/// <summary>Self-identification marker stored per member under .drivebenderutility/member.json (FR-RESOLVE-MEMBER).</summary>
public sealed record MemberMarker {
  [JsonPropertyName("poolId")] public required Guid PoolId { get; init; }
  [JsonPropertyName("memberId")] public required Guid MemberId { get; init; }
  [JsonPropertyName("name")] public string? Name { get; init; }

  [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>Accepts byte sizes as plain numbers or unit strings ("20GiB", "512MiB", "1024").</summary>
public sealed class ByteSizeJsonConverter : JsonConverter<long> {
  public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch {
    JsonTokenType.Number => reader.GetInt64(),
    JsonTokenType.String => SizeSpec.ParseBytes(reader.GetString()!),
    _ => throw new ManifestException($"Expected a byte size (number or unit string), got {reader.TokenType}"),
  };

  public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>
/// Parses and writes pool manifests and member markers with schema-version checking:
/// an older app must refuse a newer major version rather than misinterpret it, and
/// unknown keys are preserved on rewrite (CFG-SCHEMA).
/// </summary>
public static class ManifestSerializer {

  internal static readonly JsonSerializerOptions Options = new() {
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
  };

  public static PoolManifest Parse(string json) {
    PoolManifest? manifest;
    try {
      manifest = JsonSerializer.Deserialize<PoolManifest>(json, Options);
    } catch (JsonException e) {
      throw new ManifestException($"Manifest is not valid JSON: {e.Message}", e);
    }

    if (manifest == null)
      throw new ManifestException("Manifest is empty");

    ValidateSchema(manifest.Schema);

    if (manifest.PoolId == Guid.Empty)
      throw new ManifestException("Manifest requires a non-empty 'poolId'");
    if (string.IsNullOrWhiteSpace(manifest.Name))
      throw new ManifestException("Manifest requires a non-empty 'name'");

    var seenIds = new HashSet<Guid>();
    foreach (var member in manifest.Members) {
      if (member.MemberId == Guid.Empty)
        throw new ManifestException($"Member '{member.Path}' requires a non-empty 'memberId'");
      if (string.IsNullOrWhiteSpace(member.Path))
        throw new ManifestException($"Member '{member.MemberId}' requires a non-empty 'path'");
      if (!seenIds.Add(member.MemberId))
        throw new ManifestException($"Duplicate memberId '{member.MemberId}' in manifest");
      if (member.ReserveBytes < 0)
        throw new ManifestException($"Member '{member.Path}': 'reserveBytes' must not be negative");
    }

    return manifest;
  }

  public static void ValidateSchema(string? schema) {
    if (string.IsNullOrWhiteSpace(schema))
      throw new ManifestException("Manifest requires a 'schema' identifier");

    var parts = schema.Split('/');
    if (parts.Length != 2 || parts[0] != "drivebender-pool" || !int.TryParse(parts[1], out var major))
      throw new ManifestException($"Unknown manifest schema '{schema}'");

    if (major > PoolManifest.CurrentSchemaMajor)
      throw new ManifestException($"Manifest schema '{schema}' is newer than this application supports ({PoolManifest.CurrentSchema}); refusing to misinterpret it");
  }

  public static string Write(PoolManifest manifest) => JsonSerializer.Serialize(manifest, Options);

  public static MemberMarker ParseMarker(string json) {
    MemberMarker? marker;
    try {
      marker = JsonSerializer.Deserialize<MemberMarker>(json, Options);
    } catch (JsonException e) {
      throw new ManifestException($"Member marker is not valid JSON: {e.Message}", e);
    }

    if (marker == null || marker.PoolId == Guid.Empty || marker.MemberId == Guid.Empty)
      throw new ManifestException("Member marker requires 'poolId' and 'memberId'");

    return marker;
  }

  public static string WriteMarker(MemberMarker marker) => JsonSerializer.Serialize(marker, Options);

}
