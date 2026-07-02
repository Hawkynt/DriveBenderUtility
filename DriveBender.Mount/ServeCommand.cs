using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DivisonM.Backends;
using DivisonM.Vfs;

namespace DivisonM.Mount;

/// <summary>
/// The local management daemon (§6.13): a loopback-bound HTTP API + Server-Sent-Events
/// metrics stream serving the animation-rich web UI. Bound to 127.0.0.1 and gated by a
/// per-session bearer token so no other local user can drive it (SEC — management API
/// authenticated). The same page is what the desktop shim hosts.
/// </summary>
internal sealed class ServeCommand(
  IHostEnvironment host,
  ManifestStore store,
  IPoolProvider provider,
  PoolLifecycle lifecycle,
  BackendMemberResolver remoteResolver,
  CredentialStore credentials,
  MountRegistry mountRegistry) {

  private static readonly JsonSerializerOptions _Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

  private readonly MetricsPublisher _metrics = new(host);
  private string _token = "";

  public int Run(ServeOptions options) {
    this._token = _NewToken();
    var prefix = $"http://127.0.0.1:{options.Port}/";
    using var listener = new HttpListener();
    listener.Prefixes.Add(prefix);
    try {
      listener.Start();
    } catch (HttpListenerException e) {
      Console.Error.WriteLine($"Could not bind {prefix}: {e.Message}");
      return 1;
    }

    var url = $"{prefix}?token={this._token}";
    Console.WriteLine($"Management UI: {url}");
    if (options.OpenBrowser)
      _TryOpenBrowser(url);

    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); listener.Stop(); };

    while (!stop.IsSet) {
      HttpListenerContext context;
      try {
        context = listener.GetContext();
      } catch (Exception) {
        break; // listener stopped
      }

      // each request handled on the thread pool so the SSE stream doesn't block the API
      ThreadPool.QueueUserWorkItem(_ => this._Handle(context));
    }

    return 0;
  }

  private void _Handle(HttpListenerContext context) {
    try {
      var request = context.Request;
      var path = request.Url?.AbsolutePath ?? "/";

      // static assets are public; everything under /api requires the token
      if (path.StartsWith("/api", StringComparison.Ordinal) && !this._Authorized(request)) {
        context.Response.StatusCode = 401;
        context.Response.Close();
        return;
      }

      switch (path) {
        case "/" or "/index.html":
          this._ServeAsset(context, "index.html", "text/html; charset=utf-8");
          break;
        case "/app.js":
          this._ServeAsset(context, "app.js", "text/javascript; charset=utf-8");
          break;
        case "/styles.css":
          this._ServeAsset(context, "styles.css", "text/css; charset=utf-8");
          break;
        case "/api/pools":
          this._WriteJson(context, this._Pools());
          break;
        case "/api/stream":
          this._Stream(context);
          break;
        case "/api/health" when request.HttpMethod == "POST":
          this._WriteJson(context, this._RunHealth(request));
          break;
        case "/api/restore" when request.HttpMethod == "POST":
          this._WriteJson(context, this._RunRestore(request));
          break;
        default:
          context.Response.StatusCode = 404;
          context.Response.Close();
          break;
      }
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]management request failed: {e.Message}");
      try { context.Response.Abort(); } catch { /* client gone */ }
    }
  }

  private bool _Authorized(HttpListenerRequest request) {
    var header = request.Headers["Authorization"];
    if (header != null && header.Equals($"Bearer {this._token}", StringComparison.Ordinal))
      return true;

    return request.QueryString["token"] == this._token;
  }

  /// <summary>Live pool DTOs: health from Inspect (always current) merged with the mounted-pool metrics snapshot.</summary>
  private object _Pools() {
    var mounted = mountRegistry.List().ToDictionary(m => m.PoolId);
    return new {
      pools = provider.Discover().Select(pool => {
        var health = provider.Inspect(pool);
        var metrics = mounted.ContainsKey(pool.PoolId) ? this._metrics.TryRead(pool.PoolId) : null;
        return new {
          id = pool.PoolId,
          name = pool.Name,
          source = pool.IsVirtual ? "native" : "manifest",
          degraded = health.IsDegraded,
          mounted = mounted.TryGetValue(pool.PoolId, out var entry) ? entry.Target : null,
          bytesFree = health.BytesFree,
          bytesTotal = health.BytesTotal,
          failureDomains = health.IndependentFailureDomains,
          warnings = health.Warnings,
          members = health.Members.Select(m => new {
            id = m.MemberId,
            path = m.ResolvedPath,
            label = m.Label,
            role = m.Role.ToString().ToLowerInvariant(),
            online = m.Online,
            network = m.Network,
          }),
          metrics = metrics == null ? null : new {
            metrics.ReadBytes,
            metrics.WrittenBytes,
            metrics.CacheHitRate,
            metrics.DirtyFiles,
            metrics.DrainedFiles,
            activity = metrics.RecentActivity,
          },
        };
      }),
      stampUtc = DateTime.UtcNow.ToString("O"),
    };
  }

  private void _Stream(HttpListenerContext context) {
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.Add("Cache-Control", "no-cache");
    context.Response.SendChunked = true;
    using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false));
    try {
      while (true) {
        var frame = JsonSerializer.Serialize(this._Pools(), _Json);
        writer.Write($"data: {frame}\n\n");
        writer.Flush();
        Thread.Sleep(1000); // 1 Hz live feed (NFR-UI-LIVE)
      }
    } catch (Exception) {
      // client disconnected
    }
  }

  private object _RunHealth(HttpListenerRequest request) {
    var poolRef = this._RequirePool(request);
    var fix = request.QueryString["fix"] == "true";
    return _Guard(() => PoolOpsCommand.Health(host, provider, remoteResolver, new() { Pool = poolRef, Fix = fix }) == 0 ? "ok" : "attention");
  }

  private object _RunRestore(HttpListenerRequest request) {
    var poolRef = this._RequirePool(request);
    return _Guard(() => { PoolOpsCommand.Restore(host, provider, remoteResolver, new() { Pool = poolRef }); return "ok"; });
  }

  private string _RequirePool(HttpListenerRequest request)
    => request.QueryString["pool"] ?? throw new ManifestException("missing ?pool=");

  private static object _Guard(Func<object> action) {
    try {
      return new { ok = true, result = action() };
    } catch (Exception e) {
      return new { ok = false, error = e.Message };
    }
  }

  private void _WriteJson(HttpListenerContext context, object payload) {
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, _Json));
    context.Response.ContentType = "application/json; charset=utf-8";
    context.Response.OutputStream.Write(bytes);
    context.Response.Close();
  }

  private void _ServeAsset(HttpListenerContext context, string name, string contentType) {
    var content = _ReadAsset(name);
    if (content == null) {
      context.Response.StatusCode = 404;
      context.Response.Close();
      return;
    }

    var bytes = Encoding.UTF8.GetBytes(content);
    context.Response.ContentType = contentType;
    context.Response.OutputStream.Write(bytes);
    context.Response.Close();
  }

  private static string? _ReadAsset(string name) {
    var assembly = Assembly.GetExecutingAssembly();
    var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".WebAssets." + name, StringComparison.OrdinalIgnoreCase));
    if (resource == null)
      return null;

    using var stream = assembly.GetManifestResourceStream(resource)!;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  private static string _NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

  private static void _TryOpenBrowser(string url) {
    try {
      if (OperatingSystem.IsWindows())
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
      else if (OperatingSystem.IsLinux())
        System.Diagnostics.Process.Start("xdg-open", url);
    } catch (Exception) {
      // headless / no browser — the URL is printed anyway
    }
  }

}
