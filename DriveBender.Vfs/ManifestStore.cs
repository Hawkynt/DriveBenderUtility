namespace DivisonM.Vfs;

/// <summary>
/// Redundant manifest persistence (SAFE-MANIFEST): a registry copy under the machine
/// config dir plus a mirrored copy on every online member inside its
/// .drivebenderutility marker folder. Writes are atomic and versioned; the highest
/// version wins on conflict and stale copies are refreshed. A pool is reconstructable
/// from the registry entry or from any single member mirror.
/// </summary>
public sealed class ManifestStore(IHostEnvironment host) {

  public string RegistryDirectory => Path.Combine(host.ConfigRoot, "pools");

  public string RegistryPathFor(Guid poolId) => Path.Combine(this.RegistryDirectory, $"{poolId:D}.json");

  private static string _UtilityDir(string memberPath) => Path.Combine(memberPath, PoolPaths.UtilityFolderName);
  public static string MirrorPathFor(string memberPath) => Path.Combine(_UtilityDir(memberPath), PoolPaths.ManifestMirrorFileName);
  public static string MarkerPathFor(string memberPath) => Path.Combine(_UtilityDir(memberPath), PoolPaths.MemberMarkerFileName);

  /// <summary>
  /// Persists the manifest with a bumped version: registry first, then a mirror + marker
  /// on every member whose path is resolved and online. Returns the persisted manifest.
  /// </summary>
  public PoolManifest Save(PoolManifest manifest, IReadOnlyDictionary<Guid, string>? resolvedMemberPaths = null) {
    var persisted = manifest with { Version = manifest.Version + 1, IsVirtual = false };
    var json = ManifestSerializer.Write(persisted);

    host.CreateDirectory(this.RegistryDirectory);
    host.WriteAllTextAtomic(this.RegistryPathFor(persisted.PoolId), json);

    foreach (var member in persisted.Members) {
      var path = resolvedMemberPaths != null && resolvedMemberPaths.TryGetValue(member.MemberId, out var resolved)
        ? resolved
        : member.Path;

      if (!host.DirectoryExists(path))
        continue;

      try {
        host.CreateDirectory(_UtilityDir(path));
        host.WriteAllTextAtomic(MirrorPathFor(path), json);
        host.WriteAllTextAtomic(MarkerPathFor(path), ManifestSerializer.WriteMarker(new() {
          PoolId = persisted.PoolId,
          MemberId = member.MemberId,
          Name = member.Label ?? persisted.Name,
        }));
      } catch (IOException e) {
        DriveBender.Logger($"[Warning]Could not mirror manifest to member '{path}': {e.Message}");
      } catch (UnauthorizedAccessException e) {
        DriveBender.Logger($"[Warning]Could not mirror manifest to member '{path}': {e.Message}");
      }
    }

    return persisted;
  }

  public IEnumerable<PoolManifest> LoadRegistry() {
    foreach (var file in host.EnumerateFiles(this.RegistryDirectory, "*.json")) {
      PoolManifest manifest;
      try {
        manifest = ManifestSerializer.Parse(host.ReadAllText(file));
      } catch (ManifestException e) {
        DriveBender.Logger($"[Warning]Skipping invalid pool manifest '{file}': {e.Message}");
        continue;
      }

      yield return manifest;
    }
  }

  public PoolManifest? TryLoadRegistry(Guid poolId) {
    var path = this.RegistryPathFor(poolId);
    return host.FileExists(path) ? this._TryParse(path) : null;
  }

  public PoolManifest? TryLoadMemberMirror(string memberPath) {
    var path = MirrorPathFor(memberPath);
    return host.FileExists(path) ? this._TryParse(path) : null;
  }

  public MemberMarker? TryLoadMarker(string memberPath) {
    var path = MarkerPathFor(memberPath);
    if (!host.FileExists(path))
      return null;

    try {
      return ManifestSerializer.ParseMarker(host.ReadAllText(path));
    } catch (ManifestException e) {
      DriveBender.Logger($"[Warning]Ignoring invalid member marker '{path}': {e.Message}");
      return null;
    }
  }

  private PoolManifest? _TryParse(string path) {
    try {
      return ManifestSerializer.Parse(host.ReadAllText(path));
    } catch (ManifestException e) {
      DriveBender.Logger($"[Warning]Ignoring invalid manifest copy '{path}': {e.Message}");
      return null;
    }
  }

  /// <summary>
  /// Resolves divergent redundant copies: gathers the registry copy plus every member
  /// mirror, picks the highest version, refreshes stale copies, and returns the winner
  /// (SAFE-MANIFEST). Returns null when no copy exists anywhere.
  /// </summary>
  public PoolManifest? Reconcile(Guid poolId, IEnumerable<string> candidateMemberPaths) {
    var copies = new List<PoolManifest>();
    if (this.TryLoadRegistry(poolId) is { } registryCopy)
      copies.Add(registryCopy);

    var paths = candidateMemberPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    foreach (var memberPath in paths)
      if (this.TryLoadMemberMirror(memberPath) is { } mirror && mirror.PoolId == poolId)
        copies.Add(mirror);

    if (copies.Count == 0)
      return null;

    var winner = copies.OrderByDescending(m => m.Version).First();
    var json = ManifestSerializer.Write(winner);

    host.CreateDirectory(this.RegistryDirectory);
    host.WriteAllTextAtomic(this.RegistryPathFor(poolId), json);
    foreach (var memberPath in paths) {
      if (!host.DirectoryExists(memberPath))
        continue;

      var mirror = this.TryLoadMemberMirror(memberPath);
      if (mirror == null || mirror.Version < winner.Version) {
        host.CreateDirectory(_UtilityDir(memberPath));
        host.WriteAllTextAtomic(MirrorPathFor(memberPath), json);
      }
    }

    return winner;
  }

  /// <summary>Removes a member's marker and mirror (used by remove-member); never touches user data.</summary>
  public void RemoveMemberSidecars(string memberPath) {
    foreach (var path in new[] { MarkerPathFor(memberPath), MirrorPathFor(memberPath) })
      if (host.FileExists(path))
        host.DeleteFile(path);
  }

  public void DeleteRegistryEntry(Guid poolId) {
    var path = this.RegistryPathFor(poolId);
    if (host.FileExists(path))
      host.DeleteFile(path);
  }

}
