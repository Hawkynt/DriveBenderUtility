using System.Text;
using Hawkynt.CloudStorage;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>SFTP store over SSH.NET; paths are rooted at the URI path.</summary>
public sealed class SftpCloudStore : ICloudStore {

  private readonly SftpClient _client;
  private readonly string _rootPath;

  /// <summary>
  /// SFTP endpoint authenticated by password or, when <paramref name="privateKeyPem"/> is
  /// provided, by a PEM private key (decrypted with <paramref name="passphrase"/> when set).
  /// </summary>
  public SftpCloudStore(string host, int port, string user, string? password, string? privateKeyPem, string? passphrase, string rootPath) {
    SftpClient client;
    if (!string.IsNullOrEmpty(privateKeyPem)) {
      using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyPem));
      var keyFile = !string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(keyStream, passphrase) : new PrivateKeyFile(keyStream);
      client = new(new ConnectionInfo(host, port, user, new PrivateKeyAuthenticationMethod(user, keyFile)));
    } else
      client = new(host, port, user, password ?? "");

    this._client = client;
    this._rootPath = rootPath;
  }

  private string _Map(string physicalPath) => this._rootPath.Length == 0 ? "/" + physicalPath : $"/{this._rootPath}/{physicalPath}".TrimEnd('/');

  public void Connect() {
    if (!this._client.IsConnected)
      this._client.Connect();
  }

  public bool Probe() {
    try {
      this.Connect();
      return this._client.Exists(this._rootPath.Length == 0 ? "/" : "/" + this._rootPath);
    } catch (Exception) {
      return false;
    }
  }

  /// <summary>
  /// SFTP is a random-access protocol: a remote file opens as a seekable stream, so a range read
  /// is a seek and a bounded read rather than a transfer of everything before it.
  /// </summary>
  public CloudCaps Caps => CloudCaps.RangeRead | CloudCaps.StreamingUpload | CloudCaps.StreamingDownload;

  public byte[] Download(string physicalPath) {
    using var buffer = new MemoryStream();
    this._client.DownloadFile(this._Map(physicalPath), buffer);
    return buffer.ToArray();
  }

  public Stream OpenRead(string physicalPath) {
    this.Connect();
    return this._client.OpenRead(this._Map(physicalPath));
  }

  public Stream OpenReadRange(string physicalPath, long offset, long count) {
    if (count <= 0)
      return EmptyStream.Instance;

    this.Connect();
    var stream = this._client.OpenRead(this._Map(physicalPath));
    if (offset >= stream.Length) {
      stream.Dispose();
      return EmptyStream.Instance; // at or past the end — nothing to read, as on a local file
    }

    stream.Seek(offset, SeekOrigin.Begin);
    return new WindowStream(stream, 0, Math.Min(count, stream.Length - offset));
  }

  public void Upload(string physicalPath, byte[] content) {
    using var stream = new MemoryStream(content);
    this._client.UploadFile(stream, this._Map(physicalPath), canOverride: true);
  }

  /// <summary>Streams the body up — a multi-gigabyte file is never held in memory as a byte[].</summary>
  public void Upload(string physicalPath, Stream content, long length = -1) {
    this.Connect();
    this._client.UploadFile(content, this._Map(physicalPath), canOverride: true);
  }

  public void DeleteFile(string physicalPath) => this._client.DeleteFile(this._Map(physicalPath));

  public CloudMeta? Stat(string physicalPath) {
    try {
      var item = this._client.Get(this._Map(physicalPath));
      return new(item.IsDirectory, item.IsDirectory ? 0 : item.Length, item.LastWriteTimeUtc, item.LastWriteTimeUtc);
    } catch (SftpPathNotFoundException) {
      return null;
    }
  }

  public void CreateFolder(string physicalPath) => this._client.CreateDirectory(this._Map(physicalPath));

  public void DeleteFolder(string physicalPath) => this._client.DeleteDirectory(this._Map(physicalPath));

  public IEnumerable<CloudEntry> List(string physicalFolder) {
    foreach (var item in this._client.ListDirectory(this._Map(physicalFolder))) {
      if (item.Name is "." or "..")
        continue;

      yield return new(item.Name, item.IsDirectory, item.IsDirectory ? 0 : item.Length, item.LastWriteTimeUtc);
    }
  }

  public void Dispose() => this._client.Dispose();

}
