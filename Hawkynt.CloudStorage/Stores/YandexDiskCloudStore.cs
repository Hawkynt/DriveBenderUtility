using System.Globalization;
using System.Text.Json;
using Hawkynt.CloudStorage.OAuth;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// Yandex Disk store over the Cloud API (<c>cloud-api.yandex.net/v1/disk</c>). The service is
/// path-addressed, so no id resolution is needed; downloads and uploads go through the
/// short-lived hrefs the API issues. Authenticated by a bearer token from the
/// <see cref="IAccessTokenProvider"/>.
/// </summary>
public sealed class YandexDiskCloudStore(IAccessTokenProvider tokens, string rootPath) : ICloudStore {

  private readonly CloudRest _rest = new(tokens, "https://cloud-api.yandex.net/v1/disk/");
  private readonly string _rootPath = rootPath.Trim('/');

  private string _Remote(string path) {
    var combined = CloudPath.Combine(this._rootPath, path);
    return "/" + combined;
  }

  private static string _Resource(string remotePath, string? suffix = null)
    => $"resources{suffix}?path={Uri.EscapeDataString(remotePath)}";

  private static DateTime _Utc(JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
       && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
      ? parsed.UtcDateTime
      : DateTime.MinValue;

  private static CloudMeta _Meta(JsonElement item) {
    var isFolder = item.TryGetProperty("type", out var type) && type.GetString() == "dir";
    var length = !isFolder && item.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
    return new(isFolder, length, _Utc(item, "created"), _Utc(item, "modified"));
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      this._rest.GetJson("resources?path=%2F&fields=type", "probe");
      return true;
    } catch (Exception) {
      return false;
    }
  }

  /// <summary>
  /// Ranged and streaming transfers ride the provider's plain HTTP surface, so a block read costs
  /// its block rather than the whole object, and a large upload is sent from a stream rather than
  /// materialised in memory first.
  /// </summary>
  public CloudCaps Caps => CloudCaps.RangeRead | CloudCaps.StreamingUpload | CloudCaps.StreamingDownload;

  /// <summary>Yandex issues a short-lived signed href per transfer; the range/stream request goes to that.</summary>
  private string _DownloadHref(string path)
    => this._rest.GetJson(_Resource(this._Remote(path), "/download"), $"download {path}").GetProperty("href").GetString()!;

  public byte[] Download(string path) => this._rest.GetBytes(this._DownloadHref(path), $"download {path}");

  public Stream OpenRead(string path) => this._rest.GetStream(this._DownloadHref(path), $"download {path}");

  public Stream OpenReadRange(string path, long offset, long count)
    => count <= 0 ? EmptyStream.Instance : this._rest.GetRange(this._DownloadHref(path), offset, count, $"download {path}");

  public void Upload(string path, byte[] content) {
    using var buffer = new MemoryStream(content, false);
    this.Upload(path, buffer, content.LongLength);
  }

  public void Upload(string path, Stream content, long length = -1) {
    var link = this._rest.GetJson($"{_Resource(this._Remote(path), "/upload")}&overwrite=true", $"upload {path}");
    var body = new StreamContent(content);
    if (length >= 0)
      body.Headers.ContentLength = length;

    this._rest.Send(HttpMethod.Put, link.GetProperty("href").GetString()!, $"upload {path}", body);
  }

  public void DeleteFile(string path)
    => this._rest.Send(HttpMethod.Delete, $"{_Resource(this._Remote(path))}&permanently=true", $"delete {path}");

  public CloudMeta? Stat(string path) {
    var item = this._rest.TryGetJson($"{_Resource(this._Remote(path))}&fields=type,size,created,modified", $"stat {path}");
    return item == null ? null : _Meta(item.Value);
  }

  public void CreateFolder(string path)
    => this._rest.Send(HttpMethod.Put, _Resource(this._Remote(path)), $"mkdir {path}", tolerate: System.Net.HttpStatusCode.Conflict);

  public void DeleteFolder(string path)
    => this._rest.Send(HttpMethod.Delete, $"{_Resource(this._Remote(path))}&permanently=true", $"rmdir {path}");

  public IEnumerable<CloudEntry> List(string folder) {
    var remote = this._Remote(folder);
    var offset = 0;
    const int limit = 200;
    while (true) {
      var listing = this._rest.GetJson($"{_Resource(remote)}&limit={limit}&offset={offset}&fields=_embedded.items.name,_embedded.items.type,_embedded.items.size,_embedded.items.modified,_embedded.total", $"list {folder}");
      if (!listing.TryGetProperty("_embedded", out var embedded) || !embedded.TryGetProperty("items", out var items))
        yield break;

      var count = 0;
      foreach (var item in items.EnumerateArray()) {
        ++count;
        var meta = _Meta(item);
        yield return new(item.GetProperty("name").GetString() ?? "", meta.IsFolder, meta.Length, meta.ModifiedUtc);
      }

      if (count < limit)
        yield break;

      offset += limit;
    }
  }

  public void Dispose() => this._rest.Dispose();

}
