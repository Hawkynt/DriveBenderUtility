using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace Hawkynt.CloudStorage.OAuth;

/// <summary>
/// The transport half of the OAuth2 authorization-code + PKCE flow (RFC 6749 / 7636):
/// builds the authorization URL, exchanges the returned code for tokens, and refreshes an
/// access token from a refresh token. It is deliberately free of any UI or listener — the
/// caller owns redirect capture — so it unit-tests against a stub <see cref="HttpClient"/>.
/// </summary>
public sealed class OAuth2Client(HttpClient httpClient, Func<DateTimeOffset>? clock = null) {

  private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.UtcNow);

  /// <summary>Composes the provider authorization URL for the given redirect and PKCE challenge.</summary>
  public string BuildAuthorizationUrl(OAuth2Config config, string redirectUri, PkcePair pkce, string state) {
    var query = HttpUtility.ParseQueryString(string.Empty);
    query["response_type"] = "code";
    query["client_id"] = config.ClientId;
    query["redirect_uri"] = redirectUri;
    query["scope"] = string.Join(' ', config.Scopes);
    query["state"] = state;
    query["code_challenge"] = pkce.Challenge;
    query["code_challenge_method"] = PkcePair.ChallengeMethod;
    foreach (var (key, value) in config.ExtraAuthorizationParameters)
      query[key] = value;

    return $"{config.AuthorizationEndpoint}?{query}";
  }

  /// <summary>Trades an authorization code (and its PKCE verifier) for an access + refresh token set.</summary>
  public async Task<OAuth2Token> ExchangeCodeAsync(OAuth2Config config, string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken = default) {
    var form = new Dictionary<string, string> {
      ["grant_type"] = "authorization_code",
      ["code"] = code,
      ["redirect_uri"] = redirectUri,
      ["client_id"] = config.ClientId,
      ["code_verifier"] = codeVerifier,
    };
    if (!string.IsNullOrEmpty(config.ClientSecret))
      form["client_secret"] = config.ClientSecret;

    return await this._PostTokenAsync(config, form, previousRefreshToken: null, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Mints a new access token from a refresh token; carries the old refresh token forward when the provider omits it.</summary>
  public async Task<OAuth2Token> RefreshAsync(OAuth2Config config, string refreshToken, CancellationToken cancellationToken = default) {
    var form = new Dictionary<string, string> {
      ["grant_type"] = "refresh_token",
      ["refresh_token"] = refreshToken,
      ["client_id"] = config.ClientId,
    };
    if (config.Scopes.Count > 0)
      form["scope"] = string.Join(' ', config.Scopes);
    if (!string.IsNullOrEmpty(config.ClientSecret))
      form["client_secret"] = config.ClientSecret;

    return await this._PostTokenAsync(config, form, previousRefreshToken: refreshToken, cancellationToken).ConfigureAwait(false);
  }

  private async Task<OAuth2Token> _PostTokenAsync(OAuth2Config config, Dictionary<string, string> form, string? previousRefreshToken, CancellationToken cancellationToken) {
    using var response = await httpClient.PostAsync(config.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    if (!response.IsSuccessful())
      throw new CloudStorageException(CloudStorageError.AccessDenied, $"OAuth token endpoint returned {(int)response.StatusCode}: {body}");

    var token = JsonSerializer.Deserialize<TokenResponse>(body)
                ?? throw new CloudStorageException(CloudStorageError.IoError, "OAuth token endpoint returned an empty body");

    var expiresAt = this._clock() + TimeSpan.FromSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600);
    return new(token.AccessToken, token.RefreshToken ?? previousRefreshToken, expiresAt, token.TokenType ?? "Bearer");
  }

  private sealed record TokenResponse {
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; init; }
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
  }

}

internal static class HttpResponseExtensions {
  public static bool IsSuccessful(this HttpResponseMessage response) => response.IsSuccessStatusCode;
}
