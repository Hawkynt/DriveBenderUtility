using Hawkynt.CloudStorage.OAuth;
using WebDav;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// WebDAV store over WebDav.Client (GET/PUT/PROPFIND/MKCOL/DELETE): whole-file, no trusted
/// atomic MOVE, no durable flush. Authenticates with either HTTP Basic credentials or an
/// OAuth bearer token — the latter is how Strato HiDrive and Yandex Disk (over their WebDAV
/// gateways) are reached with a refreshable token.
/// </summary>
public sealed class WebDavCloudStore : ICloudStore {

  private readonly WebDavClient _client;
  private readonly string _rootPath;

  /// <summary>HTTP Basic (or anonymous when <paramref name="user"/> is empty) WebDAV endpoint.</summary>
  public WebDavCloudStore(Uri baseAddress, string rootPath, string? user = null, string? password = null) {
    this._rootPath = rootPath.Trim('/');
    this._client = new(new WebDavClientParams {
      BaseAddress = baseAddress,
      Credentials = !string.IsNullOrEmpty(user)
        ? new System.Net.NetworkCredential(user, password ?? "")
        : null,
    });
  }

  /// <summary>Bearer-authenticated WebDAV endpoint: the token is refreshed per request via <paramref name="tokens"/>.</summary>
  public WebDavCloudStore(Uri baseAddress, string rootPath, IAccessTokenProvider tokens) {
    this._rootPath = rootPath.Trim('/');
    var http = new HttpClient(new BearerInjectingHandler(tokens)) { BaseAddress = baseAddress };
    this._client = new(http);
  }

  private string _Resource(string path) {
    var escaped = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    return this._rootPath.Length == 0 ? escaped : escaped.Length == 0 ? this._rootPath : $"{this._rootPath}/{escaped}";
  }

  private static CloudStorageException _FromStatus(int statusCode, string what) => statusCode switch {
    404 => new(CloudStorageError.NotFound, $"{what}: not found"),
    401 or 403 => new(CloudStorageError.AccessDenied, $"{what}: access denied"),
    405 or 409 => new(CloudStorageError.Exists, $"{what}: conflict (HTTP {statusCode})"),
    507 => new(CloudStorageError.NoSpace, $"{what}: storage exhausted"),
    _ => new(CloudStorageError.IoError, $"{what}: HTTP {statusCode}"),
  };

  public void Connect() {
  }

  public bool Probe() {
    try {
      return SyncBridge.Run(() => this._client.Propfind(this._Resource(""), new() { ApplyTo = ApplyTo.Propfind.ResourceOnly })).IsSuccessful;
    } catch (Exception) {
      return false;
    }
  }

  /// <summary>
  /// WebDAV is HTTP, so a GET carrying a Range header returns just that window (RFC 9110); a
  /// server that ignores it answers with the whole body, which is still correct and is bounded
  /// locally. Uploads go straight from the caller's stream.
  /// </summary>
  public CloudCaps Caps => CloudCaps.RangeRead | CloudCaps.StreamingUpload | CloudCaps.StreamingDownload;

  public byte[] Download(string path) {
    using var stream = this.OpenRead(path);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  public Stream OpenRead(string path) {
    var response = SyncBridge.Run(() => this._client.GetRawFile(this._Resource(path)));
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"GET {path}");

    return new OwnedStream(response.Stream, response, -1);
  }

  public Stream OpenReadRange(string path, long offset, long count) {
    if (count <= 0)
      return EmptyStream.Instance;

    var response = SyncBridge.Run(() => this._client
      .GetRawFile(this._Resource(path), new() { Headers = [new("Range", $"bytes={offset}-{offset + count - 1}")] }));
    if (response.StatusCode == 416)
      return EmptyStream.Instance; // at or past the end
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"GET {path}");

    // 200 rather than 206 means the server ignored the range and is sending everything; the
    // window is cut out here so the caller always gets exactly what it asked for
    return new OwnedStream(response.StatusCode == 206 ? response.Stream : new WindowStream(response.Stream, offset, count), response, count);
  }

  public void Upload(string path, byte[] content) {
    using var buffer = new MemoryStream(content, false);
    this.Upload(path, buffer, content.LongLength);
  }

  /// <summary>Streams the body up — a large file is never held in memory as a byte[].</summary>
  public void Upload(string path, Stream content, long length = -1) {
    var response = SyncBridge.Run(() => this._client.PutFile(this._Resource(path), content));
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"PUT {path}");
  }

  public void DeleteFile(string path) {
    var response = SyncBridge.Run(() => this._client.Delete(this._Resource(path)));
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"DELETE {path}");
  }

  public CloudMeta? Stat(string path) {
    var response = SyncBridge.Run(() => this._client.Propfind(this._Resource(path), new() { ApplyTo = ApplyTo.Propfind.ResourceOnly }));
    if (response.StatusCode == 404)
      return null;
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"PROPFIND {path}");

    var resource = response.Resources.FirstOrDefault();
    if (resource == null)
      return null;

    var modified = resource.LastModifiedDate?.ToUniversalTime() ?? DateTime.MinValue;
    return new(resource.IsCollection, resource.ContentLength ?? 0, resource.CreationDate?.ToUniversalTime() ?? modified, modified);
  }

  public void CreateFolder(string path) {
    var response = SyncBridge.Run(() => this._client.Mkcol(this._Resource(path)));
    if (!response.IsSuccessful && response.StatusCode != 405 /* already exists */)
      throw _FromStatus(response.StatusCode, $"MKCOL {path}");
  }

  public void DeleteFolder(string path) {
    var response = SyncBridge.Run(() => this._client.Delete(this._Resource(path)));
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"DELETE {path}");
  }

  public IEnumerable<CloudEntry> List(string folder) {
    var response = SyncBridge.Run(() => this._client.Propfind(this._Resource(folder), new() { ApplyTo = ApplyTo.Propfind.ResourceAndChildren }));
    if (response.StatusCode == 404)
      throw new CloudStorageException(CloudStorageError.NotFound, $"Folder not found: {folder}");
    if (!response.IsSuccessful)
      throw _FromStatus(response.StatusCode, $"PROPFIND {folder}");

    var selfSegments = this._Resource(folder).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    foreach (var resource in response.Resources) {
      var decoded = Uri.UnescapeDataString(resource.Uri.TrimEnd('/'));
      var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length <= selfSegments)
        continue; // the collection itself

      yield return new(segments[^1], resource.IsCollection, resource.ContentLength ?? 0, resource.LastModifiedDate?.ToUniversalTime() ?? DateTime.MinValue);
    }
  }

  public void Dispose() => this._client.Dispose();

}
