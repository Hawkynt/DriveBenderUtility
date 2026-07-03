using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace DivisonM.Mount;

/// <summary>Result of a filesystem-driver prerequisite check.</summary>
public sealed record PrereqStatus(bool Ok, string Driver, string Detail, bool Installable, bool NeedsElevation);

/// <summary>
/// Detects and (best-effort) installs the platform filesystem driver a mount needs —
/// WinFsp/Dokan on Windows, FUSE on Linux — and reports whether the process can actually
/// mount (Windows mounting needs elevation). Keeps "click Mount and nothing happens" from
/// being silent: the daemon checks this first and tells the UI what to do.
/// </summary>
internal static class Prerequisites {

  // Install the exact WinFsp release the bundled winfsp.net binding was built against — its
  // CheckVersion() demands a matching major.minor native runtime, so installing "latest" (which
  // may be a newer/older line) would crash the mount with "incorrect dll version". Keep in
  // lockstep with the winfsp.net PackageReference in DriveBender.Mount.Windows.csproj.
  private const string _WINFSP_TAG = "v2.1";

  public static bool IsElevated {
    get {
      try {
        return Environment.IsPrivilegedProcess;
      } catch (Exception) {
        return false;
      }
    }
  }

  public static PrereqStatus Check() {
#if WINDOWS
    var winfsp = Windows.WinFspMountHost.IsWinFspAvailable();
    var dokan = Windows.DokanMountHost.IsDokanAvailable();
    if (winfsp || dokan)
      return new(true, winfsp ? "WinFsp" : "Dokan", $"{(winfsp ? "WinFsp" : "Dokan")} is installed.", false, !IsElevated);

    return new(false, "WinFsp/Dokan", "No filesystem driver (WinFsp or Dokan) is installed.", true, false);
#else
    if (Linux.LinuxFuseMountHost.IsFuseAvailable())
      return new(true, "FUSE", "FUSE is available.", false, false);

    return new(false, "FUSE (fuse3)", "FUSE is not available (/dev/fuse missing).", true, false);
#endif
  }

  /// <summary>Attempts to install the driver directly; returns a human-readable result.</summary>
  public static (bool ok, string message) Install() {
#if WINDOWS
    // Download and run the official WinFsp MSI straight from its GitHub release — no dependency on
    // winget being present. msiexec is launched elevated (mounting/installing a driver needs admin);
    // the user still sees the standard UAC + installer prompts.
    if (!_TryDownloadWinFspMsi(out var msi, out var dlError))
      return (false, "Could not download the WinFsp installer.\n" + dlError
        + "\nInstall it manually from https://winfsp.dev and retry.");

    var runasVerb = IsElevated ? null : "runas";
    if (!_TryRunShell("msiexec", $"/i \"{msi}\" /passive /norestart", runasVerb, out var output))
      return (false, "The WinFsp installer did not complete (the elevation prompt may have been declined).\n" + output
        + "\nYou can also install it manually from https://winfsp.dev.");

    return Windows.WinFspMountHost.IsWinFspAvailable()
      ? (true, "WinFsp installed. You can mount now.")
      : (false, "The installer ran but WinFsp is still not detected — reopen the app and retry. If it persists, install manually from https://winfsp.dev.");
#else
    // Linux: fuse3 is a system package, so run the distro's own installer directly. We probe absolute
    // paths (not just $PATH via `which`, which a sandboxed daemon may lack) across the major managers.
    foreach (var (tool, args) in new[] {
      ("apt-get", "install -y fuse3"),
      ("dnf", "install -y fuse3"),
      ("yum", "install -y fuse3"),
      ("zypper", "--non-interactive install fuse3"),
      ("pacman", "-S --noconfirm fuse3"),
      ("apk", "add fuse3"),
      ("xbps-install", "-y fuse"),
      ("emerge", "sys-fs/fuse"),
      ("eopkg", "-y install fuse3"),
    }) {
      var resolved = _Resolve(tool);
      if (resolved == null)
        continue;

      var (file, fullArgs) = IsElevated ? (resolved, args) : ("sudo", $"-n {resolved} {args}");
      if (_TryRun(file, fullArgs, out var output))
        return Linux.LinuxFuseMountHost.IsFuseAvailable()
          ? (true, "FUSE installed. You can mount now.")
          : (false, $"{tool} ran but /dev/fuse is still missing.\n{output}");

      return (false, $"Could not install fuse3 automatically (needs root). Run: sudo {tool} {args}\n{output}");
    }

    return (false, "No supported package manager found (apt/dnf/yum/zypper/pacman/apk/xbps/emerge/eopkg). Install the fuse3 package for your distro and retry.");
#endif
  }

#if WINDOWS
  /// <summary>Fetches the newest WinFsp release's .msi from GitHub into a temp file.</summary>
  private static bool _TryDownloadWinFspMsi(out string path, out string error) {
    path = "";
    error = "";
    try {
      using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
      http.DefaultRequestHeaders.UserAgent.ParseAdd("DriveBenderUtility");

      var releaseJson = http.GetStringAsync($"https://api.github.com/repos/winfsp/winfsp/releases/tags/{_WINFSP_TAG}").GetAwaiter().GetResult();
      using var doc = JsonDocument.Parse(releaseJson);
      var url = doc.RootElement.GetProperty("assets").EnumerateArray()
        .Select(a => a.GetProperty("browser_download_url").GetString())
        .FirstOrDefault(u => u != null && u.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
      if (url == null) {
        error = "No .msi asset found in the latest WinFsp release.";
        return false;
      }

      var target = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(url).LocalPath));
      using (var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()) {
        response.EnsureSuccessStatusCode();
        using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var output = File.Create(target);
        input.CopyTo(output);
      }

      path = target;
      return true;
    } catch (Exception e) {
      error = e.Message;
      return false;
    }
  }

  /// <summary>Runs a command via ShellExecute so an optional runas verb can trigger UAC; waits for exit.</summary>
  private static bool _TryRunShell(string file, string args, string? verb, out string output) {
    output = "";
    try {
      var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
      if (verb != null)
        psi.Verb = verb;

      using var process = Process.Start(psi);
      if (process == null)
        return false;

      process.WaitForExit(600_000);
      return process.ExitCode == 0;
    } catch (Exception e) {
      output = e.Message;
      return false;
    }
  }
#else
  /// <summary>Resolves a tool to an absolute path, probing common bin dirs so a limited $PATH doesn't hide it.</summary>
  private static string? _Resolve(string tool) {
    if (_Exists(tool))
      return tool;

    foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin", "/usr/local/sbin" }) {
      var candidate = Path.Combine(dir, tool);
      if (File.Exists(candidate))
        return candidate;
    }

    return null;
  }
#endif

  private static bool _TryRun(string file, string args, out string output) {
    output = "";
    try {
      using var process = Process.Start(new ProcessStartInfo(file, args) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      });
      if (process == null)
        return false;

      output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
      process.WaitForExit(300_000);
      return process.ExitCode == 0;
    } catch (Exception e) {
      output = e.Message;
      return false;
    }
  }

  private static bool _Exists(string tool) {
    try {
      using var process = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "where" : "which", tool) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      });
      process!.WaitForExit(5000);
      return process.ExitCode == 0;
    } catch (Exception) {
      return false;
    }
  }

}
