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
    using var stream = this.GetStream(url, what);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  /// <summary>
  /// The response body as a stream the caller owns. Reading it incrementally is what keeps a
  /// multi-gigabyte object out of memory; the previous whole-array read also blocked on an async
  /// call for no reason, since HttpContent exposes a synchronous stream directly.
  /// </summary>
  public Stream GetStream(string url, string what) {
    var request = new HttpRequestMessage(HttpMethod.Get, url);
    var response = this.Send(request);
    try {
      if (!response.IsSuccessStatusCode)
        throw FromStatus(response.StatusCode, what);

      // the response owns the connection; it must outlive the stream handed back
      return new OwnedStream(response.Content.ReadAsStream(), response, response.Content.Headers.ContentLength ?? -1);
    } catch {
      response.Dispose();
      throw;
    } finally {
      request.Dispose();
    }
  }

  /// <summary>
  /// A ranged GET (RFC 9110 <c>Range: bytes=from-to</c>), which every one of these providers
  /// serves over plain HTTP. A server that ignores the header answers 200 with the whole body —
  /// still correct, just not cheap — and one that reports the range as unsatisfiable means the
  /// caller asked at or past the end, which yields nothing, exactly as on a local file.
  /// </summary>
  public Stream GetRange(string url, long offset, long count, string what) {
    if (count <= 0)
      return EmptyStream.Instance;

    var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Range = new(offset, offset + count - 1);
    var response = this.Send(request);
    try {
      if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        return EmptyStream.Instance;
      if (!response.IsSuccessStatusCode)
        throw FromStatus(response.StatusCode, what);

      var body = response.Content.ReadAsStream();

      // 200 instead of 206: the server ignored the range and is sending the whole object, so the
      // requested window has to be cut out of the stream here rather than silently mis-serving it
      if (response.StatusCode != HttpStatusCode.PartialContent)
        return new OwnedStream(new WindowStream(body, offset, count), response, count);

      return new OwnedStream(body, response, response.Content.Headers.ContentLength ?? count);
    } catch {
      response.Dispose();
      throw;
    } finally {
      request.Dispose();
    }
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
