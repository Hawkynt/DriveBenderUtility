using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests.TestSupport;

/// <summary>
/// Whether this machine can actually mount a filesystem.
///
/// Checked INDEPENDENTLY of the product rather than by asking it: the product's own availability
/// probe is one of the things under test, and it has already been wrong once — reporting WinFsp
/// present from files on disk while the mount then failed.
///
/// The distinction that matters is between "skip, this developer box has no driver" and "fail,
/// CI was supposed to have installed one". A suite that silently skips everything reports the
/// same green as a suite that passed, which would make the whole exercise worthless. CI sets
/// <c>DBE2E_REQUIRE_DRIVER=1</c>, and then a missing driver is a failure.
/// </summary>
public static class DriverPrerequisite {

  /// <summary>True when CI has declared that a driver must be present, so absence is a failure.</summary>
  public static bool Required => Environment.GetEnvironmentVariable("DBE2E_REQUIRE_DRIVER") == "1";

  public static (bool available, string detail) Probe() {
    if (OperatingSystem.IsWindows()) {
      var winfsp = _WindowsDriverPresent("WinFsp", Environment.Is64BitOperatingSystem ? "winfsp-x64.dll" : "winfsp-x86.dll");
      if (winfsp != null)
        return (true, $"WinFsp ({winfsp})");

      var dokan = _DokanPresent();
      return dokan != null ? (true, $"Dokan ({dokan})") : (false, "neither WinFsp nor Dokan is installed");
    }

    if (OperatingSystem.IsLinux())
      return File.Exists("/dev/fuse")
        ? (true, "FUSE (/dev/fuse present)")
        : (false, "/dev/fuse is missing — install fuse3 and ensure the device is exposed");

    return (false, $"mounting is not supported on {DbMount.Platform}");
  }

  /// <summary>Skips on an unequipped developer machine; fails when CI said a driver would be there.</summary>
  public static void RequireAvailable() {
    var (available, detail) = Probe();
    if (available)
      return;

    var message = $"No filesystem driver on {DbMount.Platform}: {detail}.";
    if (Required)
      Assert.Fail(message + " DBE2E_REQUIRE_DRIVER=1 says this environment was supposed to have one, "
                  + "so this is a broken CI setup rather than an unequipped machine — a silently skipped "
                  + "end-to-end suite is indistinguishable from a passing one.");

    Assert.Ignore(message + " Set DBE2E_REQUIRE_DRIVER=1 to turn this into a failure.");
  }

  private static string? _WindowsDriverPresent(string productKey, string dllName) {
    if (!OperatingSystem.IsWindows())
      return null;

    try {
      foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry32, Microsoft.Win32.RegistryView.Registry64 }) {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
        using var key = baseKey.OpenSubKey($@"SOFTWARE\{productKey}");
        if (key?.GetValue("InstallDir") is string dir && dir.Length > 0 && File.Exists(Path.Combine(dir, "bin", dllName)))
          return Path.Combine(dir, "bin", dllName);
      }

      var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
      var fallback = Path.Combine(programFilesX86, productKey, "bin", dllName);
      return File.Exists(fallback) ? fallback : null;
    } catch (Exception) {
      return null;
    }
  }

  private static string? _DokanPresent() {
    if (!OperatingSystem.IsWindows())
      return null;

    foreach (var name in new[] { "dokan2.dll", "dokan1.dll" }) {
      var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
      if (File.Exists(candidate))
        return candidate;
    }

    return null;
  }

}
