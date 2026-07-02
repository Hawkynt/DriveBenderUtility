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

  [STAThread]
  private static int Main(string[] args) {
    var dbmount = _LocateDbmount();
    if (dbmount == null) {
      Console.Error.WriteLine("The 'dbmount' tool was not found next to this app. Build/install DriveBender.Mount alongside it.");
      return 1;
    }

    var port = _FreeLoopbackPort();
    using var daemon = new Process {
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

    daemon.Start();
    daemon.BeginOutputReadLine();
    if (!ready.Wait(TimeSpan.FromSeconds(15)) || url == null) {
      Console.Error.WriteLine("The management daemon did not report a URL in time.");
      _Stop(daemon);
      return 2;
    }

    try {
      new PhotinoWindow()
        .SetTitle("Drive Bender Pool Manager")
        .SetUseOsDefaultSize(false)
        .SetSize(1180, 760)
        .Center()
        .SetResizable(true)
        .Load(new Uri(url))
        .WaitForClose();
    } finally {
      _Stop(daemon);
    }

    return 0;
  }

  private static void _Stop(Process daemon) {
    try {
      if (!daemon.HasExited)
        daemon.Kill(entireProcessTree: true);
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

      foreach (var name in new[] { "dbmount.dll", "dbmount.exe" }) {
        var hit = Directory.GetFiles(mountBin, name, SearchOption.AllDirectories).FirstOrDefault();
        if (hit != null)
          return hit;
      }
    }

    return null;
  }

  private static int _FreeLoopbackPort() {
    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
    socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
    return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
  }

}
