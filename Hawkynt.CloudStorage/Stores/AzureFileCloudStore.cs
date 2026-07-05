using Azure;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Hawkynt.CloudStorage;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>Azure File Storage store over Azure.Storage.Files.Shares — a real directory tree.</summary>
public sealed class AzureFileCloudStore : ICloudStore {

  private readonly ShareClient _share;
  private readonly string _rootPath;

  /// <summary>Azure Files share addressed by a connection string.</summary>
  public AzureFileCloudStore(string connectionString, string share, string rootPath) {
    this._share = new ShareClient(connectionString, share);
    this._rootPath = rootPath;
  }

  private string _Map(string physicalPath) => this._rootPath.Length == 0 ? physicalPath : $"{this._rootPath}/{physicalPath}".Trim('/');

  private ShareDirectoryClient _Directory(string physicalFolder) {
    var mapped = this._Map(physicalFolder);
    return mapped.Length == 0 ? this._share.GetRootDirectoryClient() : this._share.GetDirectoryClient(mapped);
  }

  private ShareFileClient _File(string physicalPath) {
    var mapped = this._Map(physicalPath);
    var slash = mapped.LastIndexOf('/');
    var directory = slash < 0 ? this._share.GetRootDirectoryClient() : this._share.GetDirectoryClient(mapped[..slash]);
    return directory.GetFileClient(mapped[(slash + 1)..]);
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      return this._share.Exists().Value;
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
      throw new CloudStorageException(CloudStorageError.NotFound, $"File not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content) {
    var file = this._File(physicalPath);
    file.Create(content.Length);
    if (content.Length > 0)
      file.UploadRange(new HttpRange(0, content.Length), new MemoryStream(content));
  }

  public void DeleteFile(string physicalPath) => this._File(physicalPath).DeleteIfExists();

  public CloudMeta? Stat(string physicalPath) {
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

  public IEnumerable<CloudEntry> List(string physicalFolder) {
    foreach (var item in this._Directory(physicalFolder).GetFilesAndDirectories(new ShareDirectoryGetFilesAndDirectoriesOptions { Traits = ShareFileTraits.Timestamps }))
      yield return new(item.Name, item.IsDirectory, item.FileSize ?? 0, item.Properties?.LastWrittenOn?.UtcDateTime ?? DateTime.MinValue);
  }

  public void Dispose() {
  }

}
