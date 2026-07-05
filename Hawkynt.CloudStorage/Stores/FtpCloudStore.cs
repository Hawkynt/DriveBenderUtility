using FluentFTP;
using Hawkynt.CloudStorage;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>FTP store over FluentFTP; paths are rooted at the URI path.</summary>
public sealed class FtpCloudStore : ICloudStore {

  private readonly FtpClient _client;
  private readonly string _rootPath;

  /// <summary>FTP / FTPS endpoint; when <paramref name="ftps"/> is set explicit encryption is negotiated.</summary>
  public FtpCloudStore(string host, int port, string user, string password, bool ftps, string rootPath) {
    var client = new FtpClient(host, user, password, port);
    if (ftps)
      client.Config.EncryptionMode = FtpEncryptionMode.Explicit;

    this._client = client;
    this._rootPath = rootPath;
  }

  private string _Map(string physicalPath) => this._rootPath.Length == 0 ? "/" + physicalPath : $"/{this._rootPath}/{physicalPath}".TrimEnd('/');

  public void Connect() {
    if (!this._client.IsConnected)
      this._client.Connect();
  }

  public bool Probe() => this._client.IsConnected || this._TryConnect();

  private bool _TryConnect() {
    try {
      this._client.Connect();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    if (!this._client.DownloadBytes(out var bytes, this._Map(physicalPath)))
      throw new CloudStorageException(CloudStorageError.NotFound, $"FTP download failed: {physicalPath}");

    return bytes;
  }

  public void Upload(string physicalPath, byte[] content) {
    var status = this._client.UploadBytes(content, this._Map(physicalPath), FtpRemoteExists.Overwrite, createRemoteDir: true);
    if (status == FtpStatus.Failed)
      throw new CloudStorageException(CloudStorageError.IoError, $"FTP upload failed: {physicalPath}");
  }

  public void DeleteFile(string physicalPath) => this._client.DeleteFile(this._Map(physicalPath));

  public CloudMeta? Stat(string physicalPath) {
    var remote = this._Map(physicalPath);
    var info = this._client.GetObjectInfo(remote);
    if (info == null)
      return null;

    return new(info.Type == FtpObjectType.Directory, info.Size < 0 ? 0 : info.Size, info.RawCreated.ToUniversalTime(), info.Modified.ToUniversalTime());
  }

  public void CreateFolder(string physicalPath) => this._client.CreateDirectory(this._Map(physicalPath));

  public void DeleteFolder(string physicalPath) => this._client.DeleteDirectory(this._Map(physicalPath));

  public IEnumerable<CloudEntry> List(string physicalFolder) {
    foreach (var item in this._client.GetListing(this._Map(physicalFolder))) {
      if (item.Name is "." or "..")
        continue;

      yield return new(item.Name, item.Type == FtpObjectType.Directory, item.Size < 0 ? 0 : item.Size, item.Modified.ToUniversalTime());
    }
  }

  public void Dispose() => this._client.Dispose();

}
