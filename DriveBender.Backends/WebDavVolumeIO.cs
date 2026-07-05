using DivisonM.Vfs;
using Hawkynt.CloudStorage.Stores;

namespace DivisonM.Backends;

/// <summary>WebDAV backend factory: webdav:// maps to http, webdavs://, davs:// to https.</summary>
public sealed class WebDavVolumeIOBackend : IVolumeIOBackend {

  public string Scheme => "webdav";
  public BackendCaps Caps => BackendCaps.List | BackendCaps.Delete | BackendCaps.ServerCredentials;

  public IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials) {
    var uri = MemberUri.Parse(member);
    var secure = uri.Scheme is "webdavs" or "davs" or "https";
    var baseAddress = new UriBuilder(secure ? "https" : "http", uri.Host, uri.IsDefaultPort ? (secure ? 443 : 80) : uri.Port).Uri;
    var credential = MemberUri.ResolveCredential(member, credentials, uri, required: false);
    var store = new WebDavCloudStore(baseAddress, MemberUri.RootPath(uri), credential.UserName, CredentialPayload.Password(credential.Secret));
    var physicalId = $"webdav://{uri.Host.ToUpperInvariant()}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}/{MemberUri.RootPath(uri)}";
    return new WholeFileVolumeIO(member.MemberId, member.DisplayName, physicalId, new CloudStoreAdapter(store));
  }

}
