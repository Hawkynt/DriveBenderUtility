using DivisonM.Vfs;
using Hawkynt.CloudStorage.Stores;

namespace DivisonM.Backends;

/// <summary>FTP / FTPS capacity backend (ftp://user@host:21/root); whole-file, no durable flush (§6.1 table).</summary>
public sealed class FtpVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "ftp";
  public BackendCaps Caps => BackendCaps.List | BackendCaps.Delete | BackendCaps.ServerCredentials;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var ftps = uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase);
    var store = new FtpCloudStore(uri.Host, uri.IsDefaultPort ? 21 : uri.Port, credential.UserName, CredentialPayload.Password(credential.Secret), ftps, MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), new CloudStoreAdapter(store));
  }

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

    var hasKey = CredentialPayload.TryGetJsonField(credential.Secret, "privateKey", out var privateKeyPem);
    CredentialPayload.TryGetJsonField(credential.Secret, "passphrase", out var passphrase);
    var store = new SftpCloudStore(
      uri.Host,
      port,
      credential.UserName,
      hasKey ? null : CredentialPayload.Password(credential.Secret),
      hasKey ? privateKeyPem : null,
      passphrase.Length > 0 ? passphrase : null,
      MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), new CloudStoreAdapter(store));
  }

}
