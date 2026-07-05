namespace Hawkynt.CloudStorage.OAuth;

/// <summary>
/// Ready-made <see cref="OAuth2Config"/> factories for the providers this library speaks.
/// Every factory takes the caller's own registered client id (and secret, where the
/// provider mandates one) — nothing is embedded here. Scope defaults grant full read/write
/// on the user's drive and, where the provider gates it behind a flag, offline access so a
/// refresh token is issued.
/// </summary>
public static class CloudOAuthProviders {

  /// <summary>Google Drive. Google issues a refresh token only with <c>access_type=offline</c> and a fresh consent prompt.</summary>
  public static OAuth2Config GoogleDrive(string clientId, string clientSecret, IReadOnlyList<string>? scopes = null)
    => new(
      "https://accounts.google.com/o/oauth2/v2/auth",
      "https://oauth2.googleapis.com/token",
      clientId,
      clientSecret,
      scopes ?? ["https://www.googleapis.com/auth/drive"]) {
      ExtraAuthorizationParameters = new Dictionary<string, string> {
        ["access_type"] = "offline",
        ["prompt"] = "consent",
      },
    };

  /// <summary>Microsoft OneDrive — personal and business share the <c>/common</c> endpoint; <c>offline_access</c> yields the refresh token.</summary>
  public static OAuth2Config OneDrive(string clientId, string? clientSecret = null, IReadOnlyList<string>? scopes = null)
    => new(
      "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
      "https://login.microsoftonline.com/common/oauth2/v2.0/token",
      clientId,
      clientSecret,
      scopes ?? ["Files.ReadWrite.All", "offline_access", "User.Read"]);

  /// <summary>Dropbox. <c>token_access_type=offline</c> is what turns a one-shot token into a refreshable one.</summary>
  public static OAuth2Config Dropbox(string clientId, string clientSecret, IReadOnlyList<string>? scopes = null)
    => new(
      "https://www.dropbox.com/oauth2/authorize",
      "https://api.dropboxapi.com/oauth2/token",
      clientId,
      clientSecret,
      scopes ?? ["files.content.write", "files.content.read", "files.metadata.read", "account_info.read"]) {
      ExtraAuthorizationParameters = new Dictionary<string, string> {
        ["token_access_type"] = "offline",
      },
    };

  /// <summary>Box. Box requires the client secret even for the authorization-code flow.</summary>
  public static OAuth2Config Box(string clientId, string clientSecret, IReadOnlyList<string>? scopes = null)
    => new(
      "https://account.box.com/api/oauth2/authorize",
      "https://api.box.com/oauth2/token",
      clientId,
      clientSecret,
      scopes ?? []);

  /// <summary>Yandex Disk (Yandex OAuth). The client secret is required at the token endpoint.</summary>
  public static OAuth2Config YandexDisk(string clientId, string clientSecret, IReadOnlyList<string>? scopes = null)
    => new(
      "https://oauth.yandex.com/authorize",
      "https://oauth.yandex.com/token",
      clientId,
      clientSecret,
      scopes ?? ["cloud_api:disk.read", "cloud_api:disk.write", "cloud_api:disk.info"]);

  /// <summary>Strato HiDrive. Scopes are <c>{role},{path-scope}</c>, e.g. <c>rw,user</c> for full read/write.</summary>
  public static OAuth2Config HiDrive(string clientId, string clientSecret, IReadOnlyList<string>? scopes = null)
    => new(
      "https://my.hidrive.com/client/authorize",
      "https://my.hidrive.com/oauth2/token",
      clientId,
      clientSecret,
      scopes ?? ["rw,user"]);

}
