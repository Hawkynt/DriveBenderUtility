using System.Diagnostics;
using Photino.NET;

namespace DivisonM.App;

/// <summary>
/// The desktop UI is a thin cross-platform shell around the same web UI the daemon serves
/// (§6.13): it launches <c>dbmount serve</c>, reads the tokenised localhost URL it prints,
/// and hosts it in a native WebView (Photino → WebView2 on Windows, WebKitGTK on Linux) so
/// the desktop and browser experiences are identical. Closing the window stops the daemon.
/// </summary>
internal static class Program {

  private static Process? _daemon;
  private static volatile bool _closing;

  [STAThread]
  private static int Main(string[] args) {
    var dbmount = _LocateDbmount();
    if (dbmount == null) {
      Console.Error.WriteLine("The 'dbmount' tool was not found next to this app. Build/install DriveBender.Mount alongside it.");
      return 1;
    }

    var url = _LaunchDaemon(dbmount);
    if (url == null) {
      Console.Error.WriteLine("The management daemon did not report a URL in time.");
      _Stop();
      return 2;
    }

    var window = new PhotinoWindow()
      .SetTitle("Drive Bender Pool Manager")
      .SetUseOsDefaultSize(false)
      .SetSize(1180, 760)
      .Center()
      .SetResizable(true)
      .Load(new Uri(url));

    // supervise the daemon: if it ever dies while the window is open, relaunch it and point the
    // WebView at the fresh URL (new port + token) — otherwise the UI is stuck at "reconnecting…"
    new Thread(() => {
      while (!_closing) {
        try {
          _daemon?.WaitForExit();
        } catch (Exception) {
          // process handle gone — treat as exited
        }

        if (_closing)
          return;

        var fresh = _LaunchDaemon(dbmount);
        if (fresh == null) {
          Thread.Sleep(3000); // relaunch failed (port race / transient) — retry
          continue;
        }

        try {
          window.Invoke(() => window.Load(new Uri(fresh)));
        } catch (Exception) {
          // window is closing
          return;
        }
      }
    }) { IsBackground = true, Name = "daemon-supervisor" }.Start();

    try {
      window.WaitForClose();
    } finally {
      _closing = true;
      _Stop();
    }

    return 0;
  }

  /// <summary>Starts <c>dbmount serve</c> and returns its tokenised URL (null on timeout).</summary>
  private static string? _LaunchDaemon(string dbmount) {
    var port = _FreeLoopbackPort();
    var daemon = new Process {
      StartInfo = new(dbmount.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : dbmount) {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      },
    };
    if (dbmount.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
      daemon.StartInfo.ArgumentList.Add(dbmount);
    daemon.StartInfo.ArgumentList.Add("serve");
    daemon.StartInfo.ArgumentList.Add("--port");
    daemon.StartInfo.ArgumentList.Add(port.ToString());

    string? url = null;
    using var ready = new ManualResetEventSlim();
    daemon.OutputDataReceived += (_, e) => {
      if (e.Data == null)
        return;

      var marker = e.Data.IndexOf("http://", StringComparison.Ordinal);
      if (marker >= 0 && url == null) {
        url = e.Data[marker..].Trim();
        ready.Set();
      }
    };

    try {
      daemon.Start();
    } catch (Exception) {
      return null;
    }

    daemon.BeginOutputReadLine();
    _daemon = daemon;
    return ready.Wait(TimeSpan.FromSeconds(15)) ? url : null;
  }

  private static void _Stop() {
    try {
      // kill ONLY the daemon — never the whole tree: mount children it spawned must keep serving
      // their drives after the manager window closes (unmount is an explicit user action)
      if (_daemon is { HasExited: false })
        _daemon.Kill(entireProcessTree: false);
    } catch (InvalidOperationException) {
      // already gone
    }
  }

  /// <summary>Finds dbmount(.exe/.dll) next to this app or in a sibling build output.</summary>
  private static string? _LocateDbmount() {
    var names = new[] { "dbmount.exe", "dbmount", "dbmount.dll" };
    var appDir = AppContext.BaseDirectory;
    foreach (var name in names) {
      var candidate = Path.Combine(appDir, name);
      if (File.Exists(candidate))
        return candidate;
    }

    var dir = new DirectoryInfo(appDir);
    for (var i = 0; i < 6 && dir != null; ++i, dir = dir.Parent) {
      var mountBin = Path.Combine(dir.FullName, "DriveBender.Mount", "bin");
      if (!Directory.Exists(mountBin))
        continue;

      var candidates = names.SelectMany(name => _SafeFind(mountBin, name)).ToArray();
      if (candidates.Length == 0)
        continue;

      // pick the build matching this OS: dbmount is per-TFM (net10.0-windows carries the
      // WinFsp/Dokan code, net10.0 the FUSE code). Launching the wrong one is why an
      // install can report "no package manager found" on Windows.
      var wantsWindows = OperatingSystem.IsWindows();
      return candidates.OrderByDescending(p => p.Contains("windows", StringComparison.OrdinalIgnoreCase) == wantsWindows).First();
    }

    return null;
  }

  private static IEnumerable<string> _SafeFind(string root, string pattern) {
    try {
      return Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
    } catch {
      return [];
    }
  }

  private static int _FreeLoopbackPort() {
    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
    socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
    return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
  }

}
