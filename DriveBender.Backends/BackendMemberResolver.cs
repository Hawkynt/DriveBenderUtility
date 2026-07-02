using DivisonM.Vfs;

namespace DivisonM.Backends;

/// <summary>
/// Resolves remote members through their protocol backend (FR-RESOLVE-MEMBER for
/// non-host paths): reachability is the online test, the backend's endpoint identity is
/// the failure domain. Opened volumes are cached so the mount host reuses the same
/// connection the resolver probed.
/// </summary>
public sealed class BackendMemberResolver(BackendRegistry registry, ICredentialResolver credentials) : IRemoteMemberResolver, IDisposable {

  private readonly Dictionary<Guid, IVolumeIO> _opened = [];
  private readonly Lock _lock = new();

  public bool CanResolve(PoolMemberDefinition definition) => MemberSchemes.IsRemoteMember(definition);

  public PoolMember Resolve(PoolManifest manifest, PoolMemberDefinition definition) {
    IVolumeIO volume;
    try {
      volume = this.OpenVolume(definition);
    } catch (Exception e) when (e is ManifestException or PoolFsException) {
      DriveBender.Logger($"[Warning]Remote member '{definition.Label ?? definition.Path}' cannot open: {e.Message}");
      return new(definition.MemberId, definition.Path, string.Empty, definition.Role, false, definition.ReserveBytes) {
        Network = true,
        Label = definition.Label,
      };
    }

    return new(definition.MemberId, definition.Path, volume.PhysicalVolumeId, definition.Role, volume.IsOnline, definition.ReserveBytes) {
      Network = true,
      Label = definition.Label,
      MarkerVerified = true, // identity is the endpoint itself; letters cannot change
    };
  }

  /// <summary>The (cached) opened volume for a member — the mount host builds its engine members from these.</summary>
  public IVolumeIO OpenVolume(PoolMemberDefinition definition) {
    lock (this._lock) {
      if (this._opened.TryGetValue(definition.MemberId, out var existing))
        return existing;

      var volume = registry.Open(
        definition.MemberId,
        definition.Label ?? definition.Path,
        definition.Path,
        definition.Scheme,
        definition.Credential,
        credentials);

      this._opened.Add(definition.MemberId, volume);
      return volume;
    }
  }

  public void Dispose() {
    lock (this._lock) {
      foreach (var volume in this._opened.Values.OfType<IDisposable>())
        volume.Dispose();

      this._opened.Clear();
    }
  }

}
