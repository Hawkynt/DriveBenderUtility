using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Hawkynt.CloudStorage.OAuth;

/// <summary>
/// Drives the interactive half of the installed-application flow: it opens the provider's
/// consent page in the user's browser and captures the redirect on a transient
/// <c>http://127.0.0.1:{port}/</c> loopback listener (the redirect method Google, Microsoft,
/// Box and others bless for native apps). The captured code is exchanged for tokens via
/// <see cref="OAuth2Client"/>. This is the only component that needs a desktop session, so
/// hosts run it once per account and persist the resulting refresh token.
/// </summary>
public sealed class LoopbackAuthorizer(OAuth2Client client, Action<string>? openBrowser = null) {

  private readonly Action<string> _openBrowser = openBrowser ?? _DefaultOpenBrowser;

  public async Task<OAuth2Token> AuthorizeAsync(OAuth2Config config, CancellationToken cancellationToken = default) {
    var port = _FreeLoopbackPort();
    var redirectUri = $"http://127.0.0.1:{port}/";
    var pkce = PkcePair.Create();
    var state = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));

    using var listener = new HttpListener();
    listener.Prefixes.Add(redirectUri);
    listener.Start();

    try {
      this._openBrowser(client.BuildAuthorizationUrl(config, redirectUri, pkce, state));

      using var registration = cancellationToken.Register(listener.Stop);
      var context = await listener.GetContextAsync().ConfigureAwait(false);
      var query = context.Request.QueryString;

      var code = query["code"];
      var returnedState = query["state"];
      var error = query["error"];
      _Respond(context, error, code);

      if (!string.IsNullOrEmpty(error))
        throw new CloudStorageException(CloudStorageError.AccessDenied, $"Authorization denied: {error}");
      if (returnedState != state)
        throw new CloudStorageException(CloudStorageError.AccessDenied, "Authorization state mismatch — possible CSRF, aborting");
      if (string.IsNullOrEmpty(code))
        throw new CloudStorageException(CloudStorageError.AccessDenied, "Authorization returned no code");

      return await client.ExchangeCodeAsync(config, code, redirectUri, pkce.Verifier, cancellationToken).ConfigureAwait(false);
    } finally {
      if (listener.IsListening)
        listener.Stop();
    }
  }

  private static void _Respond(HttpListenerContext context, string? error, string? code) {
    var ok = string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code);
    var message = ok
      ? "Authorization complete — you can close this tab and return to the application."
      : $"Authorization failed: {HttpUtility.HtmlEncode(error ?? "no code returned")}.";
    var html = Encoding.UTF8.GetBytes($"<!doctype html><html><body style=\"font-family:sans-serif\"><p>{message}</p></body></html>");
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.ContentLength64 = html.Length;
    context.Response.OutputStream.Write(html, 0, html.Length);
    context.Response.OutputStream.Close();
  }

  private static int _FreeLoopbackPort() {
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
  }

  private static void _DefaultOpenBrowser(string url) {
    try {
      Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    } catch (Exception) {
      // headless or no browser: the caller is expected to print the URL as a fallback
    }
  }

}
