using System.Net;
using System.Text.Json;
using Hawkynt.CloudStorage.OAuth;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// A thin JSON/bytes REST helper shared by the providers reached over their own HTTP API
/// (Box, Yandex Disk, Strato HiDrive). It carries a bearer token refreshed per request and
/// normalizes transport/status failures into <see cref="CloudStorageException"/>. Relative
/// request URIs resolve against the base address; absolute URIs (e.g. provider-issued upload
/// hrefs) are used as-is.
/// </summary>
internal sealed class CloudRest(IAccessTokenProvider tokens, string baseAddress) : IDisposable {

  private readonly HttpClient _http = new(new BearerInjectingHandler(tokens)) { BaseAddress = new Uri(baseAddress) };

  public HttpResponseMessage Send(HttpRequestMessage request) {
    try {
      return this._http.Send(request);
    } catch (HttpRequestException e) {
      throw new CloudStorageException(CloudStorageError.Offline, $"{request.Method} {request.RequestUri}: {e.Message}", e);
    } catch (TaskCanceledException e) {
      throw new CloudStorageException(CloudStorageError.Offline, $"{request.Method} {request.RequestUri}: timed out", e);
    }
  }

  public static CloudStorageException FromStatus(HttpStatusCode status, string what) => (int)status switch {
    404 or 410 => new(CloudStorageError.NotFound, $"{what}: not found"),
    401 or 403 => new(CloudStorageError.AccessDenied, $"{what}: access denied (HTTP {(int)status})"),
    409 => new(CloudStorageError.Exists, $"{what}: conflict"),
    507 => new(CloudStorageError.NoSpace, $"{what}: storage exhausted"),
    _ => new(CloudStorageError.IoError, $"{what}: HTTP {(int)status}"),
  };

  public JsonElement GetJson(string url, string what) {
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    using var response = this.Send(request);
    if (!response.IsSuccessStatusCode)
      throw FromStatus(response.StatusCode, what);

    using var stream = response.Content.ReadAsStream();
    return JsonDocument.Parse(stream).RootElement.Clone();
  }

  public JsonElement? TryGetJson(string url, string what) {
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    using var response = this.Send(request);
    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
      return null;
    if (!response.IsSuccessStatusCode)
      throw FromStatus(response.StatusCode, what);

    using var stream = response.Content.ReadAsStream();
    return JsonDocument.Parse(stream).RootElement.Clone();
  }

  public byte[] GetBytes(string url, string what) {
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    using var response = this.Send(request);
    if (!response.IsSuccessStatusCode)
      throw FromStatus(response.StatusCode, what);

    return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
  }

  public JsonElement SendJson(HttpMethod method, string url, string what, HttpContent? content = null) {
    using var request = new HttpRequestMessage(method, url) { Content = content };
    using var response = this.Send(request);
    if (!response.IsSuccessStatusCode)
      throw FromStatus(response.StatusCode, what);

    using var stream = response.Content.ReadAsStream();
    return JsonDocument.Parse(stream).RootElement.Clone();
  }

  public void Send(HttpMethod method, string url, string what, HttpContent? content = null, params HttpStatusCode[] tolerate) {
    using var request = new HttpRequestMessage(method, url) { Content = content };
    using var response = this.Send(request);
    if (response.IsSuccessStatusCode || tolerate.Contains(response.StatusCode))
      return;

    throw FromStatus(response.StatusCode, what);
  }

  public void Dispose() => this._http.Dispose();

}
