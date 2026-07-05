namespace Hawkynt.CloudStorage.OAuth;

/// <summary>Supplies a currently-valid bearer access token to a store on demand.</summary>
public interface IAccessTokenProvider {
  string GetAccessToken();
}

/// <summary>A fixed token — for app passwords or externally-managed tokens with no refresh.</summary>
public sealed class StaticAccessTokenProvider(string accessToken) : IAccessTokenProvider {
  public string GetAccessToken() => accessToken;
}

/// <summary>
/// Keeps a provider's access token fresh: it caches the persisted token set and, when the
/// access token is within the expiry skew, silently refreshes it through the
/// <see cref="OAuth2Client"/> and writes the rotated set back to the <see cref="ITokenStore"/>.
/// A store that holds no refresh token (never authorized, or a token the provider revoked)
/// surfaces as <see cref="CloudStorageError.AccessDenied"/> so the caller re-runs the login.
/// </summary>
public sealed class OAuth2TokenProvider(
  OAuth2Client client,
  OAuth2Config config,
  ITokenStore store,
  string accountKey,
  Func<DateTimeOffset>? clock = null,
  TimeSpan? expirySkew = null) : IAccessTokenProvider {

  private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.UtcNow);
  private readonly TimeSpan _skew = expirySkew ?? TimeSpan.FromSeconds(60);
  private readonly Lock _gate = new();
  private OAuth2Token? _cached;

  public string GetAccessToken() {
    lock (this._gate) {
      this._cached ??= store.Load(accountKey)
                       ?? throw new CloudStorageException(CloudStorageError.AccessDenied, $"No stored OAuth token for '{accountKey}'; run the login flow first");

      if (!this._cached.IsExpired(this._clock(), this._skew))
        return this._cached.AccessToken;

      if (string.IsNullOrEmpty(this._cached.RefreshToken))
        throw new CloudStorageException(CloudStorageError.AccessDenied, $"OAuth token for '{accountKey}' expired and no refresh token is available; run the login flow again");

      var refreshed = client.RefreshAsync(config, this._cached.RefreshToken).GetAwaiter().GetResult();
      store.Save(accountKey, refreshed);
      this._cached = refreshed;
      return refreshed.AccessToken;
    }
  }

}
