using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using DivisonM.Vfs;
using Dropbox.Api;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Cloud.Storage.V1;
using Microsoft.Graph;

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

    var config = new AmazonS3Config();
    if (CredentialPayload.TryGetJsonField(credential.Secret, "serviceUrl", out var serviceUrl)) {
      config.ServiceURL = serviceUrl;
      config.ForcePathStyle = true;
    } else if (CredentialPayload.TryGetJsonField(credential.Secret, "region", out var region))
      config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);

    var client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    var store = new S3Store(client, uri.Host, MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), store);
  }

}

/// <summary>Azure Blob Storage backend: <c>azblob://container/prefix</c>; the secret is the storage connection string.</summary>
public sealed class AzureBlobVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "azblob";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var container = new BlobContainerClient(CredentialPayload.Password(credential.Secret), uri.Host);
    var store = new AzureBlobStore(container, MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), store);
  }

}

/// <summary>Azure File Storage backend: <c>azfile://share/prefix</c>; the secret is the storage connection string.</summary>
public sealed class AzureFileVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "azfile";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var share = new ShareClient(CredentialPayload.Password(credential.Secret), uri.Host);
    var store = new AzureFileStore(share, MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), store);
  }

}

/// <summary>Dropbox backend: <c>dropbox://app/prefix</c>; the secret is an OAuth access token.</summary>
public sealed class DropboxVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "dropbox";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var client = new DropboxClient(CredentialPayload.Password(credential.Secret));
    var store = new DropboxStore(client, MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), store);
  }

}

/// <summary>Microsoft OneDrive backend: <c>onedrive://driveId/prefix</c>; the secret is an OAuth access token for Microsoft Graph.</summary>
public sealed class OneDriveVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "onedrive";
  public BackendCaps Caps => CloudCaps.WholeFile;

  private sealed class StaticTokenCredential(string token) : TokenCredential {
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
      => new(token, DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
      => ValueTask.FromResult(this.GetToken(requestContext, cancellationToken));
  }

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var client = new GraphServiceClient(new StaticTokenCredential(CredentialPayload.Password(credential.Secret)));
    var store = new OneDriveStore(client, uri.Host, MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), store);
  }

}

/// <summary>Google Drive backend: <c>gdrive://root/prefix</c> (host = folder id or "root"); the secret is a service-account JSON key.</summary>
public sealed class GoogleDriveVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "gdrive";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var googleCredential = GoogleCredential.FromJson(credential.Secret).CreateScoped(DriveService.Scope.Drive);
    var service = new DriveService(new BaseClientService.Initializer {
      HttpClientInitializer = googleCredential,
      ApplicationName = "DriveBenderUtility",
    });
    var store = new GoogleDriveStore(service, uri.Host.Length == 0 ? "root" : uri.Host, MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), store);
  }

}

/// <summary>Google Cloud Storage backend: <c>gcs://bucket/prefix</c>; the secret is a service-account JSON key.</summary>
public sealed class GoogleCloudStorageVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "gcs";
  public BackendCaps Caps => CloudCaps.WholeFile;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var credential = MemberUri.ResolveCredential(member, credentials, uri);
    var client = StorageClient.Create(GoogleCredential.FromJson(credential.Secret));
    var store = new GcsStore(client, uri.Host, MemberUri.RootPath(uri));
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, MemberUri.PhysicalId(uri), store);
  }

}
