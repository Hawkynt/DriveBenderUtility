using DivisonM.Vfs;
using Hawkynt.CloudStorage;
using Hawkynt.CloudStorage.OAuth;
using Hawkynt.CloudStorage.Stores;

namespace DivisonM.Backends;

file static class CloudCaps {
  public const BackendCaps WholeFile = BackendCaps.List | BackendCaps.Delete | BackendCaps.ServerCredentials;
}

/// <summary>
/// Amazon S3 (and S3-compatible) capacity backend: <c>s3://bucket/prefix</c>.
/// Secret JSON: { "accessKey", "secretKey", "region" } or { …, "serviceUrl" } for
/// MinIO/compatible endpoints.
/// </summary>
public sealed class AmazonS3VolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "s3";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    if (!CredentialPayload.TryGetJsonField(credential.Secret, "accessKey", out var accessKey)
        || !CredentialPayload.TryGetJsonField(credential.Secret, "secretKey", out var secretKey))
      throw new ManifestException($"Member '{member.DisplayName}': the s3 secret must be JSON with accessKey and secretKey");

    CredentialPayload.TryGetJsonField(credential.Secret, "region", out var region);
    CredentialPayload.TryGetJsonField(credential.Secret, "serviceUrl", out var serviceUrl);
    var store = new S3CloudStore(accessKey, secretKey, region.Length > 0 ? region : null, serviceUrl.Length > 0 ? serviceUrl : null, uri.Host, MemberUri.RootPath(uri));
    return CloudMember.Volume(member, uri, store);
  }

}

/// <summary>Azure Blob Storage backend: <c>azblob://container/prefix</c>; the secret is the storage connection string.</summary>
public sealed class AzureBlobVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "azblob";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var store = new AzureBlobCloudStore(CredentialPayload.Password(credential.Secret), uri.Host, MemberUri.RootPath(uri));
    return CloudMember.Volume(member, uri, store);
  }

}

/// <summary>Azure File Storage backend: <c>azfile://share/prefix</c>; the secret is the storage connection string.</summary>
public sealed class AzureFileVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "azfile";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var store = new AzureFileCloudStore(CredentialPayload.Password(credential.Secret), uri.Host, MemberUri.RootPath(uri));
    return CloudMember.Volume(member, uri, store);
  }

}

/// <summary>Google Cloud Storage backend: <c>gcs://bucket/prefix</c>; the secret is a service-account JSON key.</summary>
public sealed class GoogleCloudStorageVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "gcs";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var store = new GcsCloudStore(credential.Secret, uri.Host, MemberUri.RootPath(uri));
    return CloudMember.Volume(member, uri, store);
  }

}

/// <summary>
/// Dropbox backend: <c>dropbox://app/prefix</c>. The secret is JSON with your own registered
/// { "clientId", "clientSecret", "refreshToken" }; the access token is refreshed automatically.
/// </summary>
public sealed class DropboxVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "dropbox";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var tokens = CloudOAuth.TokenProvider(member, credentials, (id, secret) => CloudOAuthProviders.Dropbox(id, secret));
    return CloudMember.Volume(member, uri, new DropboxCloudStore(tokens, MemberUri.RootPath(uri)));
  }

}

/// <summary>
/// Microsoft OneDrive backend: <c>onedrive://driveId/prefix</c> (host = drive id, or empty/"me"
/// for the signed-in user's default drive; covers personal and business). OAuth secret JSON as
/// for Dropbox.
/// </summary>
public sealed class OneDriveVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "onedrive";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var tokens = CloudOAuth.TokenProvider(member, credentials, (id, secret) => CloudOAuthProviders.OneDrive(id, secret));
    return CloudMember.Volume(member, uri, new OneDriveCloudStore(tokens, uri.Host, MemberUri.RootPath(uri)));
  }

}

/// <summary>
/// Google Drive backend: <c>gdrive://root/prefix</c> (host = folder id or "root"). The secret is
/// either your own OAuth client JSON ({ "clientId", "clientSecret", "refreshToken" }) or a
/// service-account JSON key — detected by the presence of a clientId.
/// </summary>
public sealed class GoogleDriveVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "gdrive";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var secret = member.CredentialReference is { } reference ? credentials?.Resolve(reference)?.Secret : null;

    ICloudStore store;
    if (secret != null && CredentialPayload.TryGetJsonField(secret, "clientId", out _)) {
      var tokens = CloudOAuth.TokenProvider(member, credentials, (id, secret) => CloudOAuthProviders.GoogleDrive(id, secret));
      store = new GoogleDriveCloudStore(tokens, uri.Host, MemberUri.RootPath(uri));
    } else {
      var credential = MemberUri.ResolveCredential(member, credentials, uri);
      store = new GoogleDriveCloudStore(credential.Secret, uri.Host, MemberUri.RootPath(uri));
    }

    return CloudMember.Volume(member, uri, store);
  }

}

/// <summary>
/// Box backend: <c>box://root/prefix</c> (host = folder id or "0"). OAuth secret JSON with your
/// own { "clientId", "clientSecret", "refreshToken" }.
/// </summary>
public sealed class BoxVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "box";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var tokens = CloudOAuth.TokenProvider(member, credentials, (id, secret) => CloudOAuthProviders.Box(id, secret));
    return CloudMember.Volume(member, uri, new BoxCloudStore(tokens, uri.Host, MemberUri.RootPath(uri)));
  }

}

/// <summary>Yandex Disk backend: <c>yandex://disk/prefix</c>. OAuth secret JSON as for Box.</summary>
public sealed class YandexDiskVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "yandex";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var tokens = CloudOAuth.TokenProvider(member, credentials, (id, secret) => CloudOAuthProviders.YandexDisk(id, secret));
    return CloudMember.Volume(member, uri, new YandexDiskCloudStore(tokens, MemberUri.RootPath(uri)));
  }

}

/// <summary>Strato HiDrive backend: <c>hidrive://users-alias/prefix</c>. OAuth secret JSON as for Box.</summary>
public sealed class HiDriveVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "hidrive";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var root = MemberUri.RootPath(uri);
    var basePath = uri.Host.Length == 0 ? root : CloudPath.Combine(uri.Host, root);
    var tokens = CloudOAuth.TokenProvider(member, credentials, (id, secret) => CloudOAuthProviders.HiDrive(id, secret));
    return CloudMember.Volume(member, uri, new HiDriveCloudStore(tokens, basePath));
  }

}

file static class CloudMember {
  public static IVolumeIO Volume(MemberDescriptor member, Uri uri, ICloudStore store)
    => new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), new CloudStoreAdapter(store));
}
