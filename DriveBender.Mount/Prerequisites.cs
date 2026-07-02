using System.Diagnostics;

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

  /// <summary>Attempts to install the driver via the platform package manager; returns a human-readable result.</summary>
  public static (bool ok, string message) Install() {
#if WINDOWS
    // WinFsp via winget (the lightest path); the user still confirms the UAC/installer prompt
    if (_TryRun("winget", "install --id WinFsp.WinFsp -e --silent --accept-package-agreements --accept-source-agreements", out var output))
      return Windows.WinFspMountHost.IsWinFspAvailable()
        ? (true, "WinFsp installed. You can mount now.")
        : (false, "winget ran but WinFsp is still not detected — you may need to reopen the app, or install it manually from https://winfsp.dev.\n" + output);

    return (false, "Could not run winget automatically. Install WinFsp from https://winfsp.dev (or Dokan from https://dokan-dev.github.io) and retry.\n" + output);
#else
    // Linux: pick the distro package manager; needs a password-less sudo or a root daemon
    foreach (var (tool, args) in new[] {
      ("apt-get", "install -y fuse3"),
      ("dnf", "install -y fuse"),
      ("pacman", "-S --noconfirm fuse3"),
    }) {
      if (!_Exists(tool))
        continue;

      var command = IsElevated ? (tool, args) : ("sudo", $"-n {tool} {args}");
      if (_TryRun(command.Item1, command.Item2, out var output))
        return Linux.LinuxFuseMountHost.IsFuseAvailable()
          ? (true, "FUSE installed. You can mount now.")
          : (false, $"{tool} ran but /dev/fuse is still missing.\n{output}");

      return (false, $"Could not install fuse3 automatically (needs root). Run: sudo {tool} {args}\n{output}");
    }

    return (false, "No supported package manager found. Install the fuse3 package for your distro and retry.");
#endif
  }

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
