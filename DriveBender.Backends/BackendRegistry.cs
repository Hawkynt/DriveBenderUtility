using DivisonM.Vfs;

namespace DivisonM.Backends;

/// <summary>
/// Maps member schemes to <see cref="IVolumeIOBackend"/> factories (§6.1): local paths
/// and UNC shares stay on the local backend; ftp/ftps, sftp, webdav/webdavs and the
/// SharpGrip cloud providers each get their capability-honest whole-file backend.
/// </summary>
public sealed class BackendRegistry {

  private readonly Dictionary<string, IVolumeIOBackend> _backends = new(StringComparer.OrdinalIgnoreCase);

  public void Register(IVolumeIOBackend backend, params string[] extraSchemes) {
    this._backends[backend.Scheme] = backend;
    foreach (var scheme in extraSchemes)
      this._backends[scheme] = backend;
  }

  public IReadOnlyCollection<string> Schemes => this._backends.Keys;

  /// <summary>Everything the utility can pool over, ready to open members.</summary>
  public static BackendRegistry CreateDefault(IHostEnvironment host) {
    var registry = new BackendRegistry();
    registry.Register(new LocalVolumeIOBackend(host), "unc", "local");
    registry.Register(new FtpVolumeIOBackend(), "ftps");
    registry.Register(new SftpVolumeIOBackend(), "ssh");
    registry.Register(new WebDavVolumeIOBackend(), "webdavs", "dav", "davs");
    registry.Register(new AmazonS3VolumeIOBackend());
    registry.Register(new AzureBlobVolumeIOBackend());
    registry.Register(new AzureFileVolumeIOBackend());
    registry.Register(new DropboxVolumeIOBackend());
    registry.Register(new OneDriveVolumeIOBackend());
    registry.Register(new GoogleDriveVolumeIOBackend());
    registry.Register(new GoogleCloudStorageVolumeIOBackend());
    return registry;
  }

  /// <summary>The scheme of a member: its explicit manifest value, else parsed from the path, else local.</summary>
  public static string SchemeOf(string? explicitScheme, string path) => MemberSchemes.SchemeOf(explicitScheme, path);

  public static bool IsRemoteScheme(string scheme) => MemberSchemes.IsRemote(scheme);

  public IVolumeIO Open(Guid memberId, string displayName, string path, string? explicitScheme, string? credentialReference, ICredentialResolver? credentials) {
    var scheme = SchemeOf(explicitScheme, path);
    if (!this._backends.TryGetValue(scheme, out var backend))
      throw new ManifestException($"No backend registered for scheme '{scheme}' (member '{displayName}'); known schemes: {string.Join(", ", this._backends.Keys.OrderBy(k => k))}");

    return backend.Open(new(memberId, displayName, path, credentialReference), credentials);
  }

}

/// <summary>Shared parsing for URI-shaped member paths (ftp://user@host:21/root, s3://bucket/prefix, …).</summary>
internal static class MemberUri {

  public static Uri Parse(MemberDescriptor member) {
    if (!Uri.TryCreate(member.Path, UriKind.Absolute, out var uri))
      throw new ManifestException($"Member '{member.DisplayName}': '{member.Path}' is not a valid URI");

    return uri;
  }

  /// <summary>Root path inside the remote service, without a leading slash.</summary>
  public static string RootPath(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');

  /// <summary>Failure-domain identity: the service endpoint, never containing credentials (SAFE-PHYS, SEC-CRED).</summary>
  public static string PhysicalId(Uri uri)
    => $"{uri.Scheme}://{uri.Host.ToUpperInvariant()}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}/{RootPath(uri)}";

  public static NetworkCredential ResolveCredential(MemberDescriptor member, ICredentialResolver? credentials, Uri uri, bool required = true) {
    if (member.CredentialReference is { } reference && credentials?.Resolve(reference) is { } resolved)
      return resolved;

    // URI user info is accepted for the *user name* only; secrets stay in the store (SEC-CRED)
    var userInfo = Uri.UnescapeDataString(uri.UserInfo);
    if (userInfo.Length > 0 && !userInfo.Contains(':'))
      return new(userInfo, "");

    if (required)
      throw new ManifestException($"Member '{member.DisplayName}' needs a credential: set one with 'dbmount credential set <name>' and reference it as cred-ref:<name>");

    return new("", "");
  }

}
