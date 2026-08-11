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
      SyncBridge.Run(() => this._client.Files.ListFolderAsync(this._Map(""), limit: 1));
      return true;
    } catch (Exception) {
      return false;
    }
  }

  /// <summary>
  /// The Dropbox SDK exposes downloads and uploads as streams, but its download argument carries
  /// no byte range — a ranged read would mean bypassing the SDK for raw HTTP against the content
  /// endpoint, so RangeRead is deliberately NOT claimed and the interface's whole-object fallback
  /// stands. Callers use the capability to keep a Dropbox member on whole-file strategies.
  /// </summary>
  public CloudCaps Caps => CloudCaps.StreamingUpload | CloudCaps.StreamingDownload;

  public byte[] Download(string path) {
    using var stream = this.OpenRead(path);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  public Stream OpenRead(string path) {
    try {
      var response = SyncBridge.Run(() => this._client.Files.DownloadAsync(this._Map(path)));
      // the response owns the HTTP connection; it must outlive the stream handed back
      return new OwnedStream(SyncBridge.Run(() => response.GetContentAsStreamAsync()), response, response.Response.Size >= 0 ? (long)response.Response.Size : -1);
    } catch (ApiException<DownloadError> e) when (e.ErrorResponse.IsPath) {
      throw new CloudStorageException(CloudStorageError.NotFound, $"File not found: {path}", e);
    }
  }

  public void Upload(string path, byte[] content) {
    using var buffer = new MemoryStream(content, false);
    this.Upload(path, buffer, content.LongLength);
  }

  /// <summary>Streams the body up — a large file is never held in memory as a byte[].</summary>
  public void Upload(string path, Stream content, long length = -1)
    => SyncBridge.Run(() => this._client.Files.UploadAsync(this._Map(path), WriteMode.Overwrite.Instance, body: content));

  public void DeleteFile(string path)
    => SyncBridge.Run(() => this._client.Files.DeleteV2Async(this._Map(path)));

  public CloudMeta? Stat(string path) {
    try {
      var metadata = SyncBridge.Run(() => this._client.Files.GetMetadataAsync(this._Map(path)));
      if (metadata.IsFolder)
        return new(true, 0, DateTime.MinValue, DateTime.MinValue);

      var file = metadata.AsFile;
      return new(false, (long)file.Size, file.ClientModified.ToUniversalTime(), file.ServerModified.ToUniversalTime());
    } catch (ApiException<GetMetadataError> e) when (e.ErrorResponse.IsPath) {
      return null;
    }
  }

  public void CreateFolder(string path)
    => SyncBridge.Run(() => this._client.Files.CreateFolderV2Async(this._Map(path)));

  public void DeleteFolder(string path)
    => SyncBridge.Run(() => this._client.Files.DeleteV2Async(this._Map(path)));

  public IEnumerable<CloudEntry> List(string folder) {
    var result = SyncBridge.Run(() => this._client.Files.ListFolderAsync(this._Map(folder)));
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

      result = SyncBridge.Run(() => this._client.Files.ListFolderContinueAsync(result.Cursor));
    }
  }

  public void Dispose() => this._client.Dispose();

}
