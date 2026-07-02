using System.Text;
using DivisonM.Vfs;
using FluentFTP;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DivisonM.Backends;

/// <summary>FTP store over FluentFTP; paths are rooted at the URI path.</summary>
public sealed class FtpStore(FtpClient client, string rootPath) : IWholeFileStore {

  private string _Map(string physicalPath) => rootPath.Length == 0 ? "/" + physicalPath : $"/{rootPath}/{physicalPath}".TrimEnd('/');

  public void Connect() {
    if (!client.IsConnected)
      client.Connect();
  }

  public bool Probe() => client.IsConnected || this._TryConnect();

  private bool _TryConnect() {
    try {
      client.Connect();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    if (!client.DownloadBytes(out var bytes, this._Map(physicalPath)))
      throw new PoolFsException(PoolFsError.NotFound, $"FTP download failed: {physicalPath}");

    return bytes;
  }

  public void Upload(string physicalPath, byte[] content) {
    var status = client.UploadBytes(content, this._Map(physicalPath), FtpRemoteExists.Overwrite, createRemoteDir: true);
    if (status == FtpStatus.Failed)
      throw new PoolFsException(PoolFsError.IoError, $"FTP upload failed: {physicalPath}");
  }

  public void DeleteFile(string physicalPath) => client.DeleteFile(this._Map(physicalPath));

  public StoreMeta? Stat(string physicalPath) {
    var remote = this._Map(physicalPath);
    var info = client.GetObjectInfo(remote);
    if (info == null)
      return null;

    return new(info.Type == FtpObjectType.Directory, info.Size < 0 ? 0 : info.Size, info.RawCreated.ToUniversalTime(), info.Modified.ToUniversalTime());
  }

  public void CreateFolder(string physicalPath) => client.CreateDirectory(this._Map(physicalPath));

  public void DeleteFolder(string physicalPath) => client.DeleteDirectory(this._Map(physicalPath));

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    foreach (var item in client.GetListing(this._Map(physicalFolder))) {
      if (item.Name is "." or "..")
        continue;

      yield return new(item.Name, item.Type == FtpObjectType.Directory, item.Size < 0 ? 0 : item.Size, item.Modified.ToUniversalTime());
    }
  }

  public void Dispose() => client.Dispose();

}

/// <summary>FTP / FTPS capacity backend (ftp://user@host:21/root); whole-file, no durable flush (§6.1 table).</summary>
public sealed class FtpVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "ftp";
  public BackendCaps Caps => BackendCaps.List | BackendCaps.Delete | BackendCaps.ServerCredentials;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);

    var client = new FtpClient(uri.Host, credential.UserName, CredentialPayload.Password(credential.Secret), uri.IsDefaultPort ? 21 : uri.Port);
    if (uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase))
      client.Config.EncryptionMode = FtpEncryptionMode.Explicit;

    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), new FtpStore(client, MemberUri.RootPath(uri)));
  }

}

/// <summary>SFTP store over SSH.NET; paths are rooted at the URI path.</summary>
public sealed class SftpStore(SftpClient client, string rootPath) : IWholeFileStore {

  private string _Map(string physicalPath) => rootPath.Length == 0 ? "/" + physicalPath : $"/{rootPath}/{physicalPath}".TrimEnd('/');

  public void Connect() {
    if (!client.IsConnected)
      client.Connect();
  }

  public bool Probe() {
    try {
      this.Connect();
      return client.Exists(rootPath.Length == 0 ? "/" : "/" + rootPath);
    } catch (Exception) {
      return false;
    }
  }

  public byte[] Download(string physicalPath) {
    using var buffer = new MemoryStream();
    client.DownloadFile(this._Map(physicalPath), buffer);
    return buffer.ToArray();
  }

  public void Upload(string physicalPath, byte[] content) {
    using var stream = new MemoryStream(content);
    client.UploadFile(stream, this._Map(physicalPath), canOverride: true);
  }

  public void DeleteFile(string physicalPath) => client.DeleteFile(this._Map(physicalPath));

  public StoreMeta? Stat(string physicalPath) {
    try {
      var item = client.Get(this._Map(physicalPath));
      return new(item.IsDirectory, item.IsDirectory ? 0 : item.Length, item.LastWriteTimeUtc, item.LastWriteTimeUtc);
    } catch (SftpPathNotFoundException) {
      return null;
    }
  }

  public void CreateFolder(string physicalPath) => client.CreateDirectory(this._Map(physicalPath));

  public void DeleteFolder(string physicalPath) => client.DeleteDirectory(this._Map(physicalPath));

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    foreach (var item in client.ListDirectory(this._Map(physicalFolder))) {
      if (item.Name is "." or "..")
        continue;

      yield return new(item.Name, item.IsDirectory, item.IsDirectory ? 0 : item.Length, item.LastWriteTimeUtc);
    }
  }

  public void Dispose() => client.Dispose();

}

/// <summary>
/// SFTP capacity backend (sftp://user@host:22/root). The secret is a plain password or a
/// JSON payload with "privateKey" (PEM) and optional "passphrase" fields.
/// </summary>
public sealed class SftpVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "sftp";
  public BackendCaps Caps => BackendCaps.List | BackendCaps.Delete | BackendCaps.ServerCredentials;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var port = uri.IsDefaultPort ? 22 : uri.Port;

    SftpClient client;
    if (CredentialPayload.TryGetJsonField(credential.Secret, "privateKey", out var privateKeyPem)) {
      CredentialPayload.TryGetJsonField(credential.Secret, "passphrase", out var passphrase);
      using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyPem));
      var keyFile = passphrase.Length > 0 ? new PrivateKeyFile(keyStream, passphrase) : new PrivateKeyFile(keyStream);
      client = new(new ConnectionInfo(uri.Host, port, credential.UserName, new PrivateKeyAuthenticationMethod(credential.UserName, keyFile)));
    } else
      client = new(uri.Host, port, credential.UserName, CredentialPayload.Password(credential.Secret));

    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), new SftpStore(client, MemberUri.RootPath(uri)));
  }

}
