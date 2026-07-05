using System.Net.Http.Headers;

namespace Hawkynt.CloudStorage.OAuth;

/// <summary>
/// A delegating handler that stamps a fresh <c>Authorization: Bearer</c> header on every
/// outbound request from an <see cref="IAccessTokenProvider"/>. Because the token is fetched
/// per request, a mid-session refresh is picked up transparently by any SDK client built on
/// an <see cref="HttpClient"/> that wraps this handler.
/// </summary>
public sealed class BearerInjectingHandler(IAccessTokenProvider tokens, HttpMessageHandler? innerHandler = null)
  : DelegatingHandler(innerHandler ?? new HttpClientHandler()) {

  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetAccessToken());
    return base.SendAsync(request, cancellationToken);
  }

}
