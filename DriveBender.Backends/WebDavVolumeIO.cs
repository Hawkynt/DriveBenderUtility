using DivisonM.Vfs;
using WebDav;
using NetworkCredential = DivisonM.Vfs.NetworkCredential;

namespace DivisonM.Backends;

/// <summary>
/// WebDAV store over WebDav.Client (GET/PUT/PROPFIND/MKCOL/DELETE): whole-file, no
/// trusted atomic MOVE, no durable flush (§6.1 table).
/// </summary>
public sealed class WebDavStore : IWholeFileStore {

  private readonly WebDavClient _client;
  private readonly string _rootPath;

  public WebDavStore(Uri baseAddress, string rootPath, NetworkCredential credential) {
    this._rootPath = rootPath.Trim('/');
    this._client = new(new WebDavClientParams {
      BaseAddress = baseAddress,
      Credentials = credential.UserName.Length > 0
        ? new System.Net.NetworkCredential(credential.UserName, credential.Secret)
        : null,
    });
  }

  private string _Resource(string physicalPath) {
    var escaped = string.Join('/', physicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    return this._rootPath.Length == 0 ? escaped : escaped.Length == 0 ? this._rootPath : $"{this._rootPath}/{escaped}";
  }

  private static PoolFsException _FromStatus(int statusCode, string what) => statusCode switch {
    404 => new(PoolFsError.NotFound, $"{what}: not found"),
    401 or 403 => new(PoolFsError.AccessDenied, $"{what}: access denied"),
    405 or 409 => new(PoolFsError.Exists, $"{what}: conflict (HTTP {statusCode})"),
    507 => new(PoolFsError.NoSpace, $"{what}: storage exhausted"),
    _ => new(PoolFsError.IoError, $"{what}: HTTP {statusCode}"),
  };

  public void Connect() {
  }

  public bool Probe() {
    try {
      return this._client.Propfind(this._Resource(""), new() { ApplyTo = ApplyTo.Propfind.ResourceOnly }).GetAwaiter().GetResult().IsSuccessful;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    var response = this._client.GetRawFile(this._Resource(physicalPath)).GetAwaiter().GetResult();
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"GET {physicalPath}");

    using var stream = response.Stream;
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  public void Upload(string physicalPath, byte[] content) {
    var response = this._client.PutFile(this._Resource(physicalPath), new MemoryStream(content)).GetAwaiter().GetResult();
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"PUT {physicalPath}");
  }

  public void DeleteFile(string physicalPath) {
    var response = this._client.Delete(this._Resource(physicalPath)).GetAwaiter().GetResult();
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"DELETE {physicalPath}");
  }

  public StoreMeta? Stat(string physicalPath) {
    var response = this._client.Propfind(this._Resource(physicalPath), new() { ApplyTo = ApplyTo.Propfind.ResourceOnly }).GetAwaiter().GetResult();
    if (response.StatusCode == 404)
      return null;
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"PROPFIND {physicalPath}");

    var resource = response.Resources.FirstOrDefault();
    if (resource == null)
      return null;

    var modified = resource.LastModifiedDate?.ToUniversalTime() ?? DateTime.MinValue;
    return new(resource.IsCollection, resource.ContentLength ?? 0, resource.CreationDate?.ToUniversalTime() ?? modified, modified);
  }

  public void CreateFolder(string physicalPath) {
    var response = this._client.Mkcol(this._Resource(physicalPath)).GetAwaiter().GetResult();
    if (!response.IsSuccessful && response.StatusCode != 405 /* already exists */)
      throw _FromStatus(response.StatusCode, $"MKCOL {physicalPath}");
  }

  public void DeleteFolder(string physicalPath) {
    var response = this._client.Delete(this._Resource(physicalPath)).GetAwaiter().GetResult();
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"DELETE {physicalPath}");
  }

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    var response = this._client.Propfind(this._Resource(physicalFolder), new() { ApplyTo = ApplyTo.Propfind.ResourceAndChildren }).GetAwaiter().GetResult();
    if (response.StatusCode == 404)
      throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {physicalFolder}");
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"PROPFIND {physicalFolder}");

    var selfSegments = this._Resource(physicalFolder).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    foreach (var resource in response.Resources) {
      var decoded = Uri.UnescapeDataString(resource.Uri.TrimEnd('/'));
      var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length <= selfSegments)
        continue; // the collection itself

      var name = segments[^1];
      yield return new(name, resource.IsCollection, resource.ContentLength ?? 0, resource.LastModifiedDate?.ToUniversalTime() ?? DateTime.MinValue);
    }
  }

  public void Dispose() => this._client.Dispose();

}

/// <summary>WebDAV backend factory: webdav:// maps to http, webdavs://, davs:// to https.</summary>
public sealed class WebDavVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "webdav";
  public BackendCaps Caps => BackendCaps.List | BackendCaps.Delete | BackendCaps.ServerCredentials;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var secure = uri.Scheme is "webdavs" or "davs" or "https";
    var baseAddress = new UriBuilder(secure ? "https" : "http", uri.Host, uri.IsDefaultPort ? (secure ? 443 : 80) : uri.Port).Uri;
    var credential = MemberUri.ResolveCredential(member, credentials, uri, required: false);
    var store = new WebDavStore(baseAddress, MemberUri.RootPath(uri), credential);
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, $"webdav://{uri.Host.ToUpperInvariant()}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}/{MemberUri.RootPath(uri)}", store);
  }

}
