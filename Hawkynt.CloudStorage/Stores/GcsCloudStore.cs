using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Hawkynt.CloudStorage;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>Google Cloud Storage store over Google.Cloud.Storage.V1.</summary>
public sealed class GcsCloudStore : ICloudStore {

  private readonly StorageClient _client;
  private readonly string _bucket;
  private readonly string _rootPrefix;

  /// <summary>Google Cloud Storage bucket authenticated with a service-account JSON key.</summary>
  public GcsCloudStore(string serviceAccountJson, string bucket, string rootPrefix) {
    this._client = StorageClient.Create(GoogleCredential.FromJson(serviceAccountJson));
    this._bucket = bucket;
    this._rootPrefix = rootPrefix;
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      this._client.GetBucket(this._bucket);
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    try {
      using var buffer = new MemoryStream();
      this._client.DownloadObject(this._bucket, ObjectKeys.File(this._rootPrefix, physicalPath), buffer);
      return buffer.ToArray();
    } catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) {
      throw new CloudStorageException(CloudStorageError.NotFound, $"Object not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content)
    => this._client.UploadObject(this._bucket, ObjectKeys.File(this._rootPrefix, physicalPath), null, new MemoryStream(content));

  public void DeleteFile(string physicalPath) {
    try {
      this._client.DeleteObject(this._bucket, ObjectKeys.File(this._rootPrefix, physicalPath));
    } catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) {
      // idempotent delete
    }
  }

  public CloudMeta? Stat(string physicalPath) {
    var key = ObjectKeys.File(this._rootPrefix, physicalPath);
    try {
      var storageObject = this._client.GetObject(this._bucket, key);
      var updated = storageObject.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.MinValue;
      return new(false, (long)(storageObject.Size ?? 0), storageObject.TimeCreatedDateTimeOffset?.UtcDateTime ?? updated, updated);
    } catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) {
      var children = this._client.ListObjects(this._bucket, key + "/");
      return children.Any() ? new(true, 0, DateTime.MinValue, DateTime.MinValue) : null;
    }
  }

  public void CreateFolder(string physicalPath)
    => this._client.UploadObject(this._bucket, ObjectKeys.FolderMarker(this._rootPrefix, physicalPath), null, new MemoryStream());

  public void DeleteFolder(string physicalPath) {
    try {
      this._client.DeleteObject(this._bucket, ObjectKeys.FolderMarker(this._rootPrefix, physicalPath));
    } catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) {
      // idempotent delete
    }
  }

  public IEnumerable<CloudEntry> List(string physicalFolder) {
    var prefix = ObjectKeys.ListPrefix(this._rootPrefix, physicalFolder);
    foreach (var page in this._client.ListObjects(this._bucket, prefix, new() { Delimiter = "/" }).AsRawResponses()) {
      foreach (var storageObject in page.Items ?? Enumerable.Empty<Google.Apis.Storage.v1.Data.Object>())
        if (storageObject.Name.Length > prefix.Length)
          yield return new(ObjectKeys.NameOf(storageObject.Name), false, (long)(storageObject.Size ?? 0), storageObject.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.MinValue);

      foreach (var commonPrefix in page.Prefixes ?? Enumerable.Empty<string>())
        yield return new(ObjectKeys.NameOf(commonPrefix), true, 0, DateTime.MinValue);
    }
  }

  public void Dispose() => this._client.Dispose();

}
