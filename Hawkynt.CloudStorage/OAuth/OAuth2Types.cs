namespace Hawkynt.CloudStorage.OAuth;

/// <summary>
/// The OAuth2 endpoints and client identity for one provider. This is the "bring your own
/// client id" contract: the caller registers an application with the provider (Google
/// Cloud Console, Azure app registration, Box/Dropbox/Yandex developer console, …) and
/// supplies its <see cref="ClientId"/> — and <see cref="ClientSecret"/> when the provider
/// insists on one even for native apps. No secrets are embedded in this library.
/// </summary>
public sealed record OAuth2Config(
  string AuthorizationEndpoint,
  string TokenEndpoint,
  string ClientId,
  string? ClientSecret,
  IReadOnlyList<string> Scopes) {

  /// <summary>Extra authorization-request parameters some providers require (e.g. Google's <c>access_type=offline</c>).</summary>
  public IReadOnlyDictionary<string, string> ExtraAuthorizationParameters { get; init; }
    = new Dictionary<string, string>();

}

/// <summary>
/// A token set from a provider: the short-lived <see cref="AccessToken"/> plus the durable
/// <see cref="RefreshToken"/> that mints new access tokens without user interaction.
/// </summary>
public sealed record OAuth2Token(
  string AccessToken,
  string? RefreshToken,
  DateTimeOffset ExpiresAtUtc,
  string TokenType = "Bearer") {

  /// <summary>True once the access token is within <paramref name="skew"/> of expiry (default 60s of clock slop).</summary>
  public bool IsExpired(DateTimeOffset nowUtc, TimeSpan skew)
    => nowUtc + skew >= this.ExpiresAtUtc;

}

/// <summary>
/// Persistence for OAuth token sets, keyed by an opaque account handle. Implementations
/// back onto whatever secret store the host offers (OS keychain, encrypted file); this
/// library never chooses one for you.
/// </summary>
public interface ITokenStore {
  OAuth2Token? Load(string key);
  void Save(string key, OAuth2Token token);
  void Delete(string key);
}
