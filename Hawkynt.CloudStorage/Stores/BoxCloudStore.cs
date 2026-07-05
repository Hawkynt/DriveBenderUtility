using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hawkynt.CloudStorage.OAuth;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// Box store over the Box Content API (<c>api.box.com/2.0</c>, uploads via
/// <c>upload.box.com</c>). Box is id-addressed, so paths resolve segment by segment from the
/// root folder ("0" unless overridden) and folder ids are cached per path. Authenticated with
/// an OAuth bearer token from the <see cref="IAccessTokenProvider"/>.
/// </summary>
public sealed class BoxCloudStore : ICloudStore {

  private readonly CloudRest _rest;
  private readonly string _rootId;
  private readonly string _rootPath;
  private readonly Dictionary<string, string> _folderIdCache = new(StringComparer.Ordinal);

  public BoxCloudStore(IAccessTokenProvider tokens, string rootId, string rootPath) {
    this._rest = new(tokens, "https://api.box.com/2.0/");
    this._rootId = string.IsNullOrEmpty(rootId) ? "0" : rootId;
    this._rootPath = rootPath.Trim('/');
  }

  private static DateTime _Utc(JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
       && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
      ? parsed.UtcDateTime
      : DateTime.MinValue;

  /// <summary>The immediate child of a folder with the given name, or null; a Box item carries id/type/size/times.</summary>
  private JsonElement? _FindChild(string parentId, string name) {
    var offset = 0;
    const int limit = 1000;
    while (true) {
      var page = this._rest.GetJson($"folders/{parentId}/items?fields=name,type,size,modified_at,created_at&limit={limit}&offset={offset}", $"list folder {parentId}");
      var entries = page.GetProperty("entries");
      foreach (var entry in entries.EnumerateArray())
        if (entry.GetProperty("name").GetString() == name)
          return entry;

      offset += entries.GetArrayLength();
      if (offset >= page.GetProperty("total_count").GetInt32() || entries.GetArrayLength() == 0)
        return null;
    }
  }

  private string? _ResolveFolderId(string folder) {
    var combined = CloudPath.Combine(this._rootPath, folder);
    if (combined.Length == 0)
      return this._rootId;

    if (this._folderIdCache.TryGetValue(combined, out var cached))
      return cached;

    var currentId = this._rootId;
    foreach (var segment in combined.Split('/')) {
      var child = this._FindChild(currentId, segment);
      if (child is not { } found || found.GetProperty("type").GetString() != "folder")
        return null;

      currentId = found.GetProperty("id").GetString()!;
    }

    this._folderIdCache[combined] = currentId;
    return currentId;
  }

  private JsonElement? _ResolveEntry(string path) {
    var parentId = this._ResolveFolderId(CloudPath.GetParent(path));
    return parentId == null ? null : this._FindChild(parentId, CloudPath.GetName(path));
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      this._rest.GetJson($"folders/{this._rootId}?fields=id", "probe");
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string path) {
    var entry = this._ResolveEntry(path);
    if (entry is not { } found || found.GetProperty("type").GetString() != "file")
      throw new CloudStorageException(CloudStorageError.NotFound, $"File not found: {path}");

    return this._rest.GetBytes($"files/{found.GetProperty("id").GetString()}/content", $"download {path}");
  }

  public void Upload(string path, byte[] content) {
    var existing = this._ResolveEntry(path);
    if (existing is { } found && found.GetProperty("type").GetString() == "file") {
      var id = found.GetProperty("id").GetString();
      this._rest.SendJson(HttpMethod.Post, $"https://upload.box.com/api/2.0/files/{id}/content", $"upload {path}", _UploadBody(CloudPath.GetName(path), null, content));
      return;
    }

    var parentId = this._ResolveFolderId(CloudPath.GetParent(path))
                   ?? throw new CloudStorageException(CloudStorageError.NotFound, $"Parent folder missing for: {path}");
    this._rest.SendJson(HttpMethod.Post, "https://upload.box.com/api/2.0/files/content", $"upload {path}", _UploadBody(CloudPath.GetName(path), parentId, content));
  }

  private static MultipartFormDataContent _UploadBody(string name, string? parentId, byte[] content) {
    var attributes = parentId == null
      ? $"{{\"name\":{JsonSerializer.Serialize(name)}}}"
      : $"{{\"name\":{JsonSerializer.Serialize(name)},\"parent\":{{\"id\":\"{parentId}\"}}}}";
    var body = new MultipartFormDataContent {
      { new StringContent(attributes, Encoding.UTF8), "attributes" },
    };
    var file = new ByteArrayContent(content);
    file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    body.Add(file, "file", name);
    return body;
  }

  public void DeleteFile(string path) {
    var entry = this._ResolveEntry(path);
    if (entry is { } found && found.GetProperty("type").GetString() == "file")
      this._rest.Send(HttpMethod.Delete, $"files/{found.GetProperty("id").GetString()}", $"delete {path}");
  }

  public CloudMeta? Stat(string path) {
    var entry = this._ResolveEntry(path);
    if (entry is not { } found)
      return null;

    var isFolder = found.GetProperty("type").GetString() == "folder";
    var length = !isFolder && found.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
    return new(isFolder, length, _Utc(found, "created_at"), _Utc(found, "modified_at"));
  }

  public void CreateFolder(string path) {
    var parentId = this._ResolveFolderId(CloudPath.GetParent(path))
                   ?? throw new CloudStorageException(CloudStorageError.NotFound, $"Parent folder missing for: {path}");
    var payload = $"{{\"name\":{JsonSerializer.Serialize(CloudPath.GetName(path))},\"parent\":{{\"id\":\"{parentId}\"}}}}";
    this._rest.SendJson(HttpMethod.Post, "folders", $"mkdir {path}", new StringContent(payload, Encoding.UTF8, "application/json"));
  }

  public void DeleteFolder(string path) {
    var folderId = this._ResolveFolderId(path);
    if (folderId != null && folderId != this._rootId) {
      this._rest.Send(HttpMethod.Delete, $"folders/{folderId}", $"rmdir {path}");
      this._folderIdCache.Clear();
    }
  }

  public IEnumerable<CloudEntry> List(string folder) {
    var folderId = this._ResolveFolderId(folder)
                   ?? throw new CloudStorageException(CloudStorageError.NotFound, $"Folder not found: {folder}");

    var offset = 0;
    const int limit = 1000;
    while (true) {
      var page = this._rest.GetJson($"folders/{folderId}/items?fields=name,type,size,modified_at&limit={limit}&offset={offset}", $"list {folder}");
      var entries = page.GetProperty("entries");
      foreach (var entry in entries.EnumerateArray()) {
        var isFolder = entry.GetProperty("type").GetString() == "folder";
        var length = !isFolder && entry.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
        yield return new(entry.GetProperty("name").GetString() ?? "", isFolder, length, _Utc(entry, "modified_at"));
      }

      offset += entries.GetArrayLength();
      if (offset >= page.GetProperty("total_count").GetInt32() || entries.GetArrayLength() == 0)
        yield break;
    }
  }

  public void Dispose() => this._rest.Dispose();

}
