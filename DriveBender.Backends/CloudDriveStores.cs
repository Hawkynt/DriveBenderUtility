using Azure;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using DivisonM.Vfs;
using Dropbox.Api;
using Dropbox.Api.Files;
using Google.Apis.Drive.v3;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using GoogleDriveFile = Google.Apis.Drive.v3.Data.File;

namespace DivisonM.Backends;

/// <summary>Azure File Storage store over Azure.Storage.Files.Shares — a real directory tree.</summary>
public sealed class AzureFileStore(ShareClient share, string rootPath) : IWholeFileStore {

  private string _Map(string physicalPath) => rootPath.Length == 0 ? physicalPath : $"{rootPath}/{physicalPath}".Trim('/');

  private ShareDirectoryClient _Directory(string physicalFolder) {
    var mapped = this._Map(physicalFolder);
    return mapped.Length == 0 ? share.GetRootDirectoryClient() : share.GetDirectoryClient(mapped);
  }

  private ShareFileClient _File(string physicalPath) {
    var mapped = this._Map(physicalPath);
    var slash = mapped.LastIndexOf('/');
    var directory = slash < 0 ? share.GetRootDirectoryClient() : share.GetDirectoryClient(mapped[..slash]);
    return directory.GetFileClient(mapped[(slash + 1)..]);
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      return share.Exists().Value;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    try {
      using var download = this._File(physicalPath).Download().Value;
      using var buffer = new MemoryStream();
      download.Content.CopyTo(buffer);
      return buffer.ToArray();
    } catch (RequestFailedException e) when (e.Status == 404) {
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content) {
    var file = this._File(physicalPath);
    file.Create(content.Length);
    if (content.Length > 0)
      file.UploadRange(new HttpRange(0, content.Length), new MemoryStream(content));
  }

  public void DeleteFile(string physicalPath) => this._File(physicalPath).DeleteIfExists();

  public StoreMeta? Stat(string physicalPath) {
    var file = this._File(physicalPath);
    try {
      var properties = file.GetProperties().Value;
      return new(false, properties.ContentLength, properties.SmbProperties.FileCreatedOn?.UtcDateTime ?? DateTime.MinValue, properties.LastModified.UtcDateTime);
    } catch (RequestFailedException e) when (e.Status is 404 or 409) {
      // not a file — maybe a directory
    }

    try {
      var directory = this._Directory(physicalPath);
      var properties = directory.GetProperties().Value;
      return new(true, 0, properties.SmbProperties.FileCreatedOn?.UtcDateTime ?? DateTime.MinValue, properties.LastModified.UtcDateTime);
    } catch (RequestFailedException e) when (e.Status is 404 or 409) {
      return null;
    }
  }

  public void CreateFolder(string physicalPath) => this._Directory(physicalPath).CreateIfNotExists();

  public void DeleteFolder(string physicalPath) => this._Directory(physicalPath).DeleteIfExists();

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    foreach (var item in this._Directory(physicalFolder).GetFilesAndDirectories(new ShareDirectoryGetFilesAndDirectoriesOptions { Traits = ShareFileTraits.Timestamps }))
      yield return new(item.Name, item.IsDirectory, item.FileSize ?? 0, item.Properties?.LastWrittenOn?.UtcDateTime ?? DateTime.MinValue);
  }

  public void Dispose() {
  }

}

/// <summary>Dropbox store over Dropbox.Api; Dropbox paths are "/rooted".</summary>
public sealed class DropboxStore(DropboxClient client, string rootPath) : IWholeFileStore {

  private string _Map(string physicalPath) {
    var combined = rootPath.Length == 0 ? physicalPath : $"{rootPath}/{physicalPath}".Trim('/');
    return combined.Length == 0 ? "" : "/" + combined;
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      client.Files.ListFolderAsync(this._Map(""), limit: 1).GetAwaiter().GetResult();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    try {
      using var response = client.Files.DownloadAsync(this._Map(physicalPath)).GetAwaiter().GetResult();
      return response.GetContentAsByteArrayAsync().GetAwaiter().GetResult();
    } catch (ApiException<DownloadError> e) when (e.ErrorResponse.IsPath) {
      throw new PoolFsException(PoolFsError.NotFound, $"File not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content)
    => client.Files.UploadAsync(this._Map(physicalPath), WriteMode.Overwrite.Instance, body: new MemoryStream(content)).GetAwaiter().GetResult();

  public void DeleteFile(string physicalPath)
    => client.Files.DeleteV2Async(this._Map(physicalPath)).GetAwaiter().GetResult();

  public StoreMeta? Stat(string physicalPath) {
    try {
      var metadata = client.Files.GetMetadataAsync(this._Map(physicalPath)).GetAwaiter().GetResult();
      if (metadata.IsFolder)
        return new(true, 0, DateTime.MinValue, DateTime.MinValue);

      var file = metadata.AsFile;
      return new(false, (long)file.Size, file.ClientModified.ToUniversalTime(), file.ServerModified.ToUniversalTime());
    } catch (ApiException<GetMetadataError> e) when (e.ErrorResponse.IsPath) {
      return null;
    }
  }

  public void CreateFolder(string physicalPath)
    => client.Files.CreateFolderV2Async(this._Map(physicalPath)).GetAwaiter().GetResult();

  public void DeleteFolder(string physicalPath)
    => client.Files.DeleteV2Async(this._Map(physicalPath)).GetAwaiter().GetResult();

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    var result = client.Files.ListFolderAsync(this._Map(physicalFolder)).GetAwaiter().GetResult();
    while (true) {
      foreach (var entry in result.Entries) {
        if (entry.IsFile) {
          var file = entry.AsFile;
          yield return new(entry.Name, false, (long)file.Size, file.ServerModified.ToUniversalTime());
        } else if (entry.IsFolder)
          yield return new(entry.Name, true, 0, DateTime.MinValue);
      }

      if (!result.HasMore)
        break;

      result = client.Files.ListFolderContinueAsync(result.Cursor).GetAwaiter().GetResult();
    }
  }

  public void Dispose() => client.Dispose();

}

/// <summary>Microsoft OneDrive store over Microsoft.Graph (drive-item path addressing).</summary>
public sealed class OneDriveStore(GraphServiceClient client, string driveId, string rootPath) : IWholeFileStore {

  private string _ItemId(string physicalPath) {
    var combined = rootPath.Length == 0 ? physicalPath : $"{rootPath}/{physicalPath}".Trim('/');
    return combined.Length == 0 ? "root" : $"root:/{combined}:";
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      client.Drives[driveId].Items["root"].GetAsync().GetAwaiter().GetResult();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  private DriveItem? _TryGetItem(string physicalPath) {
    try {
      return client.Drives[driveId].Items[this._ItemId(physicalPath)].GetAsync().GetAwaiter().GetResult();
    } catch (ODataError e) when (e.ResponseStatusCode == 404) {
      return null;
    }
  }

  public byte[] Download(string physicalPath) {
    var stream = client.Drives[driveId].Items[this._ItemId(physicalPath)].Content.GetAsync().GetAwaiter().GetResult()
                 ?? throw new PoolFsException(PoolFsError.NotFound, $"File not found: {physicalPath}");

    using (stream) {
      using var buffer = new MemoryStream();
      stream.CopyTo(buffer);
      return buffer.ToArray();
    }
  }

  public void Upload(string physicalPath, byte[] content)
    => client.Drives[driveId].Items[this._ItemId(physicalPath)].Content.PutAsync(new MemoryStream(content)).GetAwaiter().GetResult();

  public void DeleteFile(string physicalPath)
    => client.Drives[driveId].Items[this._ItemId(physicalPath)].DeleteAsync().GetAwaiter().GetResult();

  public StoreMeta? Stat(string physicalPath) {
    var item = this._TryGetItem(physicalPath);
    if (item == null)
      return null;

    var modified = item.LastModifiedDateTime?.UtcDateTime ?? DateTime.MinValue;
    return new(item.Folder != null, item.Size ?? 0, item.CreatedDateTime?.UtcDateTime ?? modified, modified);
  }

  public void CreateFolder(string physicalPath) {
    var name = PoolPaths.GetName(physicalPath);
    var parent = PoolPaths.GetParent(physicalPath);
    client.Drives[driveId].Items[this._ItemId(parent)].Children.PostAsync(new DriveItem {
      Name = name,
      Folder = new Folder(),
    }).GetAwaiter().GetResult();
  }

  public void DeleteFolder(string physicalPath)
    => client.Drives[driveId].Items[this._ItemId(physicalPath)].DeleteAsync().GetAwaiter().GetResult();

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    var page = client.Drives[driveId].Items[this._ItemId(physicalFolder)].Children.GetAsync().GetAwaiter().GetResult();
    while (page != null) {
      foreach (var item in page.Value ?? [])
        yield return new(item.Name ?? "", item.Folder != null, item.Size ?? 0, item.LastModifiedDateTime?.UtcDateTime ?? DateTime.MinValue);

      if (page.OdataNextLink == null)
        break;

      page = client.Drives[driveId].Items[this._ItemId(physicalFolder)].Children
        .WithUrl(page.OdataNextLink).GetAsync().GetAwaiter().GetResult();
    }
  }

  public void Dispose() {
  }

}

/// <summary>
/// Google Drive store over Google.Apis.Drive.v3. Drive is id-addressed, so paths resolve
/// segment by segment from the root; folder ids are cached per path.
/// </summary>
public sealed class GoogleDriveStore(DriveService service, string rootId, string rootPath) : IWholeFileStore {

  private const string _FOLDER_MIME = "application/vnd.google-apps.folder";

  private readonly Dictionary<string, string> _folderIdCache = new(StringComparer.Ordinal);

  public void Connect() {
  }

  public bool Probe() {
    try {
      var request = service.Files.List();
      request.PageSize = 1;
      request.Q = $"'{rootId}' in parents and trashed = false";
      request.Execute();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  private static string _Escape(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");

  private GoogleDriveFile? _FindChild(string parentId, string name, bool? foldersOnly = null) {
    var request = service.Files.List();
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

  private string? _ResolveFolderId(string physicalFolder) {
    var combined = rootPath.Length == 0 ? physicalFolder : $"{rootPath}/{physicalFolder}".Trim('/');
    if (combined.Length == 0)
      return rootId;

    if (this._folderIdCache.TryGetValue(combined, out var cached))
      return cached;

    var currentId = rootId;
    foreach (var segment in combined.Split('/')) {
      var child = this._FindChild(currentId, segment, foldersOnly: true);
      if (child == null)
        return null;

      currentId = child.Id;
    }

    this._folderIdCache[combined] = currentId;
    return currentId;
  }

  private GoogleDriveFile? _ResolveFile(string physicalPath) {
    var parentId = this._ResolveFolderId(PoolPaths.GetParent(physicalPath));
    return parentId == null ? null : this._FindChild(parentId, PoolPaths.GetName(physicalPath), foldersOnly: false);
  }

  public byte[] Download(string physicalPath) {
    var file = this._ResolveFile(physicalPath)
               ?? throw new PoolFsException(PoolFsError.NotFound, $"File not found: {physicalPath}");

    using var buffer = new MemoryStream();
    service.Files.Get(file.Id).Download(buffer);
    return buffer.ToArray();
  }

  public void Upload(string physicalPath, byte[] content) {
    var existing = this._ResolveFile(physicalPath);
    using var stream = new MemoryStream(content);
    if (existing != null) {
      service.Files.Update(new(), existing.Id, stream, "application/octet-stream").Upload();
      return;
    }

    var parentId = this._ResolveFolderId(PoolPaths.GetParent(physicalPath))
                   ?? throw new PoolFsException(PoolFsError.NotFound, $"Parent folder missing for: {physicalPath}");

    var create = service.Files.Create(
      new() { Name = PoolPaths.GetName(physicalPath), Parents = [parentId] },
      stream,
      "application/octet-stream");
    create.Fields = "id";
    create.Upload();
  }

  public void DeleteFile(string physicalPath) {
    var file = this._ResolveFile(physicalPath);
    if (file != null)
      service.Files.Delete(file.Id).Execute();
  }

  public StoreMeta? Stat(string physicalPath) {
    var parentId = this._ResolveFolderId(PoolPaths.GetParent(physicalPath));
    if (parentId == null)
      return null;

    var child = this._FindChild(parentId, PoolPaths.GetName(physicalPath));
    if (child == null)
      return null;

    var isFolder = child.MimeType == _FOLDER_MIME;
    var modified = child.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue;
    return new(isFolder, child.Size ?? 0, child.CreatedTimeDateTimeOffset?.UtcDateTime ?? modified, modified);
  }

  public void CreateFolder(string physicalPath) {
    var parentId = this._ResolveFolderId(PoolPaths.GetParent(physicalPath))
                   ?? throw new PoolFsException(PoolFsError.NotFound, $"Parent folder missing for: {physicalPath}");

    var request = service.Files.Create(new() {
      Name = PoolPaths.GetName(physicalPath),
      MimeType = _FOLDER_MIME,
      Parents = [parentId],
    });
    request.Fields = "id";
    request.Execute();
  }

  public void DeleteFolder(string physicalPath) {
    var folderId = this._ResolveFolderId(rootPath.Length == 0 ? physicalPath : physicalPath);
    if (folderId != null && folderId != rootId) {
      service.Files.Delete(folderId).Execute();
      this._folderIdCache.Clear(); // ids under the deleted folder are gone
    }
  }

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    var folderId = this._ResolveFolderId(physicalFolder)
                   ?? throw new PoolFsException(PoolFsError.NotFound, $"Folder not found: {physicalFolder}");

    string? pageToken = null;
    do {
      var request = service.Files.List();
      request.Q = $"'{folderId}' in parents and trashed = false";
      request.Fields = "nextPageToken, files(id, name, mimeType, size, modifiedTime)";
      request.PageToken = pageToken;
      var response = request.Execute();

      foreach (var file in response.Files)
        yield return new(file.Name, file.MimeType == _FOLDER_MIME, file.Size ?? 0, file.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue);

      pageToken = response.NextPageToken;
    } while (pageToken != null);
  }

  public void Dispose() => service.Dispose();

}
