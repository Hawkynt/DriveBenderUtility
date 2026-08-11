using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DivisonM.EndToEnd.Tests.TestSupport;

/// <summary>
/// The management daemon as a user reaches it: the real <c>dbmount serve</c> process, on a
/// loopback port, driven over HTTP with the bearer token it prints on startup.
///
/// The daemon does not need a filesystem driver — it manages pools rather than mounting them —
/// so this half of the suite runs on any machine and in any CI job, including ones where the
/// driver tier has to skip.
/// </summary>
public sealed class ManagementDaemon : IDisposable {

  private static readonly TimeSpan _READY_TIMEOUT = TimeSpan.FromSeconds(60);

  private readonly Process _process;
  private readonly StringBuilder _stdout;
  private readonly StringBuilder _stderr;
  private readonly HttpClient _http;
  private bool _disposed;

  public int Port { get; }
  public string Token { get; }
  public Uri BaseAddress { get; }

  private ManagementDaemon(Process process, StringBuilder stdout, StringBuilder stderr, int port, string token) {
    this._process = process;
    this._stdout = stdout;
    this._stderr = stderr;
    this.Port = port;
    this.Token = token;
    this.BaseAddress = new($"http://127.0.0.1:{port}/");
    this._http = new() { BaseAddress = this.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
    this._http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
  }

  public string Log => $"stdout:{Environment.NewLine}{DbMount.Snapshot(this._stdout)}{Environment.NewLine}"
                       + $"stderr:{Environment.NewLine}{DbMount.Snapshot(this._stderr)}";

  public static ManagementDaemon Start() {
    var port = _FreePort();
    var process = DbMount.Start(["serve", "--port", port.ToString()], out var stdout, out var stderr);

    // the daemon prints `Management UI: http://127.0.0.1:<port>/?token=<token>` once bound
    var deadline = DateTime.UtcNow + _READY_TIMEOUT;
    while (DateTime.UtcNow < deadline) {
      if (process.HasExited)
        throw new InvalidOperationException(
          $"`dbmount serve` exited with code {process.ExitCode} before it was ready.{Environment.NewLine}"
          + $"stdout:{Environment.NewLine}{DbMount.Snapshot(stdout)}{Environment.NewLine}"
          + $"stderr:{Environment.NewLine}{DbMount.Snapshot(stderr)}");

      var printed = DbMount.Snapshot(stdout);
      var marker = printed.IndexOf("token=", StringComparison.Ordinal);
      if (marker >= 0) {
        var token = new string(printed[(marker + "token=".Length)..].TakeWhile(char.IsLetterOrDigit).ToArray());
        if (token.Length > 0) {
          var daemon = new ManagementDaemon(process, stdout, stderr, port, token);
          daemon._WaitUntilAnswering(deadline);
          return daemon;
        }
      }

      Thread.Sleep(200);
    }

    DbMount.KillTree(process);
    throw new TimeoutException(
      $"`dbmount serve` did not announce its URL within {_READY_TIMEOUT.TotalSeconds:F0}s.{Environment.NewLine}"
      + $"stdout:{Environment.NewLine}{DbMount.Snapshot(stdout)}{Environment.NewLine}"
      + $"stderr:{Environment.NewLine}{DbMount.Snapshot(stderr)}");
  }

  private void _WaitUntilAnswering(DateTime deadline) {
    while (DateTime.UtcNow < deadline) {
      try {
        using var response = this._http.GetAsync("api/pools").GetAwaiter().GetResult();
        if (response.IsSuccessStatusCode)
          return;
      } catch (Exception) {
        // still binding
      }

      Thread.Sleep(200);
    }

    throw new TimeoutException($"The daemon never answered /api/pools.{Environment.NewLine}{this.Log}");
  }

  /// <summary>A bound-then-released port; the daemon rebinds it immediately, which is fine on loopback.</summary>
  private static int _FreePort() {
    using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
  }

  public HttpResponseMessage Get(string path) => this._http.GetAsync(path).GetAwaiter().GetResult();

  /// <summary>POSTs with an empty body — HTTP.sys rejects a POST with no content length outright.</summary>
  public HttpResponseMessage Post(string path)
    => this._http.PostAsync(path, new StringContent("", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

  public JsonElement GetJson(string path) {
    using var response = this.Get(path);
    response.EnsureSuccessStatusCode();
    return JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
  }

  public JsonElement PostJson(string path) {
    using var response = this.Post(path);
    response.EnsureSuccessStatusCode();
    return JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
  }

  /// <summary>Reads the first Server-Sent-Events frame off the live stream, or times out.</summary>
  public string ReadFirstStreamFrame(TimeSpan timeout) {
    using var request = new HttpRequestMessage(HttpMethod.Get, "api/stream");
    request.Headers.Add("Authorization", $"Bearer {this.Token}");
    using var response = this._http.Send(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();

    using var stream = response.Content.ReadAsStream();
    using var reader = new StreamReader(stream);
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
      if (reader.ReadLine() is { } line && line.StartsWith("data: ", StringComparison.Ordinal))
        return line["data: ".Length..];

    throw new TimeoutException($"No SSE data frame arrived within {timeout.TotalSeconds:F0}s");
  }

  public void Dispose() {
    if (this._disposed)
      return;

    this._disposed = true;
    this._http.Dispose();
    DbMount.KillTree(this._process);
    this._process.WaitForExit(10_000);
    this._process.Dispose();
  }

}
