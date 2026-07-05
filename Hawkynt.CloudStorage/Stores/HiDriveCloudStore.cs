using System.Text.Json;
using Hawkynt.CloudStorage.OAuth;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// Strato HiDrive store over the HiDrive REST API (<c>api.hidrive.strato.com/2.1</c>),
/// authenticated with an OAuth bearer token from the <see cref="IAccessTokenProvider"/>. The
/// service is path-addressed; <paramref name="rootPath"/> is the absolute HiDrive base (for
/// example <c>users/alias</c>). New files are created with <c>POST /file</c> and existing
/// ones replaced with <c>PUT /file</c>, matching the API's create-vs-update split.
/// </summary>
public sealed class HiDriveCloudStore(IAccessTokenProvider tokens, string rootPath) : ICloudStore {

  private readonly CloudRest _rest = new(tokens, "https://api.hidrive.strato.com/2.1/");
  private readonly string _rootPath = rootPath.Trim('/');

  private string _Remote(string path) => "/" + CloudPath.Combine(this._rootPath, path);

  private static string _Encode(string path) => Uri.EscapeDataString(path);

  private static DateTime _Unix(JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
      ? DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()).UtcDateTime
      : DateTime.MinValue;

  private static CloudMeta _Meta(JsonElement item) {
    var isFolder = item.TryGetProperty("type", out var type) && type.GetString() == "dir";
    var length = !isFolder && item.TryGetProperty("size", out var size) ? size.GetInt64() : 0;
    return new(isFolder, length, _Unix(item, "ctime"), _Unix(item, "mtime"));
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      this._rest.GetJson($"meta?path={_Encode(this._Remote(""))}&fields=type", "probe");
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string path)
    => this._rest.GetBytes($"file?path={_Encode(this._Remote(path))}", $"download {path}");

  public void Upload(string path, byte[] content) {
    var remote = this._Remote(path);
    if (this.Stat(path) is { IsFolder: false }) {
      this._rest.Send(HttpMethod.Put, $"file?path={_Encode(remote)}", $"upload {path}", new ByteArrayContent(content));
      return;
    }

    var parent = CloudPath.GetParent(remote.TrimStart('/'));
    this._rest.Send(HttpMethod.Post, $"file?dir=%2F{_Encode(parent)}&name={_Encode(CloudPath.GetName(remote))}", $"upload {path}", new ByteArrayContent(content));
  }

  public void DeleteFile(string path)
    => this._rest.Send(HttpMethod.Delete, $"file?path={_Encode(this._Remote(path))}", $"delete {path}");

  public CloudMeta? Stat(string path) {
    var item = this._rest.TryGetJson($"meta?path={_Encode(this._Remote(path))}&fields=name,type,size,ctime,mtime", $"stat {path}");
    return item == null ? null : _Meta(item.Value);
  }

  public void CreateFolder(string path)
    => this._rest.Send(HttpMethod.Post, $"dir?path={_Encode(this._Remote(path))}", $"mkdir {path}", tolerate: System.Net.HttpStatusCode.Conflict);

  public void DeleteFolder(string path)
    => this._rest.Send(HttpMethod.Delete, $"dir?path={_Encode(this._Remote(path))}", $"rmdir {path}");

  public IEnumerable<CloudEntry> List(string folder) {
    var listing = this._rest.GetJson($"dir?path={_Encode(this._Remote(folder))}&members=all&fields=members.name,members.type,members.size,members.mtime,members.ctime", $"list {folder}");
    if (!listing.TryGetProperty("members", out var members))
      yield break;

    foreach (var item in members.EnumerateArray()) {
      var meta = _Meta(item);
      yield return new(item.GetProperty("name").GetString() ?? "", meta.IsFolder, meta.Length, meta.ModifiedUtc);
    }
  }

  public void Dispose() => this._rest.Dispose();

}
