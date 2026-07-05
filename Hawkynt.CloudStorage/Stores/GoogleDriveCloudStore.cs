using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Drive.v3;
using Hawkynt.CloudStorage.OAuth;
using GoogleDriveFile = Google.Apis.Drive.v3.Data.File;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// Google Drive store over Google.Apis.Drive.v3. Drive is id-addressed, so paths resolve
/// segment by segment from the root and folder ids are cached per path. Authenticates either
/// with a user's own OAuth client (bearer token stamped per request, refreshed transparently)
/// or with a service-account key.
/// </summary>
public sealed class GoogleDriveCloudStore : ICloudStore {

  private const string _FOLDER_MIME = "application/vnd.google-apps.folder";

  private readonly DriveService _service;
  private readonly string _rootId;
  private readonly string _rootPath;
  private readonly Dictionary<string, string> _folderIdCache = new(StringComparer.Ordinal);

  /// <summary>"Bring your own client id" OAuth: the bearer token is refreshed per request via <paramref name="tokens"/>.</summary>
  public GoogleDriveCloudStore(IAccessTokenProvider tokens, string rootId, string rootPath)
    : this(new _TokenInterceptor(tokens), rootId, rootPath) {
  }

  /// <summary>Service-account authentication from a JSON key.</summary>
  public GoogleDriveCloudStore(string serviceAccountJson, string rootId, string rootPath)
    : this(GoogleCredential.FromJson(serviceAccountJson).CreateScoped(DriveService.Scope.Drive), rootId, rootPath) {
  }

  private GoogleDriveCloudStore(IConfigurableHttpClientInitializer initializer, string rootId, string rootPath) {
    this._rootId = rootId.Length == 0 ? "root" : rootId;
    this._rootPath = rootPath.Trim('/');
    this._service = new(new BaseClientService.Initializer {
      HttpClientInitializer = initializer,
      ApplicationName = "Hawkynt.CloudStorage",
    });
  }

  private sealed class _TokenInterceptor(IAccessTokenProvider tokens) : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor {
    public void Initialize(ConfigurableHttpClient httpClient) => httpClient.MessageHandler.AddExecuteInterceptor(this);

    public Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetAccessToken());
      return Task.CompletedTask;
    }
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      var request = this._service.Files.List();
      request.PageSize = 1;
      request.Q = $"'{this._rootId}' in parents and trashed = false";
      request.Execute();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  private static string _Escape(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");

  private GoogleDriveFile? _FindChild(string parentId, string name, bool? foldersOnly = null) {
    var request = this._service.Files.List();
    var mimeClause = foldersOnly switch {
      true => $" and mimeType = '{_FOLDER_MIME}'",
      false => $" and mimeType != '{_FOLDER_MIME}'",
      null => "",
    };
    request.Q = $"'{parentId}' in parents and name = '{_Escape(name)}' and trashed = false{mimeClause}";
    request.Fields = "files(id, name, mimeType, size, modifiedTime, createdTime)";
    request.PageSize = 1;
    return request.Execute().Files.FirstOrDefault();
  }

  private string? _ResolveFolderId(string folder) {
    var combined = this._rootPath.Length == 0 ? folder : $"{this._rootPath}/{folder}".Trim('/');
    if (combined.Length == 0)
      return this._rootId;

    if (this._folderIdCache.TryGetValue(combined, out var cached))
      return cached;

    var currentId = this._rootId;
    foreach (var segment in combined.Split('/')) {
      var child = this._FindChild(currentId, segment, foldersOnly: true);
      if (child == null)
        return null;

      currentId = child.Id;
    }

    this._folderIdCache[combined] = currentId;
    return currentId;
  }

  private GoogleDriveFile? _ResolveFile(string path) {
    var parentId = this._ResolveFolderId(CloudPath.GetParent(path));
    return parentId == null ? null : this._FindChild(parentId, CloudPath.GetName(path), foldersOnly: false);
  }

  public byte[] Download(string path) {
    var file = this._ResolveFile(path)
               ?? throw new CloudStorageException(CloudStorageError.NotFound, $"File not found: {path}");

    using var buffer = new MemoryStream();
    this._service.Files.Get(file.Id).Download(buffer);
    return buffer.ToArray();
  }

  public void Upload(string path, byte[] content) {
    var existing = this._ResolveFile(path);
    using var stream = new MemoryStream(content);
    if (existing != null) {
      this._service.Files.Update(new(), existing.Id, stream, "application/octet-stream").Upload();
      return;
    }

    var parentId = this._ResolveFolderId(CloudPath.GetParent(path))
                   ?? throw new CloudStorageException(CloudStorageError.NotFound, $"Parent folder missing for: {path}");

    var create = this._service.Files.Create(
      new() { Name = CloudPath.GetName(path), Parents = [parentId] },
      stream,
      "application/octet-stream");
    create.Fields = "id";
    create.Upload();
  }

  public void DeleteFile(string path) {
    var file = this._ResolveFile(path);
    if (file != null)
      this._service.Files.Delete(file.Id).Execute();
  }

  public CloudMeta? Stat(string path) {
    var parentId = this._ResolveFolderId(CloudPath.GetParent(path));
    if (parentId == null)
      return null;

    var child = this._FindChild(parentId, CloudPath.GetName(path));
    if (child == null)
      return null;

    var isFolder = child.MimeType == _FOLDER_MIME;
    var modified = child.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue;
    return new(isFolder, child.Size ?? 0, child.CreatedTimeDateTimeOffset?.UtcDateTime ?? modified, modified);
  }

  public void CreateFolder(string path) {
    var parentId = this._ResolveFolderId(CloudPath.GetParent(path))
                   ?? throw new CloudStorageException(CloudStorageError.NotFound, $"Parent folder missing for: {path}");

    var request = this._service.Files.Create(new() {
      Name = CloudPath.GetName(path),
      MimeType = _FOLDER_MIME,
      Parents = [parentId],
    });
    request.Fields = "id";
    request.Execute();
  }

  public void DeleteFolder(string path) {
    var folderId = this._ResolveFolderId(path);
    if (folderId != null && folderId != this._rootId) {
      this._service.Files.Delete(folderId).Execute();
      this._folderIdCache.Clear(); // ids under the deleted folder are gone
    }
  }

  public IEnumerable<CloudEntry> List(string folder) {
    var folderId = this._ResolveFolderId(folder)
                   ?? throw new CloudStorageException(CloudStorageError.NotFound, $"Folder not found: {folder}");

    string? pageToken = null;
    do {
      var request = this._service.Files.List();
      request.Q = $"'{folderId}' in parents and trashed = false";
      request.Fields = "nextPageToken, files(id, name, mimeType, size, modifiedTime)";
      request.PageToken = pageToken;
      var response = request.Execute();

      foreach (var file in response.Files)
        yield return new(file.Name, file.MimeType == _FOLDER_MIME, file.Size ?? 0, file.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue);

      pageToken = response.NextPageToken;
    } while (pageToken != null);
  }

  public void Dispose() => this._service.Dispose();

}
