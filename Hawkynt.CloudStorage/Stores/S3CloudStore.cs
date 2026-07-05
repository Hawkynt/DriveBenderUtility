using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Hawkynt.CloudStorage;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>Amazon S3 (and S3-compatible) store over AWSSDK.S3.</summary>
public sealed class S3CloudStore : ICloudStore {

  private readonly IAmazonS3 _client;
  private readonly string _bucket;
  private readonly string _rootPrefix;

  /// <summary>
  /// Amazon S3 or an S3-compatible endpoint: <paramref name="serviceUrl"/> selects a custom
  /// endpoint (path-style addressing), otherwise <paramref name="region"/> picks an AWS region.
  /// </summary>
  public S3CloudStore(string accessKey, string secretKey, string? region, string? serviceUrl, string bucket, string rootPrefix) {
    var config = new AmazonS3Config();
    if (!string.IsNullOrEmpty(serviceUrl)) {
      config.ServiceURL = serviceUrl;
      config.ForcePathStyle = true;
    } else if (!string.IsNullOrEmpty(region))
      config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);

    this._client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    this._bucket = bucket;
    this._rootPrefix = rootPrefix;
  }

  public void Connect() {
  }

  public bool Probe() {
    try {
      this._client.ListObjectsV2Async(new() { BucketName = this._bucket, MaxKeys = 1 }).GetAwaiter().GetResult();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    try {
      using var response = this._client.GetObjectAsync(this._bucket, ObjectKeys.File(this._rootPrefix, physicalPath)).GetAwaiter().GetResult();
      using var buffer = new MemoryStream();
      response.ResponseStream.CopyTo(buffer);
      return buffer.ToArray();
    } catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) {
      throw new CloudStorageException(CloudStorageError.NotFound, $"Object not found: {physicalPath}", e);
    }
  }

  public void Upload(string physicalPath, byte[] content)
    => this._client.PutObjectAsync(new() {
      BucketName = this._bucket,
      Key = ObjectKeys.File(this._rootPrefix, physicalPath),
      InputStream = new MemoryStream(content),
    }).GetAwaiter().GetResult();

  public void DeleteFile(string physicalPath)
    => this._client.DeleteObjectAsync(this._bucket, ObjectKeys.File(this._rootPrefix, physicalPath)).GetAwaiter().GetResult();

  public CloudMeta? Stat(string physicalPath) {
    var key = ObjectKeys.File(this._rootPrefix, physicalPath);
    try {
      var meta = this._client.GetObjectMetadataAsync(this._bucket, key).GetAwaiter().GetResult();
      return new(false, meta.ContentLength, meta.LastModified?.ToUniversalTime() ?? DateTime.MinValue, meta.LastModified?.ToUniversalTime() ?? DateTime.MinValue);
    } catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) {
      // no file object: a folder exists when its marker or any child does
      var listing = this._client.ListObjectsV2Async(new() { BucketName = this._bucket, Prefix = key + "/", MaxKeys = 1 }).GetAwaiter().GetResult();
      return listing.KeyCount > 0 ? new(true, 0, DateTime.MinValue, DateTime.MinValue) : null;
    }
  }

  public void CreateFolder(string physicalPath)
    => this._client.PutObjectAsync(new() {
      BucketName = this._bucket,
      Key = ObjectKeys.FolderMarker(this._rootPrefix, physicalPath),
      InputStream = new MemoryStream(),
    }).GetAwaiter().GetResult();

  public void DeleteFolder(string physicalPath)
    => this._client.DeleteObjectAsync(this._bucket, ObjectKeys.FolderMarker(this._rootPrefix, physicalPath)).GetAwaiter().GetResult();

  public IEnumerable<CloudEntry> List(string physicalFolder) {
    var prefix = ObjectKeys.ListPrefix(this._rootPrefix, physicalFolder);
    var request = new ListObjectsV2Request { BucketName = this._bucket, Prefix = prefix, Delimiter = "/" };
    do {
      var response = this._client.ListObjectsV2Async(request).GetAwaiter().GetResult();
      foreach (var s3Object in response.S3Objects ?? [])
        if (s3Object.Key.Length > prefix.Length) // skip the folder marker itself
          yield return new(ObjectKeys.NameOf(s3Object.Key), false, s3Object.Size ?? 0, s3Object.LastModified?.ToUniversalTime() ?? DateTime.MinValue);

      foreach (var commonPrefix in response.CommonPrefixes ?? [])
        yield return new(ObjectKeys.NameOf(commonPrefix), true, 0, DateTime.MinValue);

      request.ContinuationToken = response.NextContinuationToken;
    } while (request.ContinuationToken != null);
  }

  public void Dispose() => this._client.Dispose();

}
