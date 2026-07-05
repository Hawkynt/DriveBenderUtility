using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using FluentAssertions;
using Hawkynt.CloudStorage;
using Hawkynt.CloudStorage.OAuth;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The "bring your own client id" OAuth2 machinery (installed-app authorization-code + PKCE
/// with silent refresh), exercised headlessly against a stub token endpoint — no network, no
/// browser, no provider account (the transport is isolated from the loopback UI by design).
/// </summary>
[TestFixture]
[Category("Unit")]
public class CloudStorageOAuthTests {

  private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler {
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      this.LastRequest = request;
      this.LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
      var (status, body) = respond(request);
      return new(status) { Content = new StringContent(body) };
    }
  }

  private sealed class InMemoryTokenStore : ITokenStore {
    private readonly Dictionary<string, OAuth2Token> _tokens = new();
    public int Saves { get; private set; }
    public OAuth2Token? Load(string key) => this._tokens.GetValueOrDefault(key);
    public void Save(string key, OAuth2Token token) { this._tokens[key] = token; ++this.Saves; }
    public void Delete(string key) => this._tokens.Remove(key);
    public void Seed(string key, OAuth2Token token) => this._tokens[key] = token;
  }

  private static OAuth2Config _Config()
    => CloudOAuthProviders.GoogleDrive("client-123", "secret-abc");

  [Test]
  [Category("HappyPath")]
  public void Pkce_GivenFreshPair_WhenCreated_ThenChallengeIsS256OfVerifier() {
    var pair = PkcePair.Create();

    var expected = Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(pair.Verifier)));
    pair.Challenge.Should().Be(expected);
    pair.Verifier.Should().NotBe(PkcePair.Create().Verifier, "each pair carries fresh entropy");
    pair.Challenge.Should().NotContainAny("+", "/", "=");
  }

  [Test]
  [Category("HappyPath")]
  public void BuildAuthorizationUrl_GivenConfigAndPkce_WhenBuilt_ThenCarriesEveryRequiredParameter() {
    var client = new OAuth2Client(new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))));
    var pkce = PkcePair.Create();

    var url = client.BuildAuthorizationUrl(_Config(), "http://127.0.0.1:5000/", pkce, "state-xyz");

    var query = HttpUtility.ParseQueryString(new Uri(url).Query);
    query["response_type"].Should().Be("code");
    query["client_id"].Should().Be("client-123");
    query["redirect_uri"].Should().Be("http://127.0.0.1:5000/");
    query["code_challenge"].Should().Be(pkce.Challenge);
    query["code_challenge_method"].Should().Be("S256");
    query["state"].Should().Be("state-xyz");
    query["access_type"].Should().Be("offline", "Google only returns a refresh token for offline access");
    query["scope"].Should().Contain("drive");
  }

  [Test]
  [Category("HappyPath")]
  public void ExchangeCode_GivenAuthorizationCode_WhenExchanged_ThenTokensAndExpiryParsed() {
    var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var handler = new StubHandler(_ => (HttpStatusCode.OK, """{"access_token":"AT","refresh_token":"RT","expires_in":3600,"token_type":"Bearer"}"""));
    var client = new OAuth2Client(new HttpClient(handler), () => now);

    var token = client.ExchangeCodeAsync(_Config(), "the-code", "http://127.0.0.1:5000/", "the-verifier").GetAwaiter().GetResult();

    token.AccessToken.Should().Be("AT");
    token.RefreshToken.Should().Be("RT");
    token.ExpiresAtUtc.Should().Be(now.AddSeconds(3600));
    handler.LastBody.Should().Contain("grant_type=authorization_code")
      .And.Contain("code_verifier=the-verifier")
      .And.Contain("client_secret=secret-abc");
  }

  [Test]
  [Category("EdgeCase")]
  public void Refresh_GivenResponseWithoutRefreshToken_WhenRefreshed_ThenOldRefreshTokenCarriesForward() {
    var handler = new StubHandler(_ => (HttpStatusCode.OK, """{"access_token":"AT2","expires_in":3600}"""));
    var client = new OAuth2Client(new HttpClient(handler));

    var token = client.RefreshAsync(_Config(), "original-refresh").GetAwaiter().GetResult();

    token.AccessToken.Should().Be("AT2");
    token.RefreshToken.Should().Be("original-refresh", "providers that omit a rotated token keep the existing one alive");
  }

  [Test]
  [Category("Exception")]
  public void Exchange_GivenTokenEndpointError_WhenExchanged_ThenAccessDenied() {
    var handler = new StubHandler(_ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
    var client = new OAuth2Client(new HttpClient(handler));

    var act = () => client.RefreshAsync(_Config(), "bad").GetAwaiter().GetResult();

    act.Should().Throw<CloudStorageException>().Which.Error.Should().Be(CloudStorageError.AccessDenied);
  }

  [Test]
  [Category("HappyPath")]
  public void TokenProvider_GivenExpiredAccessToken_WhenAccessed_ThenRefreshesAndPersists() {
    var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var store = new InMemoryTokenStore();
    store.Seed("acct", new("stale", "RT", now.AddMinutes(-5))); // already expired
    var handler = new StubHandler(_ => (HttpStatusCode.OK, """{"access_token":"fresh","refresh_token":"RT2","expires_in":3600}"""));
    var provider = new OAuth2TokenProvider(new OAuth2Client(new HttpClient(handler), () => now), _Config(), store, "acct", () => now);

    provider.GetAccessToken().Should().Be("fresh");
    store.Load("acct")!.RefreshToken.Should().Be("RT2", "the rotated refresh token is written back");
    store.Saves.Should().Be(1);

    provider.GetAccessToken().Should().Be("fresh");
    store.Saves.Should().Be(1, "a still-valid access token is served from cache without another refresh");
  }

  [Test]
  [Category("Exception")]
  public void TokenProvider_GivenNoStoredToken_WhenAccessed_ThenAccessDeniedGuidesToLogin() {
    var provider = new OAuth2TokenProvider(new OAuth2Client(new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}")))), _Config(), new InMemoryTokenStore(), "acct");

    var act = () => provider.GetAccessToken();

    act.Should().Throw<CloudStorageException>()
      .Which.Error.Should().Be(CloudStorageError.AccessDenied);
  }

}
