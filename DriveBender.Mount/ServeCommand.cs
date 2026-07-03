using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DivisonM.Backends;
using DivisonM.Vfs;

namespace DivisonM.Mount;

/// <summary>A mount was requested but the filesystem driver prerequisite is missing.</summary>
internal sealed class PrereqException(PrereqStatus status) : Exception(status.Detail) {
  public PrereqStatus Status { get; } = status;
}

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

  // the manager MANAGES; it does not probe. One background sampler builds the live frame off the
  // client path and caches it as JSON — every SSE client and /api/pools just ship this cache, so a
  // client connection can never stall on a drive scan. Discovery (the expensive drive walk) is
  // cached separately and refreshed slowly; a mounted pool's per-storage space comes from the
  // pool's own published snapshot, never a fresh disk probe here.
  private volatile string _frameJson = "{\"pools\":[]}";
  private readonly Lock _discoLock = new();
  private DateTime _discoAtUtc = DateTime.MinValue;
  private IReadOnlyList<DiscoveredPool>? _disco;

  private sealed record DiscoveredPool(PoolRef Pool, PoolHealth Health, IReadOnlyDictionary<Guid, (long free, long total)> MemberSpace);

  public int Run(ServeOptions options) {
    // a fixed token (from the desktop shell) keeps the URL stable across a daemon restart, so the
    // page's SSE/fetches just resume and open dialogs survive; else a fresh per-session token
    this._token = string.IsNullOrWhiteSpace(options.Token) ? _NewToken() : options.Token!;
    var prefix = $"http://127.0.0.1:{options.Port}/";

    using var stop = new ManualResetEventSlim();
    var announced = false;

    // the single frame sampler runs for the whole daemon lifetime, independent of any client
    new Thread(() => this._Sampler(stop)) { IsBackground = true, Name = "frame-sampler" }.Start();

    // the listener is self-healing: if HTTP.sys drops it we rebuild and keep serving instead of
    // exiting the process — an exit would force the desktop shell to reload and lose UI state
    while (!stop.IsSet) {
      using var listener = new HttpListener();
      listener.Prefixes.Add(prefix);
      try {
        listener.Start();
      } catch (HttpListenerException e) {
        Console.Error.WriteLine($"Could not bind {prefix}: {e.Message}");
        return 1;
      }

      if (!announced) {
        var url = $"{prefix}?token={this._token}";
        Console.WriteLine($"Management UI: {url}");
        if (options.OpenBrowser)
          _TryOpenBrowser(url);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); try { listener.Stop(); } catch { } };
        announced = true;
      }

      try {
        this._Accept(listener, stop);
      } catch (Exception e) {
        if (stop.IsSet)
          break;

        DriveBender.Logger($"[Warning]listener died ({e.Message}) — rebuilding");
        Thread.Sleep(250); // let HTTP.sys settle before rebinding
      }
    }

    return 0;
  }

  private void _Accept(HttpListener listener, ManualResetEventSlim stop) {
    while (!stop.IsSet) {
      HttpListenerContext context;
      try {
        context = listener.GetContext();
      } catch (Exception e) {
        if (stop.IsSet)
          return;
        if (!listener.IsListening)
          throw; // the listener itself broke — Run rebuilds it

        // transient accept failure (client aborted mid-handshake etc.): keep THIS listener —
        // rebuilding would tear down every live SSE stream and show "reconnecting…" in the UI
        DriveBender.Logger($"[Warning]management accept failed: {e.Message}");
        continue;
      }

      // the SSE stream blocks for the life of the connection; run it on a DEDICATED thread so it
      // can never starve the request thread pool. Short requests stay on the pool.
      if ((context.Request.Url?.AbsolutePath ?? "") == "/api/stream")
        new Thread(() => this._Handle(context)) { IsBackground = true }.Start();
      else
        ThreadPool.QueueUserWorkItem(_ => this._Handle(context));
    }
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
          this._WriteRawJson(context, this._frameJson); // the sampler's cached frame — no collection on this request
          break;
        case "/api/fs/list":
          this._WriteJson(context, this._FsList(request));
          break;
        case "/api/pool/browse":
          this._WriteJson(context, _Guard(() => PoolOpsCommand.Browse(host, provider, remoteResolver, this._RequirePool(request), request.QueryString["path"])));
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
        case "/api/pool/create" when request.HttpMethod == "POST":
          this._WriteJson(context, this._Create(request));
          break;
        case "/api/pool/recover" when request.HttpMethod == "POST":
          this._WriteJson(context, this._Recover(request));
          break;
        case "/api/pool/forget" when request.HttpMethod == "POST":
          this._WriteJson(context, this._Forget(request));
          break;
        case "/api/pool/duplication" when request.HttpMethod == "POST":
          this._WriteJson(context, this._SetDuplication(request));
          break;
        case "/api/pool/config" when request.HttpMethod == "GET":
          this._WriteJson(context, this._GetConfig(request));
          break;
        case "/api/pool/config" when request.HttpMethod == "POST":
          this._WriteJson(context, this._SetConfig(request));
          break;
        case "/api/prereqs":
          this._WriteJson(context, _PrereqPayload(Prerequisites.Check()));
          break;
        case "/api/prereqs/install" when request.HttpMethod == "POST":
          this._WriteJson(context, _Guard(() => {
            var (ok, message) = Prerequisites.Install();
            if (!ok)
              throw new ManifestException(message);

            return message;
          }));
          break;
        case "/api/pool/mount" when request.HttpMethod == "POST":
          this._WriteJson(context, this._Mount(request));
          break;
        case "/api/pool/unmount" when request.HttpMethod == "POST":
          this._WriteJson(context, this._Unmount(request));
          break;
        case "/api/pool/add-member" when request.HttpMethod == "POST":
          this._WriteJson(context, this._AddMember(request));
          break;
        case "/api/pool/member-role" when request.HttpMethod == "POST":
          this._WriteJson(context, _Guard(() => {
            var pool = this._Discover(this._RequirePool(request));
            var memberId = Guid.TryParse(request.QueryString["member"], out var id) ? id : throw new ManifestException("member-role needs ?member=<id>");
            lifecycle.SetMemberRole(pool.Manifest, memberId, _ParseRole(request.QueryString["role"]));
            return this._ApplyLive(pool);
          }));
          break;
        case "/api/pool/remove-member" when request.HttpMethod == "POST":
          this._WriteJson(context, _Guard(() => this._MediaOp(this._RequirePool(request),
            ["pool-remove-media", this._RequirePool(request), "--member", request.QueryString["member"] ?? ""])));
          break;
        case "/api/pool/replace-media" when request.HttpMethod == "POST":
          this._WriteJson(context, _Guard(() => this._MediaOp(this._RequirePool(request),
            ["pool-replace-media", this._RequirePool(request), "--old", request.QueryString["old"] ?? "", "--new", request.QueryString["new"] ?? ""])));
          break;
        case "/api/pool/delete" when request.HttpMethod == "POST":
          this._WriteJson(context, this._Delete(request, purge: false));
          break;
        case "/api/pool/purge" when request.HttpMethod == "POST":
          this._WriteJson(context, this._Delete(request, purge: true));
          break;
        case "/api/credential/set" when request.HttpMethod == "POST":
          this._WriteJson(context, this._CredentialSet(request));
          break;
        case "/api/credential/remove" when request.HttpMethod == "POST":
          this._WriteJson(context, _Guard(() => { credentials.Remove(request.QueryString["name"] ?? ""); return "ok"; }));
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

  /// <summary>Rebuilds the cached live frame once per second off the client path (never blocks a stream).</summary>
  private void _Sampler(ManualResetEventSlim stop) {
    while (!stop.IsSet) {
      try {
        this._frameJson = JsonSerializer.Serialize(this._BuildFrame(), _Json);
      } catch (Exception e) {
        DriveBender.Logger($"[Warning]dashboard sampler tick failed (last frame kept): {e.Message}");
      }

      stop.Wait(TimeSpan.FromSeconds(1));
    }
  }

  /// <summary>Discovery + health + per-member space, cached and refreshed slowly — the expensive drive walk runs at most every few seconds regardless of how many clients are connected.</summary>
  private IReadOnlyList<DiscoveredPool> _Discovery() {
    lock (this._discoLock)
      if (this._disco != null && DateTime.UtcNow - this._discoAtUtc < TimeSpan.FromSeconds(3))
        return this._disco;

    var built = provider.Discover().Select(pool => {
      var health = provider.Inspect(pool);
      var space = health.Members.ToDictionary(m => m.MemberId, m => this._MemberSpace(m));
      return new DiscoveredPool(pool, health, space);
    }).ToArray();

    lock (this._discoLock) {
      this._disco = built;
      this._discoAtUtc = DateTime.UtcNow;
    }

    return built;
  }

  /// <summary>Live pool DTOs: slowly-cached discovery merged with the mounted pool's own published snapshot (space + metrics).</summary>
  private object _BuildFrame() {
    var mounted = mountRegistry.List().ToDictionary(m => m.PoolId);
    return new {
      pools = this._Discovery().Select(d => {
        var (pool, health, discoveredSpace) = d;
        var snapshot = mounted.ContainsKey(pool.PoolId) ? this._metrics.TryRead(pool.PoolId) : null;
        // mounted → the POOL reports each storage's live space; unmounted → the slow discovery probe
        var liveSpace = snapshot?.MemberSpace.ToDictionary(s => s.MemberId) ?? [];
        return new {
          id = pool.PoolId,
          name = pool.Name,
          source = pool.IsVirtual ? "native" : "manifest",
          degraded = health.IsDegraded,
          mounted = mounted.TryGetValue(pool.PoolId, out var entry) ? entry.Target : null,
          configuredTarget = pool.Manifest.Mount?.Target,
          duplication = _DuplicationOf(pool.Manifest),
          allowSamePhysical = _AllowSamePhysicalOf(pool.Manifest),
          autoLandingZone = _PlacementFlagOf(pool.Manifest, "autoLandingZone"),
          placementStrategy = _PlacementStringOf(pool.Manifest, "strategy") ?? "most-free-space",
          bytesFree = snapshot?.BytesFree ?? health.BytesFree,
          bytesTotal = snapshot?.BytesTotal ?? health.BytesTotal,
          failureDomains = health.IndependentFailureDomains,
          warnings = health.Warnings,
          members = health.Members.Select(m => {
            var hasLive = liveSpace.TryGetValue(m.MemberId, out var live);
            var free = hasLive ? live!.BytesFree : discoveredSpace.TryGetValue(m.MemberId, out var f) ? f.free : 0;
            var total = hasLive ? live!.BytesTotal : discoveredSpace.TryGetValue(m.MemberId, out var t) ? t.total : 0;
            return new {
              id = m.MemberId,
              path = m.ResolvedPath,
              label = m.Label,
              role = m.Role.ToString().ToLowerInvariant(),
              online = m.Online,
              network = m.Network,
              bytesFree = free,
              bytesTotal = total,
            };
          }),
          metrics = snapshot == null ? null : new {
            snapshot.ReadBytes,
            snapshot.WrittenBytes,
            snapshot.CacheHitRate,
            snapshot.DirtyFiles,
            snapshot.DrainedFiles,
            snapshot.CacheReadUsedBytes,
            snapshot.CacheReadMaxBytes,
            snapshot.CacheWriteUsedBytes,
            snapshot.CacheWriteMaxBytes,
            memberLatencies = snapshot.MemberLatencies,
            activity = snapshot.RecentActivity,
          },
        };
      }),
      stampUtc = DateTime.UtcNow.ToString("O"),
    };
  }

  /// <summary>Invalidates the discovery cache so the next sample reflects a create/mount/delete immediately.</summary>
  private void _InvalidateDiscovery() {
    lock (this._discoLock)
      this._discoAtUtc = DateTime.MinValue;
  }

  /// <summary>
  /// Read-only directory listing that backs the web folder picker — a browser can't open a
  /// native dialog, so the localhost daemon enumerates folders for it. No path (or an
  /// unreadable one) lists the machine's volume roots; only folder names are returned.
  /// </summary>
  private object _FsList(HttpListenerRequest request) {
    var path = request.QueryString["path"];
    if (string.IsNullOrWhiteSpace(path) || !host.DirectoryExists(path))
      return new { path = (string?)null, parent = (string?)null, dirs = _DirEntries(host.EnumerateVolumeRoots()) };

    var parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    string[] children;
    try {
      children = host.EnumerateDirectories(path).ToArray();
    } catch (Exception) {
      children = []; // unreadable folder — show it empty rather than failing the picker
    }

    return new { path, parent = string.IsNullOrEmpty(parent) ? null : parent, dirs = _DirEntries(children) };
  }

  private static object[] _DirEntries(IEnumerable<string> paths)
    => paths.Select(p => new { name = _LeafName(p), path = p }).OrderBy(d => d.name, StringComparer.OrdinalIgnoreCase).ToArray();

  private static string _LeafName(string path) {
    var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var leaf = Path.GetFileName(trimmed);
    return leaf.Length > 0 ? leaf : path; // volume roots (e.g. "C:\") have no leaf — show the root itself
  }

  /// <summary>Free/total bytes of the volume under a member (0/0 when offline, remote or unprobeable).</summary>
  private (long free, long total) _MemberSpace(PoolMember member) {
    if (!member.Online || member.Network)
      return (0, 0);

    try {
      var identity = host.GetVolumeIdentity(member.ResolvedPath);
      return (identity.BytesFree, identity.BytesTotal);
    } catch (Exception) {
      return (0, 0);
    }
  }

  /// <summary>The pool-wide duplication level from the manifest defaults (1 when unset).</summary>
  private static int _DuplicationOf(PoolManifest manifest) {
    if (manifest.Defaults is { ValueKind: JsonValueKind.Object } defaults
        && defaults.TryGetProperty("duplication", out var d) && d.ValueKind == JsonValueKind.Number)
      return d.GetInt32();

    return 1;
  }

  /// <summary>Whether the pool opted into co-locating copies on one disk (placement.shadowNeverSamePhysical = false).</summary>
  private static bool _AllowSamePhysicalOf(PoolManifest manifest)
    => manifest.Defaults is { ValueKind: JsonValueKind.Object } defaults
       && defaults.TryGetProperty("placement", out var p) && p.ValueKind == JsonValueKind.Object
       && p.TryGetProperty("shadowNeverSamePhysical", out var s) && s.ValueKind == JsonValueKind.False;

  /// <summary>A boolean flag from the manifest's placement block (e.g. autoLandingZone).</summary>
  private static bool _PlacementFlagOf(PoolManifest manifest, string flag)
    => manifest.Defaults is { ValueKind: JsonValueKind.Object } defaults
       && defaults.TryGetProperty("placement", out var p) && p.ValueKind == JsonValueKind.Object
       && p.TryGetProperty(flag, out var f) && f.ValueKind == JsonValueKind.True;

  /// <summary>A string value from the manifest's placement block (e.g. strategy).</summary>
  private static string? _PlacementStringOf(PoolManifest manifest, string key)
    => manifest.Defaults is { ValueKind: JsonValueKind.Object } defaults
       && defaults.TryGetProperty("placement", out var p) && p.ValueKind == JsonValueKind.Object
       && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
      ? v.GetString()
      : null;

  private void _Stream(HttpListenerContext context) {
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.Add("Cache-Control", "no-cache");
    context.Response.SendChunked = true;
    try {
      using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false));
      writer.Write("retry: 1000\n\n"); // reconnect within 1s if the daemon restarts (stable URL)
      writer.Flush();
      try {
        // ships the SAMPLER'S cached frame — this loop never collects, so it can never stall; a
        // heartbeat comment keeps the connection alive even if the sampler pauses on a slow drive
        var lastSent = "";
        while (true) {
          var frame = this._frameJson;
          if (!ReferenceEquals(frame, lastSent) && frame != lastSent) {
            writer.Write($"data: {frame}\n\n");
            lastSent = frame;
          } else
            writer.Write(": ping\n\n");

          writer.Flush(); // throws once the client is gone → ends the stream
          Thread.Sleep(1000); // 1 Hz live feed (NFR-UI-LIVE)
        }
      } catch (Exception) {
        // client disconnected — normal
      }
    } catch (Exception) {
      // response already torn down; nothing to clean up
    }
  }

  /// <summary>
  /// Pool work never runs inside the manager (it is a reload-safe UI shell): a MOUNTED pool
  /// executes the operation in its own process via the op channel; an unmounted pool gets a
  /// TRANSIENT worker process. Either way, a manager reload cannot kill pool work.
  /// </summary>
  private object _PoolOp(string poolRef, string op, string[] workerArgs) => _Guard(() => {
    var pool = this._Discover(poolRef);
    string? json;
    if (mountRegistry.Find(pool.PoolId.ToString()) != null) {
      var id = Guid.NewGuid().ToString("N");
      mountRegistry.RequestOp(pool.PoolId, id, op);
      json = mountRegistry.WaitOpResult(pool.PoolId, id, TimeSpan.FromMinutes(30))
             ?? throw new ManifestException("the pool's process did not answer — check the mount");
    } else {
      json = _RunWorker(workerArgs)
             ?? throw new ManifestException("the worker process failed — check the daemon log");
    }

    using var document = JsonDocument.Parse(json);
    if (document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
      throw new ManifestException(document.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "pool operation failed" : "pool operation failed");

    return document.RootElement.Clone();
  });

  /// <summary>Runs the problem scan (optionally correcting) — relayed to the pool's process or a transient worker.</summary>
  private object _RunHealth(HttpListenerRequest request) {
    var poolRef = this._RequirePool(request);
    var fix = request.QueryString["fix"] == "true";
    return this._PoolOp(poolRef, fix ? "fix" : "health",
      fix ? ["pool-health", poolRef, "--fix", "--json"] : ["pool-health", poolRef, "--json"]);
  }

  private object _RunRestore(HttpListenerRequest request) {
    var poolRef = this._RequirePool(request);
    return this._PoolOp(poolRef, "restore", ["pool-restore", poolRef, "--json"]);
  }

  /// <summary>
  /// Media surgery (scatter-remove / replace) runs in a transient worker process, never in the
  /// manager — and never concurrently with the pool's own engine: a mounted pool must be
  /// unmounted first (two engines over one member set would race).
  /// </summary>
  private object _MediaOp(string poolRef, string[] workerArgs) {
    var pool = this._Discover(poolRef);
    if (mountRegistry.Find(pool.PoolId.ToString()) != null)
      throw new ManifestException("unmount the pool first — media operations rearrange the members' files and must not race the live mount");

    var output = _RunWorkerText(workerArgs, out var exitCode);
    if (exitCode != 0)
      throw new ManifestException(output.Length > 0 ? output : "the media operation failed — check the daemon log");

    return "ok";
  }

  /// <summary>Spawns a short-lived dbmount worker and returns its full output + exit code.</summary>
  private static string _RunWorkerText(string[] args, out int exitCode) {
    exitCode = -1;
    var entryDll = Assembly.GetEntryAssembly()!.Location;
    var exe = Environment.ProcessPath ?? "dotnet";
    var viaDotnet = exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    var start = new System.Diagnostics.ProcessStartInfo {
      FileName = exe,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    if (viaDotnet)
      start.ArgumentList.Add(entryDll);
    foreach (var argument in args)
      start.ArgumentList.Add(argument);

    try {
      using var worker = System.Diagnostics.Process.Start(start);
      if (worker == null)
        return "";

      var stdout = worker.StandardOutput.ReadToEnd();
      var stderr = worker.StandardError.ReadToEnd();
      worker.WaitForExit(30 * 60 * 1000);
      exitCode = worker.ExitCode;
      return (stderr.Trim().Length > 0 ? stderr : stdout).Trim();
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]worker spawn failed: {e.Message}");
      return e.Message;
    }
  }

  /// <summary>Spawns a short-lived dbmount worker and returns the JSON line it printed (null on failure).</summary>
  private static string? _RunWorker(string[] args) {
    var entryDll = Assembly.GetEntryAssembly()!.Location;
    var exe = Environment.ProcessPath ?? "dotnet";
    var viaDotnet = exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    var start = new System.Diagnostics.ProcessStartInfo {
      FileName = exe,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    if (viaDotnet)
      start.ArgumentList.Add(entryDll);
    foreach (var argument in args)
      start.ArgumentList.Add(argument);

    try {
      using var worker = System.Diagnostics.Process.Start(start);
      if (worker == null)
        return null;

      var stdout = worker.StandardOutput.ReadToEnd();
      worker.StandardError.ReadToEnd();
      worker.WaitForExit(30 * 60 * 1000);
      // the JSON result is the last line that looks like an object
      return stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]worker spawn failed: {e.Message}");
      return null;
    }
  }

  private sealed record CreateBody(string? Name, string? MountTarget, MemberBody[]? Members, bool TakeOver = false);
  private sealed record MemberBody(string Location, string? Role, string? Credential);
  private sealed record PathBody(string? Path);

  private object _Create(HttpListenerRequest request) => _Guard(() => {
    var body = _ReadBody<CreateBody>(request);
    if (body?.Name == null || body.Members == null || body.Members.Length == 0)
      throw new ManifestException("create needs a name and at least one member");

    var specs = body.Members.Select(m => new PoolLifecycle.MemberSpec(
      m.Location,
      _ParseRole(m.Role),
      Credential: string.IsNullOrWhiteSpace(m.Credential) ? null : "cred-ref:" + CredentialStore.NormalizeReference(m.Credential!),
      Network: MemberSchemes.IsRemote(MemberSchemes.SchemeOf(null, m.Location))));

    var manifest = lifecycle.Create(body.Name, specs, string.IsNullOrWhiteSpace(body.MountTarget) ? null : body.MountTarget, takeOver: body.TakeOver);
    return new { manifest.PoolId, manifest.Name };
  });

  private object _AddMember(HttpListenerRequest request) => _Guard(() => {
    var body = _ReadBody<MemberBody>(request);
    var pool = this._Discover(this._RequirePool(request));
    if (body == null)
      throw new ManifestException("add-member needs a member body");

    var credential = string.IsNullOrWhiteSpace(body.Credential) ? null : "cred-ref:" + CredentialStore.NormalizeReference(body.Credential!);
    lifecycle.AddMember(pool.Manifest, new(body.Location, _ParseRole(body.Role), Credential: credential), takeOver: request.QueryString["takeover"] == "true");
    return "ok";
  });

  /// <summary>Rebuilds an orphaned pool from a member folder's manifest mirror and re-registers it.</summary>
  private object _Recover(HttpListenerRequest request) => _Guard(() => {
    var path = _ReadBody<PathBody>(request)?.Path ?? request.QueryString["path"];
    if (string.IsNullOrWhiteSpace(path))
      throw new ManifestException("recover needs a member folder path");

    var manifest = lifecycle.Recover(path);
    return new { manifest.PoolId, manifest.Name };
  });

  private sealed record DuplicationBody(int? Level, string? Folder, bool? AllowSamePhysical, string? Strategy);
  private sealed record ConfigBody(string? Json);

  /// <summary>Sets pool-wide (or per-folder) duplication and the primary-placement strategy; applied live when mounted.</summary>
  private object _SetDuplication(HttpListenerRequest request) => _Guard(() => {
    var body = _ReadBody<DuplicationBody>(request);
    var pool = this._Discover(this._RequirePool(request));
    if (body?.Level == null)
      throw new ManifestException("duplication needs a level (1-10)");

    lifecycle.SetDuplication(pool.Manifest, body.Level.Value, string.IsNullOrWhiteSpace(body.Folder) ? null : body.Folder, body.AllowSamePhysical, body.Strategy);
    return this._ApplyLive(pool);
  });

  /// <summary>After a settings change: asks a running mount to reload live; otherwise it lands on the next mount.</summary>
  private string _ApplyLive(PoolRef pool) {
    if (mountRegistry.Find(pool.PoolId.ToString()) == null)
      return "saved — takes effect on the next mount";

    mountRegistry.RequestReload(pool.PoolId);
    return "saved — applying live to the running mount (owed copies are being created in the background)";
  }

  /// <summary>Returns the pool's current settings block plus the built-in defaults as a template.</summary>
  private object _GetConfig(HttpListenerRequest request) => _Guard(() => {
    var pool = this._Discover(this._RequirePool(request));
    var current = pool.Manifest.Defaults is { ValueKind: JsonValueKind.Object } d ? d.GetRawText() : "";
    return new { current, template = ConfigResolver.BuiltInDefaultsJson };
  });

  /// <summary>Validates and stores the pool's whole settings block (the settings editor); next-mount effective.</summary>
  private object _SetConfig(HttpListenerRequest request) => _Guard(() => {
    var body = _ReadBody<ConfigBody>(request);
    var pool = this._Discover(this._RequirePool(request));
    if (string.IsNullOrWhiteSpace(body?.Json))
      throw new ManifestException("settings need a JSON object");

    lifecycle.SetConfig(pool.Manifest, body.Json);
    return this._ApplyLive(pool);
  });

  /// <summary>Removes a pool from this machine's registry only — data and on-media markers stay.</summary>
  private object _Forget(HttpListenerRequest request) => _Guard(() => {
    var pool = this._Discover(this._RequirePool(request));
    if (mountRegistry.Find(pool.PoolId.ToString()) != null)
      throw new ManifestException("unmount the pool before forgetting it");

    lifecycle.Forget(pool.Manifest);
    return "forgotten";
  });

  private object _Mount(HttpListenerRequest request) => _Guard(() => {
    var pool = this._Discover(this._RequirePool(request));
    if (mountRegistry.Find(pool.PoolId.ToString()) != null)
      return "already mounted";

    // don't launch a doomed child — check the driver first and tell the UI what to do
    var prereq = Prerequisites.Check();
    if (!prereq.Ok)
      throw new PrereqException(prereq);

    var target = request.QueryString["target"] ?? pool.Manifest.Mount?.Target;
    if (string.IsNullOrWhiteSpace(target))
      throw new ManifestException("No mount target — choose a drive letter (e.g. X:) or an empty folder to mount at.");

    mountRegistry.TakeError(pool.PoolId); // discard any stale failure report from a previous attempt
    var process = _LaunchMount(pool.PoolId, target);

    // wait for the mount to actually appear (or the child to fail) so "Mount" reports the truth
    for (var i = 0; i < 40; ++i) {
      if (mountRegistry.Find(pool.PoolId.ToString()) != null)
        return "mounted";

      if (process is { HasExited: true }) {
        // prefer the child's own reported reason, then its captured stderr
        var reported = mountRegistry.TakeError(pool.PoolId);
        if (reported == null) {
          Thread.Sleep(250); // give a crashing child a moment to flush its report
          reported = mountRegistry.TakeError(pool.PoolId);
        }

        var error = process.StartInfo.RedirectStandardError ? process.StandardError.ReadToEnd().Trim() : "";
        throw new ManifestException(
          !string.IsNullOrEmpty(reported) ? reported
          : error.Length > 0 ? error
          : "The mount process exited before the pool came up. Check that a filesystem driver is installed.");
      }

      Thread.Sleep(250);
    }

    return "mounting"; // still coming up (large pools / remote members can take a moment)
  });

  private object _Unmount(HttpListenerRequest request) => _Guard(() => {
    var entry = mountRegistry.Find(this._RequirePool(request)) ?? throw new ManifestException("pool is not mounted");
    mountRegistry.RequestStop(entry.PoolId); // the mount process flushes and detaches cleanly
    return "unmounting";
  });

  private object _Delete(HttpListenerRequest request, bool purge) => _Guard(() => {
    var pool = this._Discover(this._RequirePool(request));
    if (mountRegistry.Find(pool.PoolId.ToString()) != null)
      throw new ManifestException("unmount the pool before deleting it");

    lifecycle.Delete(pool.Manifest, purgeData: purge);
    return purge ? "purged" : "deleted";
  });

  private object _CredentialSet(HttpListenerRequest request) => _Guard(() => {
    var body = _ReadBody<CredentialBody>(request);
    if (body?.Name == null || string.IsNullOrEmpty(body.Secret))
      throw new ManifestException("credential set needs a name and secret");

    credentials.Store(body.Name, body.User ?? "", body.Secret);
    return "ok";
  });

  private sealed record CredentialBody(string? Name, string? User, string? Secret);

  private static MemberRole _ParseRole(string? role) => role?.ToLowerInvariant() switch {
    "landing" => MemberRole.Landing,
    "readonly" => MemberRole.ReadOnly,
    _ => MemberRole.Capacity,
  };

  private PoolRef _Discover(string nameOrId) {
    var pools = provider.Discover();
    var pool = Guid.TryParse(nameOrId, out var id)
      ? pools.FirstOrDefault(p => p.PoolId == id)
      : pools.FirstOrDefault(p => p.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
    return pool ?? throw new ManifestException($"no pool '{nameOrId}'");
  }

  /// <summary>
  /// Launches a detached <c>dbmount mount</c> child so the daemon never holds the mount itself.
  /// The child runs in the SAME (interactive, non-elevated) session as the daemon on purpose:
  /// mounting a WinFsp/Dokan/FUSE volume doesn't need elevation, and a drive letter created by an
  /// elevated process lives in a different logon session than the user's Explorer — so it would
  /// mount but never appear. Only installing the driver needs elevation (handled separately).
  /// </summary>
  private static System.Diagnostics.Process? _LaunchMount(Guid poolId, string? target) {
    var entryDll = Assembly.GetEntryAssembly()!.Location;
    var exe = Environment.ProcessPath ?? "dotnet";
    var viaDotnet = exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    var start = new System.Diagnostics.ProcessStartInfo {
      FileName = exe,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardError = true,
    };
    if (viaDotnet)
      start.ArgumentList.Add(entryDll);
    start.ArgumentList.Add("mount");
    start.ArgumentList.Add("--manifest");
    start.ArgumentList.Add(poolId.ToString());
    if (!string.IsNullOrWhiteSpace(target)) {
      start.ArgumentList.Add("--target");
      start.ArgumentList.Add(target);
    }

    return System.Diagnostics.Process.Start(start);
  }

  private static T? _ReadBody<T>(HttpListenerRequest request) {
    using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
    var json = reader.ReadToEnd();
    return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, _Json);
  }

  private string _RequirePool(HttpListenerRequest request)
    => request.QueryString["pool"] ?? throw new ManifestException("missing ?pool=");

  private static object _Guard(Func<object> action) {
    try {
      return new { ok = true, result = action() };
    } catch (PrereqException e) {
      return new { ok = false, error = e.Message, needsPrereq = true, driver = e.Status.Driver, installable = e.Status.Installable };
    } catch (MemberClaimConflictException e) {
      return new { ok = false, error = e.Message, conflict = new { path = e.Path, poolId = e.ConflictPoolId, restorable = e.Restorable, registered = e.Registered } };
    } catch (Exception e) {
      return new { ok = false, error = e.Message };
    }
  }

  private void _WriteJson(HttpListenerContext context, object payload) {
    // any mutating API result invalidates the discovery cache so a create/mount/delete/role
    // change shows up on the very next sample instead of up to 3s later
    this._InvalidateDiscovery();
    this._WriteRawJson(context, JsonSerializer.Serialize(payload, _Json));
  }

  private void _WriteRawJson(HttpListenerContext context, string json) {
    var bytes = Encoding.UTF8.GetBytes(json);
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

  private static object _PrereqPayload(PrereqStatus status)
    => new { status.Ok, status.Driver, status.Detail, status.Installable, status.NeedsElevation };

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
