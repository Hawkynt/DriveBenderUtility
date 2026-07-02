using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DivisonM.Vfs;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace DivisonM.Backends;

/// <summary>
/// Shared key mapping for flat object stores: folders are virtual prefixes plus a
/// zero-byte "path/" marker object so empty folders exist and Stat can distinguish them.
/// </summary>
internal static class ObjectKeys {

  public static string File(string rootPrefix, string physicalPath)
    => rootPrefix.Length == 0 ? physicalPath : $"{rootPrefix}/{physicalPath}";

  public static string FolderMarker(string rootPrefix, string physicalFolder)
    => File(rootPrefix, physicalFolder) + "/";

  public static string ListPrefix(string rootPrefix, string physicalFolder) {
    var baseKey = physicalFolder.Length == 0 ? rootPrefix : File(rootPrefix, physicalFolder);
    return baseKey.Length == 0 ? "" : baseKey + "/";
  }

  public static string NameOf(string key) {
    var trimmed = key.TrimEnd('/');
    return trimmed[(trimmed.LastIndexOf('/') + 1)..];
  }

}

/// <summary>Amazon S3 (and S3-compatible) store over AWSSDK.S3.</summary>
public sealed class S3Store(IAmazonS3 client, string bucket, string rootPrefix) : IWholeFileStore {

  public void Connect() {
  }

  public bool Probe() {
    try {
      client.ListObjectsV2Async(new() { BucketName = bucket, MaxKeys = 1 }).GetAwaiter().GetResult();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    try {
      using var response = client.GetObjectAsync(bucket, ObjectKeys.File(rootPrefix, physicalPath)).GetAwaiter().GetResult();
      using var buffer = new MemoryStream();
      response.ResponseStream.CopyTo(buffer);
      return buffer.ToArray();
    } catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) {
      throw new PoolFsException(PoolFsError.NotFound, $"Object not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content)
    => client.PutObjectAsync(new() {
      BucketName = bucket,
      Key = ObjectKeys.File(rootPrefix, physicalPath),
      InputStream = new MemoryStream(content),
    }).GetAwaiter().GetResult();

  public void DeleteFile(string physicalPath)
    => client.DeleteObjectAsync(bucket, ObjectKeys.File(rootPrefix, physicalPath)).GetAwaiter().GetResult();

  public StoreMeta? Stat(string physicalPath) {
    var key = ObjectKeys.File(rootPrefix, physicalPath);
    try {
      var meta = client.GetObjectMetadataAsync(bucket, key).GetAwaiter().GetResult();
      return new(false, meta.ContentLength, meta.LastModified?.ToUniversalTime() ?? DateTime.MinValue, meta.LastModified?.ToUniversalTime() ?? DateTime.MinValue);
    } catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) {
      // no file object: a folder exists when its marker or any child does
      var listing = client.ListObjectsV2Async(new() { BucketName = bucket, Prefix = key + "/", MaxKeys = 1 }).GetAwaiter().GetResult();
      return listing.KeyCount > 0 ? new(true, 0, DateTime.MinValue, DateTime.MinValue) : null;
    }
  }

  public void CreateFolder(string physicalPath)
    => client.PutObjectAsync(new() {
      BucketName = bucket,
      Key = ObjectKeys.FolderMarker(rootPrefix, physicalPath),
      InputStream = new MemoryStream(),
    }).GetAwaiter().GetResult();

  public void DeleteFolder(string physicalPath)
    => client.DeleteObjectAsync(bucket, ObjectKeys.FolderMarker(rootPrefix, physicalPath)).GetAwaiter().GetResult();

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    var prefix = ObjectKeys.ListPrefix(rootPrefix, physicalFolder);
    var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, Delimiter = "/" };
    do {
      var response = client.ListObjectsV2Async(request).GetAwaiter().GetResult();
      foreach (var s3Object in response.S3Objects ?? [])
        if (s3Object.Key.Length > prefix.Length) // skip the folder marker itself
          yield return new(ObjectKeys.NameOf(s3Object.Key), false, s3Object.Size ?? 0, s3Object.LastModified?.ToUniversalTime() ?? DateTime.MinValue);

      foreach (var commonPrefix in response.CommonPrefixes ?? [])
        yield return new(ObjectKeys.NameOf(commonPrefix), true, 0, DateTime.MinValue);

      request.ContinuationToken = response.NextContinuationToken;
    } while (request.ContinuationToken != null);
  }

  public void Dispose() => client.Dispose();

}

/// <summary>Azure Blob Storage store over Azure.Storage.Blobs.</summary>
public sealed class AzureBlobStore(BlobContainerClient container, string rootPrefix) : IWholeFileStore {

  public void Connect() {
  }

  public bool Probe() {
    try {
      return container.Exists().Value;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    try {
      return container.GetBlobClient(ObjectKeys.File(rootPrefix, physicalPath)).DownloadContent().Value.Content.ToArray();
    } catch (RequestFailedException e) when (e.Status == 404) {
      throw new PoolFsException(PoolFsError.NotFound, $"Blob not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content)
    => container.GetBlobClient(ObjectKeys.File(rootPrefix, physicalPath)).Upload(new BinaryData(content), overwrite: true);

  public void DeleteFile(string physicalPath)
    => container.GetBlobClient(ObjectKeys.File(rootPrefix, physicalPath)).DeleteIfExists();

  public StoreMeta? Stat(string physicalPath) {
    var key = ObjectKeys.File(rootPrefix, physicalPath);
    var blob = container.GetBlobClient(key);
    try {
      var properties = blob.GetProperties().Value;
      return new(false, properties.ContentLength, properties.CreatedOn.UtcDateTime, properties.LastModified.UtcDateTime);
    } catch (RequestFailedException e) when (e.Status == 404) {
      var children = container.GetBlobs(BlobTraits.None, BlobStates.None, key + "/", CancellationToken.None).AsPages(pageSizeHint: 1).FirstOrDefault();
      return children != null && children.Values.Count > 0 ? new(true, 0, DateTime.MinValue, DateTime.MinValue) : null;
    }
  }

  public void CreateFolder(string physicalPath)
    => container.GetBlobClient(ObjectKeys.FolderMarker(rootPrefix, physicalPath)).Upload(new BinaryData(Array.Empty<byte>()), overwrite: true);

  public void DeleteFolder(string physicalPath)
    => container.GetBlobClient(ObjectKeys.FolderMarker(rootPrefix, physicalPath)).DeleteIfExists();

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    var prefix = ObjectKeys.ListPrefix(rootPrefix, physicalFolder);
    foreach (var item in container.GetBlobsByHierarchy(BlobTraits.None, BlobStates.None, "/", prefix)) {
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

/// <summary>Google Cloud Storage store over Google.Cloud.Storage.V1.</summary>
public sealed class GcsStore(StorageClient client, string bucket, string rootPrefix) : IWholeFileStore {

  public void Connect() {
  }

  public bool Probe() {
    try {
      client.GetBucket(bucket);
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    try {
      using var buffer = new MemoryStream();
      client.DownloadObject(bucket, ObjectKeys.File(rootPrefix, physicalPath), buffer);
      return buffer.ToArray();
    } catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) {
      throw new PoolFsException(PoolFsError.NotFound, $"Object not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content)
    => client.UploadObject(bucket, ObjectKeys.File(rootPrefix, physicalPath), null, new MemoryStream(content));

  public void DeleteFile(string physicalPath) {
    try {
      client.DeleteObject(bucket, ObjectKeys.File(rootPrefix, physicalPath));
    } catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) {
      // idempotent delete
    }
  }

  public StoreMeta? Stat(string physicalPath) {
    var key = ObjectKeys.File(rootPrefix, physicalPath);
    try {
      var storageObject = client.GetObject(bucket, key);
      var updated = storageObject.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.MinValue;
      return new(false, (long)(storageObject.Size ?? 0), storageObject.TimeCreatedDateTimeOffset?.UtcDateTime ?? updated, updated);
    } catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) {
      var children = client.ListObjects(bucket, key + "/");
      return children.Any() ? new(true, 0, DateTime.MinValue, DateTime.MinValue) : null;
    }
  }

  public void CreateFolder(string physicalPath)
    => client.UploadObject(bucket, ObjectKeys.FolderMarker(rootPrefix, physicalPath), null, new MemoryStream());

  public void DeleteFolder(string physicalPath) {
    try {
      client.DeleteObject(bucket, ObjectKeys.FolderMarker(rootPrefix, physicalPath));
    } catch (GoogleApiException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound) {
      // idempotent delete
    }
  }

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    var prefix = ObjectKeys.ListPrefix(rootPrefix, physicalFolder);
    foreach (var page in client.ListObjects(bucket, prefix, new() { Delimiter = "/" }).AsRawResponses()) {
      foreach (var storageObject in page.Items ?? Enumerable.Empty<Google.Apis.Storage.v1.Data.Object>())
        if (storageObject.Name.Length > prefix.Length)
          yield return new(ObjectKeys.NameOf(storageObject.Name), false, (long)(storageObject.Size ?? 0), storageObject.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.MinValue);

      foreach (var commonPrefix in page.Prefixes ?? Enumerable.Empty<string>())
        yield return new(ObjectKeys.NameOf(commonPrefix), true, 0, DateTime.MinValue);
    }
  }

  public void Dispose() => client.Dispose();

}
