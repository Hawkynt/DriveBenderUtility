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

  /// <summary>
  /// Expands a LOCAL path the way a shell would, so a member/target typed as <c>~/test1/</c> works
  /// instead of being stored verbatim (which created a folder literally named <c>~</c>): a leading
  /// <c>~</c> / <c>~/</c> / <c>~\</c> becomes the user's home directory and environment variables
  /// (<c>%VAR%</c>) are expanded. Remote URIs and UNC shares are returned unchanged, and a bare
  /// drive letter like <c>X:</c> is preserved (no GetFullPath, which would resolve it to a cwd).
  /// </summary>
  public static string ExpandLocal(string path) {
    if (string.IsNullOrWhiteSpace(path))
      return path;

    if (IsRemote(SchemeOf(null, path)) || path.StartsWith(@"\\", StringComparison.Ordinal))
      return path; // remote endpoint or UNC share — not a local filesystem path

    var expanded = path.Trim();
    if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal)) {
      var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      expanded = expanded.Length == 1 ? home : Path.Combine(home, expanded[2..]);
    }

    return Environment.ExpandEnvironmentVariables(expanded);
  }

}

/// <summary>
/// Extension point for resolving members that live behind a protocol backend instead of
/// the host filesystem (SAFE-OFFLINE applies identically: unreachable remotes degrade).
/// </summary>
public interface IRemoteMemberResolver {
  bool CanResolve(PoolMemberDefinition definition);
  PoolMember Resolve(PoolManifest manifest, PoolMemberDefinition definition);
}
