namespace DivisonM.Vfs;

/// <summary>
/// One resolved pool member (§6.0.7): the live path, its physical failure domain
/// (SAFE-PHYS), and whether it is online. <see cref="MarkerVerified"/> is false when the
/// member was accepted by path hint alone (no marker on disk yet).
/// </summary>
public sealed record PoolMember(
  Guid MemberId,
  string ResolvedPath,
  string PhysicalVolumeId,
  MemberRole Role,
  bool Online,
  long ReserveBytes
) {
  public bool Network { get; init; }
  public string? Label { get; init; }
  public bool MarkerVerified { get; init; }
  public bool PathChanged { get; init; }
}

/// <summary>
/// Resolves manifest members to live paths (FR-RESOLVE-MEMBER): first the manifest's
/// last-known path hint, then a scan of candidate roots (all local volumes, configured
/// search paths, and their immediate subdirectories) for a marker matching
/// (poolId, memberId). Resolution is by marker content, not path — A:\ today and E:\
/// tomorrow resolve to the same member.
/// </summary>
public sealed class MemberResolver(IHostEnvironment host, ManifestStore store, IReadOnlyList<string>? searchPaths = null) {

  public PoolMember Resolve(PoolManifest manifest, PoolMemberDefinition definition) {
    // 1. the last-known path hint, verified by marker content
    if (host.DirectoryExists(definition.Path)) {
      var marker = store.TryLoadMarker(definition.Path);
      if (marker != null && marker.PoolId == manifest.PoolId && marker.MemberId == definition.MemberId)
        return this._Online(definition, definition.Path, markerVerified: true, pathChanged: false);

      // a virtual (scan-synthesized) manifest's paths are authoritative — they came from the live scan;
      // an explicit manifest accepts a marker-less hint only if no marker claims the folder for another pool
      if (marker == null && (manifest.IsVirtual || !this._MarkerExistsAnywhereFor(manifest.PoolId, definition.MemberId)))
        return this._Online(definition, definition.Path, markerVerified: manifest.IsVirtual, pathChanged: false);
    }

    // 2. scan candidate roots for the marker — drive letters change, UNC hosts move
    foreach (var candidate in this._EnumerateCandidates()) {
      var marker = store.TryLoadMarker(candidate);
      if (marker != null && marker.PoolId == manifest.PoolId && marker.MemberId == definition.MemberId)
        return this._Online(definition, candidate, markerVerified: true, pathChanged: !candidate.Equals(definition.Path, StringComparison.OrdinalIgnoreCase));
    }

    // offline (SAFE-OFFLINE): keep the hint so the member can be reconciled on return
    return new(definition.MemberId, definition.Path, string.Empty, definition.Role, false, definition.ReserveBytes) {
      Network = definition.Network,
      Label = definition.Label,
    };
  }

  /// <summary>Resolves all members; newly resolved paths should be written back to the manifest by the caller.</summary>
  public IReadOnlyList<PoolMember> ResolveAll(PoolManifest manifest) => [.. manifest.Members.Select(m => this.Resolve(manifest, m))];

  private PoolMember _Online(PoolMemberDefinition definition, string path, bool markerVerified, bool pathChanged) {
    var identity = host.GetVolumeIdentity(path);
    return new(definition.MemberId, path, identity.PhysicalVolumeId, definition.Role, true, definition.ReserveBytes) {
      Network = definition.Network,
      Label = definition.Label,
      MarkerVerified = markerVerified,
      PathChanged = pathChanged,
    };
  }

  private bool _MarkerExistsAnywhereFor(Guid poolId, Guid memberId) {
    foreach (var candidate in this._EnumerateCandidates()) {
      var marker = store.TryLoadMarker(candidate);
      if (marker != null && marker.PoolId == poolId && marker.MemberId == memberId)
        return true;
    }

    return false;
  }

  private IEnumerable<string> _EnumerateCandidates() {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var root in host.EnumerateVolumeRoots()) {
      if (seen.Add(root))
        yield return root;

      foreach (var sub in this._SafeSubdirectories(root))
        if (seen.Add(sub))
          yield return sub;
    }

    foreach (var searchPath in searchPaths ?? []) {
      if (seen.Add(searchPath))
        yield return searchPath;

      foreach (var sub in this._SafeSubdirectories(searchPath))
        if (seen.Add(sub))
          yield return sub;
    }
  }

  private IEnumerable<string> _SafeSubdirectories(string directory) {
    try {
      return host.EnumerateDirectories(directory).ToArray();
    } catch (IOException) {
      return [];
    } catch (UnauthorizedAccessException) {
      return [];
    }
  }

}
