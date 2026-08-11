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

  public CloudCaps Caps => CloudCaps.RangeRead | CloudCaps.StreamingUpload | CloudCaps.StreamingDownload;

  public void Connect() {
  }

  public bool Probe() {
    try {
      SyncBridge.Run(() => this._client.ListObjectsV2Async(new() { BucketName = this._bucket, MaxKeys = 1 }));
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    using var stream = this.OpenRead(physicalPath);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  public Stream OpenRead(string physicalPath) => this._Get(new() {
    BucketName = this._bucket,
    Key = ObjectKeys.File(this._rootPrefix, physicalPath),
  }, physicalPath);

  /// <summary>
  /// A real HTTP range request (the S3 Range header): the object is never transferred beyond the
  /// bytes asked for, which is what turns a block-by-block read of a large object from quadratic
  /// into linear.
  /// </summary>
  public Stream OpenReadRange(string physicalPath, long offset, long count) {
    if (count <= 0)
      return new MemoryStream([], false);

    return this._Get(new() {
      BucketName = this._bucket,
      Key = ObjectKeys.File(this._rootPrefix, physicalPath),
      ByteRange = new(offset, offset + count - 1), // inclusive, per RFC 9110
    }, physicalPath, rangePastEndIsEmpty: true);
  }

  private Stream _Get(GetObjectRequest request, string physicalPath, bool rangePastEndIsEmpty = false) {
    try {
      // the response owns the network stream; handing it out transfers that ownership
      var response = SyncBridge.Run(() => this._client.GetObjectAsync(request));
      return new ResponseOwningStream(response);
    } catch (AmazonS3Exception e) when (rangePastEndIsEmpty && e.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) {
      return new MemoryStream([], false); // reading at or past EOF yields nothing, as on a local file
    } catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) {
      throw new CloudStorageException(CloudStorageError.NotFound, $"Object not found: {physicalPath}", e);
    }
  }

  /// <summary>Keeps the SDK response alive for exactly as long as the caller reads its stream.</summary>
  private sealed class ResponseOwningStream(GetObjectResponse response) : Stream {

    private readonly Stream _inner = response.ResponseStream;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => response.ContentLength;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => this._inner.Read(buffer);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
      if (disposing)
        response.Dispose(); // disposes the underlying network stream with it

      base.Dispose(disposing);
    }
  }

  public void Upload(string physicalPath, byte[] content) {
    using var buffer = new MemoryStream(content, false);
    this.Upload(physicalPath, buffer, content.LongLength);
  }

  /// <summary>Streams the body straight to S3 — a multi-gigabyte object is never held in memory.</summary>
  public void Upload(string physicalPath, Stream content, long length = -1)
    => SyncBridge.Run(() => this._client.PutObjectAsync(new() {
      BucketName = this._bucket,
      Key = ObjectKeys.File(this._rootPrefix, physicalPath),
      InputStream = content,
      AutoCloseStream = false, // the caller owns the stream it handed us
    }));

  public void DeleteFile(string physicalPath)
    => SyncBridge.Run(() => this._client.DeleteObjectAsync(this._bucket, ObjectKeys.File(this._rootPrefix, physicalPath)));

  public CloudMeta? Stat(string physicalPath) {
    var key = ObjectKeys.File(this._rootPrefix, physicalPath);
    try {
      var meta = SyncBridge.Run(() => this._client.GetObjectMetadataAsync(this._bucket, key));
      return new(false, meta.ContentLength, meta.LastModified?.ToUniversalTime() ?? DateTime.MinValue, meta.LastModified?.ToUniversalTime() ?? DateTime.MinValue);
    } catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) {
      // no file object: a folder exists when its marker or any child does
      var listing = SyncBridge.Run(() => this._client.ListObjectsV2Async(new() { BucketName = this._bucket, Prefix = key + "/", MaxKeys = 1 }));
      return listing.KeyCount > 0 ? new(true, 0, DateTime.MinValue, DateTime.MinValue) : null;
    }
  }

  public void CreateFolder(string physicalPath)
    => SyncBridge.Run(() => this._client.PutObjectAsync(new() {
      BucketName = this._bucket,
      Key = ObjectKeys.FolderMarker(this._rootPrefix, physicalPath),
      InputStream = new MemoryStream(),
    }));

  public void DeleteFolder(string physicalPath)
    => SyncBridge.Run(() => this._client.DeleteObjectAsync(this._bucket, ObjectKeys.FolderMarker(this._rootPrefix, physicalPath)));

  public IEnumerable<CloudEntry> List(string physicalFolder) {
    var prefix = ObjectKeys.ListPrefix(this._rootPrefix, physicalFolder);
    var request = new ListObjectsV2Request { BucketName = this._bucket, Prefix = prefix, Delimiter = "/" };
    do {
      var response = SyncBridge.Run(() => this._client.ListObjectsV2Async(request));
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
