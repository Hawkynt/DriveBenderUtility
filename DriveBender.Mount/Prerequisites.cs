using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
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
      // mounting a drive letter does not need elevation (and must NOT be elevated, or the drive
      // lands in a different session than Explorer) — only the driver install does
      return new(true, winfsp ? "WinFsp" : "Dokan", $"{(winfsp ? "WinFsp" : "Dokan")} is installed.", false, false);

    return new(false, "WinFsp/Dokan", "No filesystem driver (WinFsp or Dokan) is installed.", true, false);
#else
    // the non-Windows build running on Windows can never mount there — say THAT instead of
    // nonsensically demanding FUSE on a Windows box
    if (OperatingSystem.IsWindows())
      return new(false, "WinFsp/Dokan",
        "This dbmount is the non-Windows build and cannot mount on Windows — launch the net10.0-windows dbmount (rebuild the solution so both flavors exist).",
        false, false);

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

    // NEVER run an unverified installer elevated: verify the downloaded MSI carries a valid
    // Authenticode signature (rejects a tampered or swapped file) before msiexec touches it.
    if (!_VerifyAuthenticode(msi, out var sigError)) {
      try { File.Delete(msi); } catch { /* best effort */ }
      return (false, "Refusing to run the downloaded WinFsp installer — its digital signature could not be verified.\n"
        + sigError + "\nInstall it manually from https://winfsp.dev instead.");
    }

    var runasVerb = IsElevated ? null : "runas";
    if (!_TryRunShell("msiexec", $"/i \"{msi}\" /passive /norestart", runasVerb, out var output))
      return (false, "The WinFsp installer did not complete (the elevation prompt may have been declined).\n" + output
        + "\nYou can also install it manually from https://winfsp.dev.");

    return Windows.WinFspMountHost.IsWinFspAvailable()
      ? (true, "WinFsp installed. You can mount now.")
      : (false, "The installer ran but WinFsp is still not detected — reopen the app and retry. If it persists, install manually from https://winfsp.dev.");
#else
    if (OperatingSystem.IsWindows())
      return (false, "This dbmount is the non-Windows build — there is nothing to install on Windows; launch the net10.0-windows dbmount instead.");

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

      // download into a FRESH private directory, not the shared temp root: the predictable
      // shared path let another local process swap the file between download and elevated launch
      var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dbwinfsp-" + Guid.NewGuid().ToString("N")));
      var target = Path.Combine(dir.FullName, Path.GetFileName(new Uri(url).LocalPath));
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

  private static class Wintrust {
    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_FILE_INFO {
      public uint cbStruct;
      [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
      public IntPtr hFile;
      public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_DATA {
      public uint cbStruct;
      public IntPtr pPolicyCallbackData;
      public IntPtr pSIPClientData;
      public uint dwUIChoice;
      public uint fdwRevocationChecks;
      public uint dwUnionChoice;
      public IntPtr pFile;
      public uint dwStateAction;
      public IntPtr hWVTStateData;
      public IntPtr pwszURLReference;
      public uint dwProvFlags;
      public uint dwUIContext;
      public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);
  }

  /// <summary>
  /// Verifies the file carries a VALID Authenticode signature that chains to a trusted root
  /// (via WinVerifyTrust — this detects any tampering of the content, unlike merely reading the
  /// embedded cert) AND is signed by the expected WinFsp publisher. Returns false with a reason
  /// otherwise, so an unsigned/tampered/foreign installer is never executed elevated.
  /// </summary>
  private static bool _VerifyAuthenticode(string path, out string error) {
    error = "";
    var actionGeneric = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
    var fileInfo = new Wintrust.WINTRUST_FILE_INFO {
      cbStruct = (uint)Marshal.SizeOf<Wintrust.WINTRUST_FILE_INFO>(),
      pcwszFilePath = path,
    };
    var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<Wintrust.WINTRUST_FILE_INFO>());
    var pData = IntPtr.Zero;
    try {
      Marshal.StructureToPtr(fileInfo, pFile, false);
      var data = new Wintrust.WINTRUST_DATA {
        cbStruct = (uint)Marshal.SizeOf<Wintrust.WINTRUST_DATA>(),
        dwUIChoice = 2,          // WTD_UI_NONE
        fdwRevocationChecks = 0, // WTD_REVOKE_NONE (offline-tolerant; chain trust still enforced)
        dwUnionChoice = 1,       // WTD_CHOICE_FILE
        pFile = pFile,
        dwStateAction = 1,       // WTD_STATEACTION_VERIFY
        dwProvFlags = 0x100,     // WTD_SAFER_FLAG
      };
      pData = Marshal.AllocHGlobal(Marshal.SizeOf<Wintrust.WINTRUST_DATA>());
      Marshal.StructureToPtr(data, pData, false);

      var result = Wintrust.WinVerifyTrust(IntPtr.Zero, actionGeneric, pData);

      // release the WinVerifyTrust state regardless
      data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
      Marshal.StructureToPtr(data, pData, true);
      Wintrust.WinVerifyTrust(IntPtr.Zero, actionGeneric, pData);

      if (result != 0) {
        error = $"WinVerifyTrust rejected the file (0x{result:X8}) — it is unsigned, tampered, or its certificate does not chain to a trusted root.";
        return false;
      }
    } catch (Exception e) {
      error = "Signature verification threw: " + e.Message;
      return false;
    } finally {
      Marshal.FreeHGlobal(pFile);
      if (pData != IntPtr.Zero)
        Marshal.FreeHGlobal(pData);
    }

    // the chain is valid; also confirm the signer is WinFsp's publisher, not just any valid cert
    try {
      using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
        System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path));
      var subject = cert.Subject;
      if (subject.IndexOf("WinFsp", StringComparison.OrdinalIgnoreCase) < 0
          && subject.IndexOf("Navimatics", StringComparison.OrdinalIgnoreCase) < 0) {
        error = $"The installer is validly signed but by an unexpected publisher: {subject}";
        return false;
      }

      DriveBender.Logger($"WinFsp installer signature verified — signer: {subject}");
      return true;
    } catch (Exception e) {
      error = "Could not read the signer certificate: " + e.Message;
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
