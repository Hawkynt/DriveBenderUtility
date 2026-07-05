using Dropbox.Api;
using Dropbox.Api.Files;
using Hawkynt.CloudStorage.OAuth;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// Dropbox store over Dropbox.Api; Dropbox paths are "/rooted". The bearer token is stamped
/// per request from the <see cref="IAccessTokenProvider"/>, so a refreshed access token is
/// picked up without rebuilding the client.
/// </summary>
public sealed class DropboxCloudStore : ICloudStore {

  private readonly DropboxClient _client;
  private readonly string _rootPath;

  public DropboxCloudStore(IAccessTokenProvider tokens, string rootPath) {
    this._rootPath = rootPath.Trim('/');
    var http = new HttpClient(new BearerInjectingHandler(tokens));
    this._client = new(tokens.GetAccessToken(), new DropboxClientConfig("Hawkynt.CloudStorage") { HttpClient = http });
  }

  private string _Map(string path) {
    var combined = this._rootPath.Length == 0 ? path : $"{this._rootPath}/{path}".Trim('/');
    return combined.Length == 0 ? "" : "/" + combined;
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      this._client.Files.ListFolderAsync(this._Map(""), limit: 1).GetAwaiter().GetResult();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string path) {
    try {
      using var response = this._client.Files.DownloadAsync(this._Map(path)).GetAwaiter().GetResult();
      return response.GetContentAsByteArrayAsync().GetAwaiter().GetResult();
    } catch (ApiException<DownloadError> e) when (e.ErrorResponse.IsPath) {
      throw new CloudStorageException(CloudStorageError.NotFound, $"File not found: {path}", e);
    }
  }

  public void Upload(string path, byte[] content)
    => this._client.Files.UploadAsync(this._Map(path), WriteMode.Overwrite.Instance, body: new MemoryStream(content)).GetAwaiter().GetResult();

  public void DeleteFile(string path)
    => this._client.Files.DeleteV2Async(this._Map(path)).GetAwaiter().GetResult();

  public CloudMeta? Stat(string path) {
    try {
      var metadata = this._client.Files.GetMetadataAsync(this._Map(path)).GetAwaiter().GetResult();
      if (metadata.IsFolder)
        return new(true, 0, DateTime.MinValue, DateTime.MinValue);

      var file = metadata.AsFile;
      return new(false, (long)file.Size, file.ClientModified.ToUniversalTime(), file.ServerModified.ToUniversalTime());
    } catch (ApiException<GetMetadataError> e) when (e.ErrorResponse.IsPath) {
      return null;
    }
  }

  public void CreateFolder(string path)
    => this._client.Files.CreateFolderV2Async(this._Map(path)).GetAwaiter().GetResult();

  public void DeleteFolder(string path)
    => this._client.Files.DeleteV2Async(this._Map(path)).GetAwaiter().GetResult();

  public IEnumerable<CloudEntry> List(string folder) {
    var result = this._client.Files.ListFolderAsync(this._Map(folder)).GetAwaiter().GetResult();
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

      result = this._client.Files.ListFolderContinueAsync(result.Cursor).GetAwaiter().GetResult();
    }
  }

  public void Dispose() => this._client.Dispose();

}
