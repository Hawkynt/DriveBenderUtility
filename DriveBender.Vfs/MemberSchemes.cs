namespace DivisonM.Vfs;

/// <summary>
/// Member scheme classification (§6.0.1): local paths and UNC shares resolve by marker
/// scan on the host filesystem; everything else is a remote endpoint resolved by
/// reachability through its backend.
/// </summary>
public static class MemberSchemes {

  /// <summary>The scheme of a member: its explicit manifest value, else parsed from the path, else local.</summary>
  public static string SchemeOf(string? explicitScheme, string path) {
    if (!string.IsNullOrWhiteSpace(explicitScheme))
      return explicitScheme;

    if (path.StartsWith(@"\\", StringComparison.Ordinal))
      return "unc";

    var separator = path.IndexOf("://", StringComparison.Ordinal);
    return separator > 0 ? path[..separator] : "file";
  }

  public static bool IsRemote(string scheme) => scheme is not ("file" or "local" or "unc");

  public static bool IsRemoteMember(PoolMemberDefinition definition)
    => IsRemote(SchemeOf(definition.Scheme, definition.Path));

}

/// <summary>
/// Extension point for resolving members that live behind a protocol backend instead of
/// the host filesystem (SAFE-OFFLINE applies identically: unreachable remotes degrade).
/// </summary>
public interface IRemoteMemberResolver {
  bool CanResolve(PoolMemberDefinition definition);
  PoolMember Resolve(PoolManifest manifest, PoolMemberDefinition definition);
}
