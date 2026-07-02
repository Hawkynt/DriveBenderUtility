namespace DivisonM.Vfs;

/// <summary>An operation would touch pre-existing data without explicit consent (SAFE-NONDESTRUCTIVE).</summary>
public sealed class NonDestructiveViolationException(string message) : ManifestException(message);

/// <summary>
/// Manifest-pool lifecycle operations behind the pool CLI verbs (FR-POOL-CLI): create,
/// import, adopt, add/remove member. Creating a pool writes the manifest (registry +
/// member markers) and initialises each member's marker folder; it never destroys
/// pre-existing data in a chosen folder without explicit force (SAFE-NONDESTRUCTIVE).
/// </summary>
public sealed class PoolLifecycle(IHostEnvironment host, ManifestStore store) {

  public sealed record MemberSpec(string Path, MemberRole Role = MemberRole.Capacity, string? Label = null, long ReserveBytes = 0, bool Network = false, string? Credential = null);

  public PoolManifest Create(string name, IEnumerable<MemberSpec> members, string? mountTarget = null, bool force = false) {
    if (string.IsNullOrWhiteSpace(name))
      throw new ManifestException("Pool requires a non-empty name");

    var memberSpecs = members.ToArray();
    if (memberSpecs.Length == 0)
      throw new ManifestException("Pool requires at least one member");

    var poolId = Guid.NewGuid();
    var definitions = new List<PoolMemberDefinition>();
    foreach (var spec in memberSpecs) {
      this._EnsureMemberFolderUsable(spec.Path, poolId, force);
      definitions.Add(new() {
        MemberId = Guid.NewGuid(),
        Path = spec.Path,
        Role = spec.Role,
        Label = spec.Label,
        ReserveBytes = spec.ReserveBytes,
        Network = spec.Network,
        Credential = spec.Credential,
      });
    }

    foreach (var definition in definitions)
      if (!host.DirectoryExists(definition.Path))
        host.CreateDirectory(definition.Path);

    var manifest = new PoolManifest {
      PoolId = poolId,
      Name = name,
      Members = definitions,
      Mount = mountTarget == null ? null : new() { Target = mountTarget, VolumeLabel = name },
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

  public PoolManifest AddMember(PoolManifest manifest, MemberSpec spec, bool force = false) {
    if (manifest.IsVirtual)
      throw new ManifestException("Adopt the native pool first (pool adopt) before editing its membership");
    if (manifest.Members.Any(m => m.Path.Equals(spec.Path, StringComparison.OrdinalIgnoreCase)))
      throw new ManifestException($"'{spec.Path}' is already a member of pool '{manifest.Name}'");

    this._EnsureMemberFolderUsable(spec.Path, manifest.PoolId, force);
    if (!host.DirectoryExists(spec.Path))
      host.CreateDirectory(spec.Path);

    var updated = manifest with {
      Members = [.. manifest.Members, new PoolMemberDefinition {
        MemberId = Guid.NewGuid(),
        Path = spec.Path,
        Role = spec.Role,
        Label = spec.Label,
        ReserveBytes = spec.ReserveBytes,
        Network = spec.Network,
        Credential = spec.Credential,
      }],
    };

    DriveBender.Logger($"Adding member '{spec.Path}' to pool '{manifest.Name}'");
    return store.Save(updated);
  }

  /// <summary>Removes a member from the manifest; its data stays untouched — only our sidecars are removed.</summary>
  public PoolManifest RemoveMember(PoolManifest manifest, Guid memberId) {
    var member = manifest.FindMember(memberId)
                 ?? throw new ManifestException($"Pool '{manifest.Name}' has no member '{memberId}'");

    if (manifest.Members.Count == 1)
      throw new ManifestException($"Cannot remove the last member of pool '{manifest.Name}'");

    if (host.DirectoryExists(member.Path))
      store.RemoveMemberSidecars(member.Path);

    DriveBender.Logger($"Removing member '{member.Path}' from pool '{manifest.Name}' (data stays in place)");
    return store.Save(manifest with { Members = [.. manifest.Members.Where(m => m.MemberId != memberId)] });
  }

  public string Export(PoolManifest manifest) => ManifestSerializer.Write(manifest);

  /// <summary>
  /// SAFE-NONDESTRUCTIVE: a folder already claimed by another pool is always refused; a
  /// non-empty unclaimed folder needs explicit force (its content becomes pool content).
  /// </summary>
  private void _EnsureMemberFolderUsable(string path, Guid poolId, bool force, Guid? memberId = null) {
    if (!host.DirectoryExists(path))
      return;

    var marker = store.TryLoadMarker(path);
    if (marker != null) {
      if (marker.PoolId != poolId)
        throw new NonDestructiveViolationException($"'{path}' already belongs to another pool ({marker.PoolId}) — refusing regardless of force");
      if (memberId != null && marker.MemberId != memberId)
        throw new NonDestructiveViolationException($"'{path}' is already member {marker.MemberId} of this pool — refusing to re-initialise it as {memberId}");

      return;
    }

    if (force)
      return;

    var hasContent = host.EnumerateFiles(path, "*").Any(f => !PoolPaths.IsHiddenName(Path.GetFileName(f)))
                     || host.EnumerateDirectories(path).Any(d => !PoolPaths.IsHiddenName(Path.GetFileName(d)!));

    if (hasContent)
      throw new NonDestructiveViolationException($"'{path}' is not empty; its content would become pool content — pass force to consent (SAFE-NONDESTRUCTIVE)");
  }

}
