using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Hawkynt.CloudStorage;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>Azure Blob Storage store over Azure.Storage.Blobs.</summary>
public sealed class AzureBlobCloudStore : ICloudStore {

  private readonly BlobContainerClient _container;
  private readonly string _rootPrefix;

  /// <summary>Azure Blob container addressed by a connection string.</summary>
  public AzureBlobCloudStore(string connectionString, string container, string rootPrefix) {
    this._container = new BlobContainerClient(connectionString, container);
    this._rootPrefix = rootPrefix;
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      return this._container.Exists().Value;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    try {
      return this._container.GetBlobClient(ObjectKeys.File(this._rootPrefix, physicalPath)).DownloadContent().Value.Content.ToArray();
    } catch (RequestFailedException e) when (e.Status == 404) {
      throw new CloudStorageException(CloudStorageError.NotFound, $"Blob not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content)
    => this._container.GetBlobClient(ObjectKeys.File(this._rootPrefix, physicalPath)).Upload(new BinaryData(content), overwrite: true);

  public void DeleteFile(string physicalPath)
    => this._container.GetBlobClient(ObjectKeys.File(this._rootPrefix, physicalPath)).DeleteIfExists();

  public CloudMeta? Stat(string physicalPath) {
    var key = ObjectKeys.File(this._rootPrefix, physicalPath);
    var blob = this._container.GetBlobClient(key);
    try {
      var properties = blob.GetProperties().Value;
      return new(false, properties.ContentLength, properties.CreatedOn.UtcDateTime, properties.LastModified.UtcDateTime);
    } catch (RequestFailedException e) when (e.Status == 404) {
      var children = this._container.GetBlobs(BlobTraits.None, BlobStates.None, key + "/", CancellationToken.None).AsPages(pageSizeHint: 1).FirstOrDefault();
      return children != null && children.Values.Count > 0 ? new(true, 0, DateTime.MinValue, DateTime.MinValue) : null;
    }
  }

  public void CreateFolder(string physicalPath)
    => this._container.GetBlobClient(ObjectKeys.FolderMarker(this._rootPrefix, physicalPath)).Upload(new BinaryData(Array.Empty<byte>()), overwrite: true);

  public void DeleteFolder(string physicalPath)
    => this._container.GetBlobClient(ObjectKeys.FolderMarker(this._rootPrefix, physicalPath)).DeleteIfExists();

  public IEnumerable<CloudEntry> List(string physicalFolder) {
    var prefix = ObjectKeys.ListPrefix(this._rootPrefix, physicalFolder);
    foreach (var item in this._container.GetBlobsByHierarchy(BlobTraits.None, BlobStates.None, "/", prefix)) {
      if (item.IsPrefix) {
        yield return new(ObjectKeys.NameOf(item.Prefix), true, 0, DateTime.MinValue);
        continue;
      }

      if (item.Blob.Name.Length > prefix.Length) // skip the folder marker itself
        yield return new(ObjectKeys.NameOf(item.Blob.Name), false, item.Blob.Properties.ContentLength ?? 0, item.Blob.Properties.LastModified?.UtcDateTime ?? DateTime.MinValue);
    }
  }

  public void Dispose() {
  }

}
