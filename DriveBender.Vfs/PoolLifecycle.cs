using System.Text.Json;
using System.Text.Json.Nodes;

namespace DivisonM.Vfs;

/// <summary>An operation would touch pre-existing data without explicit consent (SAFE-NONDESTRUCTIVE).</summary>
public class NonDestructiveViolationException(string message) : ManifestException(message);

/// <summary>
/// A folder is already claimed by a different pool's member marker. Carries enough context for
/// the caller to offer a real choice — recover the claiming pool (when its mirror is still there)
/// or take the folder over for a new/other pool — instead of a dead-end error.
/// </summary>
public sealed class MemberClaimConflictException(string message, string path, Guid conflictPoolId, bool restorable, bool registered)
  : NonDestructiveViolationException(message) {
  public string Path { get; } = path;
  public Guid ConflictPoolId { get; } = conflictPoolId;

  /// <summary>The claiming pool can be rebuilt from a manifest mirror still present at this folder.</summary>
  public bool Restorable { get; } = restorable;

  /// <summary>The claiming pool already has a registry entry on this machine (it should be visible in the list).</summary>
  public bool Registered { get; } = registered;
}

/// <summary>
/// Manifest-pool lifecycle operations behind the pool CLI verbs (FR-POOL-CLI): create,
/// import, adopt, add/remove member. Creating a pool writes the manifest (registry +
/// member markers) and initialises each member's marker folder; it never destroys
/// pre-existing data in a chosen folder without explicit force (SAFE-NONDESTRUCTIVE).
/// </summary>
public sealed class PoolLifecycle(IHostEnvironment host, ManifestStore store) {

  public sealed record MemberSpec(string Path, MemberRole Role = MemberRole.Capacity, string? Label = null, long ReserveBytes = 0, bool Network = false, string? Credential = null);

  public PoolManifest Create(string name, IEnumerable<MemberSpec> members, string? mountTarget = null, bool force = false, bool takeOver = false) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ManifestException("Pool requires a non-empty name");

    var memberSpecs = members.ToArray();
    if (memberSpecs.Length == 0)
      throw new ManifestException("Pool requires at least one member");

    var poolId = Guid.NewGuid();
    var definitions = new List<PoolMemberDefinition>();
    foreach (var spec in memberSpecs) {
      var scheme = MemberSchemes.SchemeOf(null, spec.Path);
      var remote = MemberSchemes.IsRemote(scheme);
      // expand a local ~/… or %VAR% path before it is checked, created and stored
      var path = remote ? spec.Path : MemberSchemes.ExpandLocal(spec.Path);
      if (!remote)
        this._EnsureMemberFolderUsable(path, poolId, force, takeOver: takeOver);

      definitions.Add(new() {
        MemberId = Guid.NewGuid(),
        Path = path,
        Role = spec.Role,
        Label = spec.Label,
        ReserveBytes = spec.ReserveBytes,
        Network = spec.Network || remote,
        Credential = spec.Credential,
        Scheme = remote ? scheme : null,
      });
    }

    foreach (var definition in definitions)
      if (definition.Scheme == null && !host.DirectoryExists(definition.Path))
        host.CreateDirectory(definition.Path);

    var manifest = new PoolManifest {
      PoolId = poolId,
      Name = name,
      Members = definitions,
      Mount = mountTarget == null ? null : new() { Target = MemberSchemes.ExpandLocal(mountTarget), VolumeLabel = name },
    };

    DriveBender.Logger($"Creating manifest pool '{name}' ({poolId}) with {definitions.Count} member(s)");
    return store.Save(manifest);
  }

  /// <summary>Imports an external manifest file: validates, persists to the registry and initialises reachable members.</summary>
  public PoolManifest Import(string manifestJson, bool force = false) {
    var manifest = ManifestSerializer.Parse(manifestJson);
    foreach (var member in manifest.Members)
      if (host.DirectoryExists(member.Path))
        this._EnsureMemberFolderUsable(member.Path, manifest.PoolId, force, memberId: member.MemberId);

    DriveBender.Logger($"Importing manifest pool '{manifest.Name}' ({manifest.PoolId})");
    return store.Save(manifest);
  }

  /// <summary>
  /// FR-ADOPT: materialises a discovered native pool's virtual manifest into an explicit,
  /// editable JSON manifest in place — no data is moved; only sidecar markers are written.
  /// </summary>
  public PoolManifest Adopt(PoolRef pool) {
    if (!pool.IsVirtual)
      throw new ManifestException($"Pool '{pool.Name}' is already an explicit manifest pool");

    DriveBender.Logger($"Adopting native pool '{pool.Name}' ({pool.PoolId}) into an explicit manifest (in place, no data moved)");
    return store.Save(pool.Manifest);
  }

  public PoolManifest AddMember(PoolManifest manifest, MemberSpec spec, bool force = false, bool takeOver = false) {
    if (manifest.IsVirtual)
      throw new ManifestException("Adopt the native pool first (pool adopt) before editing its membership");

    var scheme = MemberSchemes.SchemeOf(null, spec.Path);
    var remote = MemberSchemes.IsRemote(scheme);
    // expand a local ~/… or %VAR% path before dedup, checking, creating and storing
    var path = remote ? spec.Path : MemberSchemes.ExpandLocal(spec.Path);
    if (manifest.Members.Any(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
      throw new ManifestException($"'{path}' is already a member of pool '{manifest.Name}'");

    if (!remote) {
      this._EnsureMemberFolderUsable(path, manifest.PoolId, force, takeOver: takeOver);
      if (!host.DirectoryExists(path))
        host.CreateDirectory(path);
    }

    var updated = manifest with {
      Members = [.. manifest.Members, new PoolMemberDefinition {
        MemberId = Guid.NewGuid(),
        Path = path,
        Role = spec.Role,
        Label = spec.Label,
        ReserveBytes = spec.ReserveBytes,
        Network = spec.Network || remote,
        Credential = spec.Credential,
        Scheme = remote ? scheme : null,
      }],
    };

    DriveBender.Logger($"Adding member '{path}' to pool '{manifest.Name}'");
    return store.Save(updated);
  }

  /// <summary>
  /// Changes a member's role (capacity ↔ landing ↔ readonly) — reconfigure storage tiers
  /// without touching data. A mounted pool applies it live via the reload channel.
  /// </summary>
  public PoolManifest SetMemberRole(PoolManifest manifest, Guid memberId, MemberRole role) {
    if (manifest.IsVirtual)
      throw new ManifestException("Adopt the native pool first (pool adopt) before editing its membership");

    var member = manifest.FindMember(memberId)
                 ?? throw new ManifestException($"Pool '{manifest.Name}' has no member '{memberId}'");
    if (member.Role == role)
      return manifest;

    var updated = manifest with {
      Members = [.. manifest.Members.Select(m => m.MemberId == memberId ? m with { Role = role } : m)],
    };

    this._ValidateOrThrow(updated); // a role change that contradicts tiers.*.members must fail now, not at mount
    DriveBender.Logger($"Member '{member.Label ?? member.Path}' of pool '{manifest.Name}' is now role '{role}'");
    return store.Save(updated);
  }

  /// <summary>Removes a member from the manifest; its data stays untouched — only our sidecars are removed.</summary>
  public PoolManifest RemoveMember(PoolManifest manifest, Guid memberId) {
    var member = manifest.FindMember(memberId)
                 ?? throw new ManifestException($"Pool '{manifest.Name}' has no member '{memberId}'");

    if (manifest.Members.Count == 1)
      throw new ManifestException($"Cannot remove the last member of pool '{manifest.Name}'");

    // only strip OUR sidecars: a reassigned path could point at another pool's member, and
    // removing its marker/mirror would destroy that pool's identity + redundancy (SAFE-PHYS)
    if (host.DirectoryExists(member.Path) && this._MarkerBelongsToPool(member.Path, manifest.PoolId))
      store.RemoveMemberSidecars(member.Path);
    else if (host.DirectoryExists(member.Path))
      DriveBender.Logger($"[Warning]Leaving sidecars on '{member.Path}' — its marker does not identify pool '{manifest.Name}' (path may now point elsewhere)");

    DriveBender.Logger($"Removing member '{member.Path}' from pool '{manifest.Name}' (data stays in place)");
    return store.Save(manifest with { Members = [.. manifest.Members.Where(m => m.MemberId != memberId)] });
  }

  /// <summary>
  /// Removes a pool from the registry and its member sidecars. Data is preserved unless
  /// <paramref name="purgeData"/> is set, in which case each member's pool content is
  /// wiped too (destructive; the caller must confirm). Members' foreign data outside the
  /// pool layout is never touched.
  /// </summary>
  public void Delete(PoolManifest manifest, bool purgeData) {
    foreach (var member in manifest.Members) {
      if (!host.DirectoryExists(member.Path))
        continue;

      // CRITICAL: a member's Path is only a hint (a drive letter can be reassigned, a UNC
      // re-pointed). Never destroy data at a path unless its marker proves it is THIS pool's
      // member — otherwise a purge could recursively wipe an unrelated disk (SAFE-PHYS).
      if (!this._MarkerBelongsToPool(member.Path, manifest.PoolId)) {
        DriveBender.Logger($"[Warning]Skipping '{member.Path}' — its marker does not identify pool '{manifest.Name}'; refusing to touch data at an unverified path");
        continue;
      }

      if (purgeData) {
        // wipe the pool's on-disk content under this member (files + shadow/duplication folders,
        // incl. root-level FOLDER.DUPLICATE.$DRIVEBENDER which IS pool data, not a foreign sidecar)
        foreach (var file in host.EnumerateFiles(member.Path, "*"))
          host.DeleteFile(file);
        foreach (var dir in host.EnumerateDirectories(member.Path)) {
          var name = Path.GetFileName(dir)!;
          if (name.Equals(PoolPaths.UtilityFolderName, StringComparison.OrdinalIgnoreCase))
            continue; // removed explicitly below (marker/trash/mirror live here)
          host.DeleteDirectory(dir, recursive: true);
        }
      }

      store.RemoveMemberSidecars(member.Path);
      if (host.DirectoryExists(Path.Combine(member.Path, PoolPaths.UtilityFolderName)))
        host.DeleteDirectory(Path.Combine(member.Path, PoolPaths.UtilityFolderName), recursive: true);
    }

    store.DeleteRegistryEntry(manifest.PoolId);
    DriveBender.Logger($"{(purgeData ? "Purged" : "Deleted")} pool '{manifest.Name}' ({manifest.PoolId}){(purgeData ? " — data wiped" : " — data preserved")}");
  }

  /// <summary>
  /// Proves a folder is genuinely this pool's member before any destructive action touches it.
  /// A sidecar (marker OR mirror) that names a DIFFERENT pool blocks the action outright — even
  /// if the other sidecar happens to match — because a reassigned drive letter or re-pointed UNC
  /// carries the stranger pool's full sidecar set. At least one sidecar must positively name us.
  /// </summary>
  private bool _MarkerBelongsToPool(string memberPath, Guid poolId) {
    var marker = store.TryLoadMarker(memberPath);
    var mirror = store.TryLoadMemberMirror(memberPath);
    if (marker != null && marker.PoolId != poolId)
      return false;
    if (mirror != null && mirror.PoolId != poolId)
      return false;

    return marker?.PoolId == poolId || mirror?.PoolId == poolId;
  }

  public string Export(PoolManifest manifest) => ManifestSerializer.Write(manifest);

  /// <summary>
  /// Sets the duplication level D (total copies to keep) for a pool, either pool-wide or for a
  /// single folder/file glob (§6.3). Persisted into the manifest's defaults block; it takes effect
  /// on the next mount, after which the background duplicator creates any owed copies. Copies only
  /// ever land on independent physical volumes (SAFE-PHYS), so D&gt;domains keeps owed duplication
  /// pending rather than co-locating copies.
  /// </summary>
  public PoolManifest SetDuplication(PoolManifest manifest, int level, string? folderGlob = null, bool? allowSamePhysical = null, string? strategy = null) {
    if (level is < 1 or > 10)
      throw new ManifestException("Duplication level must be between 1 and 10 (1 = a single copy, no duplication)");
    if (manifest.IsVirtual)
      throw new ManifestException("Adopt the native pool first (pool adopt) before configuring duplication");

    var defaults = manifest.Defaults is { ValueKind: JsonValueKind.Object } existing
      ? JsonNode.Parse(existing.GetRawText())!.AsObject()
      : new JsonObject();

    if (string.IsNullOrWhiteSpace(folderGlob))
      defaults["duplication"] = level;
    else {
      if (defaults["folders"] is not JsonObject folders)
        defaults["folders"] = folders = new JsonObject();
      if (folders[folderGlob] is not JsonObject entry)
        folders[folderGlob] = entry = new JsonObject();
      entry["duplication"] = level;
    }

    if (allowSamePhysical != null) {
      // opting in lets copies co-locate on one disk (bit-rot protection, not disk-loss protection)
      if (defaults["placement"] is not JsonObject placement)
        defaults["placement"] = placement = new JsonObject();
      placement["shadowNeverSamePhysical"] = !allowSamePhysical.Value;
    }

    if (!string.IsNullOrWhiteSpace(strategy)) {
      if (strategy is not ("most-free-space" or "round-robin" or "least-used" or "lowest-latency"))
        throw new ManifestException($"Unknown placement strategy '{strategy}' (most-free-space | round-robin | least-used | lowest-latency)");

      if (defaults["placement"] is not JsonObject placement)
        defaults["placement"] = placement = new JsonObject();
      placement["strategy"] = strategy;
    }

    var updated = manifest with { Defaults = JsonSerializer.SerializeToElement(defaults) };
    this._ValidateOrThrow(updated); // never persist a config that would refuse the next mount
    DriveBender.Logger($"Set duplication of pool '{manifest.Name}'{(string.IsNullOrWhiteSpace(folderGlob) ? "" : $" for '{folderGlob}'")} to {level} cop{(level == 1 ? "y" : "ies")} (effective on next mount)");
    return store.Save(updated);
  }

  /// <summary>Validates a manifest's effective config exactly as the next mount would, so a lifecycle edit can never leave the pool unmountable.</summary>
  private void _ValidateOrThrow(PoolManifest manifest) {
    var globalConfigPath = Path.Combine(host.ConfigRoot, "config.json");
    var globalJson = host.FileExists(globalConfigPath) ? host.ReadAllText(globalConfigPath) : null;
    var effective = ConfigResolver.ResolveEffective(globalJson, manifest.Defaults?.GetRawText());
    ConfigValidator.Validate(effective, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    ConfigValidator.ValidateTierAssignments(manifest, effective);
  }

  /// <summary>
  /// Replaces the pool's whole defaults/settings block with an edited JSON object (the "change all
  /// settings" editor). Validated exactly as a mount would (built-in ← global ← these), so an
  /// invalid value is rejected with the validator's message rather than breaking the next mount.
  /// </summary>
  public PoolManifest SetConfig(PoolManifest manifest, string json) {
    if (manifest.IsVirtual)
      throw new ManifestException("Adopt the native pool first (pool adopt) before editing its settings");

    JsonElement element;
    try {
      element = JsonSerializer.Deserialize<JsonElement>(json);
    } catch (JsonException e) {
      throw new ManifestException($"Settings are not valid JSON: {e.Message}");
    }

    if (element.ValueKind != JsonValueKind.Object)
      throw new ManifestException("Settings must be a JSON object.");

    var updated = manifest with { Defaults = element };

    var globalConfigPath = Path.Combine(host.ConfigRoot, "config.json");
    var globalJson = host.FileExists(globalConfigPath) ? host.ReadAllText(globalConfigPath) : null;
    var effective = ConfigResolver.ResolveEffective(globalJson, element.GetRawText());
    ConfigValidator.Validate(effective, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    ConfigValidator.ValidateTierAssignments(updated, effective);

    DriveBender.Logger($"Updated settings of pool '{manifest.Name}' (effective on next mount)");
    return store.Save(updated);
  }

  /// <summary>
  /// Sets (or clears, when target is null/empty) the pool's default mount location — the drive
  /// letter/folder the UI's Mount button and automount use. A local ~/… or %VAR% target is
  /// expanded like a member path. Applies to the NEXT mount (an already-mounted pool keeps its
  /// current mountpoint until remounted).
  /// </summary>
  public PoolManifest SetMountTarget(PoolManifest manifest, string? target) {
    if (manifest.IsVirtual)
      throw new ManifestException("Adopt the native pool first (pool adopt) before editing its mount location");

    var trimmed = string.IsNullOrWhiteSpace(target) ? null : MemberSchemes.ExpandLocal(target.Trim());
    var mount = (manifest.Mount ?? new MountSpec()) with { Target = trimmed, VolumeLabel = manifest.Mount?.VolumeLabel ?? manifest.Name };
    var updated = manifest with { Mount = trimmed == null && manifest.Mount?.ReadOnly != true ? null : mount };

    DriveBender.Logger($"Set mount location of pool '{manifest.Name}' to '{trimmed ?? "(none)"}' (effective on next mount)");
    return store.Save(updated);
  }

  /// <summary>
  /// Rebuilds a pool the registry has lost from a member folder's manifest mirror and
  /// re-registers it (the "restore the old pool" choice for an orphaned member marker).
  /// Reconciles across the folder so the newest surviving copy wins (SAFE-MANIFEST).
  /// </summary>
  public PoolManifest Recover(string memberPath) {
    var mirror = store.TryLoadMemberMirror(memberPath)
      ?? throw new ManifestException($"'{memberPath}' has no recoverable manifest (its mirror copy is gone) — there is no old pool to restore here");

    var recovered = store.Reconcile(mirror.PoolId, [memberPath]) ?? mirror;
    DriveBender.Logger($"Recovered pool '{recovered.Name}' ({recovered.PoolId}) into the registry from the mirror at '{memberPath}'");
    return recovered;
  }

  /// <summary>
  /// Drops a pool from this machine's registry only. On-media markers/mirrors and all data
  /// are left intact, so the pool stays self-describing and can be re-imported or recovered
  /// later — the "remove from my list without deleting it" choice.
  /// </summary>
  public void Forget(PoolManifest manifest) {
    if (manifest.IsVirtual)
      throw new ManifestException($"'{manifest.Name}' is a native pool discovered from its drives — it has no registry entry to forget; detach its drives or delete it instead");

    store.DeleteRegistryEntry(manifest.PoolId);
    DriveBender.Logger($"Forgot pool '{manifest.Name}' ({manifest.PoolId}) — removed from this machine's registry; on-media markers and data untouched");
  }

  /// <summary>
  /// SAFE-NONDESTRUCTIVE: a folder already claimed by another pool is refused with an
  /// actionable conflict (recover it, or take it over); a non-empty unclaimed folder needs
  /// explicit force (its content becomes pool content). <paramref name="takeOver"/> is the
  /// caller's explicit consent to seize a foreign-claimed folder — its old marker is cleared.
  /// </summary>
  private void _EnsureMemberFolderUsable(string path, Guid poolId, bool force, Guid? memberId = null, bool takeOver = false) {
    if (!host.DirectoryExists(path))
      return;

    var marker = store.TryLoadMarker(path);
    if (marker != null) {
      if (marker.PoolId != poolId) {
        if (!takeOver)
          throw new MemberClaimConflictException(
            $"'{path}' already belongs to another pool ({marker.PoolId}).",
            path, marker.PoolId,
            restorable: store.TryLoadMemberMirror(path) != null,
            registered: store.TryLoadRegistry(marker.PoolId) != null);

        // explicit take-over: drop the foreign claim, then treat the folder as consented content
        DriveBender.Logger($"Taking over '{path}' from pool {marker.PoolId} at the user's request");
        store.RemoveMemberSidecars(path);
        return;
      }

      if (memberId != null && marker.MemberId != memberId)
        throw new NonDestructiveViolationException($"'{path}' is already member {marker.MemberId} of this pool — refusing to re-initialise it as {memberId}");

      return;
    }

    if (force || takeOver)
      return;

    var hasContent = host.EnumerateFiles(path, "*").Any(f => !PoolPaths.IsHiddenName(Path.GetFileName(f)))
                     || host.EnumerateDirectories(path).Any(d => !PoolPaths.IsHiddenName(Path.GetFileName(d)!));

    if (hasContent)
      throw new NonDestructiveViolationException($"'{path}' is not empty; its content would become pool content — pass force to consent (SAFE-NONDESTRUCTIVE)");
  }

}
