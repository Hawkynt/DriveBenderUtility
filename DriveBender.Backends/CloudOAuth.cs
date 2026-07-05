using System.Globalization;
using System.Text.Json.Nodes;
using DivisonM.Vfs;
using Hawkynt.CloudStorage.OAuth;

namespace DivisonM.Backends;

/// <summary>
/// Builds a refreshing <see cref="IAccessTokenProvider"/> for an OAuth cloud member from its
/// stored credential (SEC-CRED). The credential secret is JSON carrying the caller's own
/// registered <c>clientId</c>/<c>clientSecret</c> plus the <c>refreshToken</c> obtained by the
/// one-time login flow; the short-lived access token is minted (and silently rotated) from
/// the refresh token, and the rotated set is written back to the credential store when it is
/// writable.
/// </summary>
internal static class CloudOAuth {

  private static readonly HttpClient _http = new();

  public static IAccessTokenProvider TokenProvider(MemberDescriptor member, ICredentialResolver? credentials, Func<string, string, OAuth2Config> configFactory) {
    var reference = member.CredentialReference
                    ?? throw new ManifestException($"Member '{member.DisplayName}' needs an OAuth credential: register your own client id and run 'dbmount credential login <name>'");

    var secret = credentials?.Resolve(reference)?.Secret
                 ?? throw new ManifestException($"Member '{member.DisplayName}': no credential stored for '{reference}'");

    if (!CredentialPayload.TryGetJsonField(secret, "clientId", out var clientId)
        || !CredentialPayload.TryGetJsonField(secret, "clientSecret", out var clientSecret))
      throw new ManifestException($"Member '{member.DisplayName}': the OAuth secret must be JSON with clientId, clientSecret and refreshToken");

    var config = configFactory(clientId, clientSecret);
    var tokenStore = new CredentialTokenStore(credentials, reference);
    return new OAuth2TokenProvider(new OAuth2Client(_http), config, tokenStore, reference);
  }

}

/// <summary>
/// An <see cref="ITokenStore"/> over a member's credential entry: it reads the token set from
/// the JSON secret and, when the resolver is a writable <see cref="CredentialStore"/>, merges
/// a rotated set back while preserving the client id/secret. On a read-only resolver the save
/// is a no-op — the session still refreshes in memory, it just re-derives next start.
/// </summary>
internal sealed class CredentialTokenStore(ICredentialResolver? credentials, string reference) : ITokenStore {

  public OAuth2Token? Load(string key) {
    var secret = credentials?.Resolve(reference)?.Secret;
    if (secret == null)
      return null;

    CredentialPayload.TryGetJsonField(secret, "accessToken", out var access);
    CredentialPayload.TryGetJsonField(secret, "refreshToken", out var refresh);
    if (access.Length == 0 && refresh.Length == 0)
      return null;

    var expiresAt = CredentialPayload.TryGetJsonField(secret, "expiresAt", out var iso)
                    && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
      ? parsed
      : DateTimeOffset.MinValue;

    return new(access, refresh.Length == 0 ? null : refresh, expiresAt);
  }

  public void Save(string key, OAuth2Token token) {
    if (credentials is not CredentialStore writable)
      return;

    var current = writable.Resolve(reference);
    if (current == null)
      return;

    var node = _AsObject(current.Secret);
    node["accessToken"] = token.AccessToken;
    if (token.RefreshToken != null)
      node["refreshToken"] = token.RefreshToken;
    node["expiresAt"] = token.ExpiresAtUtc.ToString("o", CultureInfo.InvariantCulture);
    writable.Store(reference, current.UserName, node.ToJsonString());
  }

  public void Delete(string key) => (credentials as CredentialStore)?.Remove(reference);

  private static JsonObject _AsObject(string secret) {
    try {
      return JsonNode.Parse(secret) as JsonObject ?? new JsonObject();
    } catch (Exception) {
      return new JsonObject();
    }
  }

}
