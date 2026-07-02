using System.Diagnostics;
using DivisonM.Vfs;

namespace DivisonM.Mount;

/// <summary>
/// Unattended-mount installers (§6.12): a Windows service (FR-MOUNT-WIN-CLI) and a Linux
/// systemd unit + fstab helper (FR-MOUNT-FSTAB) so a manifest mounts before login / at
/// boot. Each command reports the exact effect and needs the elevation the OS requires.
/// </summary>
internal static class ServiceInstaller {

  private static string _ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "dbmount");

  #region Windows service

  public static int InstallWindowsService(string manifest, string? target) {
    if (!OperatingSystem.IsWindows()) {
      Console.Error.WriteLine("install-service is a Windows command; use install-systemd on Linux.");
      return 1;
    }

    var serviceName = _ServiceName(manifest);
    var targetArg = string.IsNullOrWhiteSpace(target) ? "" : $" --target \\\"{target}\\\"";
    var binPath = $"\"{_ExecutablePath}\" mount --manifest \"{manifest}\"{targetArg}";

    var create = _RunSc($"create {serviceName} binPath= \"{binPath}\" start= auto DisplayName= \"Drive Bender pool {Path.GetFileNameWithoutExtension(manifest)}\"");
    if (create != 0)
      return create;

    _RunSc($"description {serviceName} \"Mounts the Drive Bender pool defined by {manifest}\"");
    Console.WriteLine($"Installed Windows service '{serviceName}'. Start it with: sc start {serviceName}");
    Console.WriteLine("Remote-member credentials must be stored under the service account's credential store.");
    return 0;
  }

  public static int UninstallWindowsService(string manifest) {
    if (!OperatingSystem.IsWindows()) {
      Console.Error.WriteLine("uninstall-service is a Windows command.");
      return 1;
    }

    var serviceName = _ServiceName(manifest);
    _RunSc($"stop {serviceName}");
    var delete = _RunSc($"delete {serviceName}");
    if (delete == 0)
      Console.WriteLine($"Removed Windows service '{serviceName}'.");

    return delete;
  }

  private static string _ServiceName(string manifest) => "DriveBenderPool_" + Path.GetFileNameWithoutExtension(manifest);

  private static int _RunSc(string arguments) {
    var process = Process.Start(new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false });
    process!.WaitForExit();
    if (process.ExitCode != 0)
      Console.Error.WriteLine($"sc {arguments.Split(' ')[0]} failed (exit {process.ExitCode}); run from an elevated prompt.");

    return process.ExitCode;
  }

  #endregion

  #region Linux systemd + fstab helper

  public static int InstallSystemd(string manifest, IHostEnvironment host) {
    if (!OperatingSystem.IsLinux()) {
      Console.Error.WriteLine("install-systemd is a Linux command; use install-service on Windows.");
      return 1;
    }

    var instance = Path.GetFileNameWithoutExtension(manifest);
    var unitPath = "/etc/systemd/system/drivebender-pool@.service";
    var helperSource = Path.Combine(AppContext.BaseDirectory, "scripts", "mount.drivebender");
    var unitSource = Path.Combine(AppContext.BaseDirectory, "scripts", "drivebender-pool@.service");

    try {
      if (File.Exists(unitSource))
        File.Copy(unitSource, unitPath, overwrite: true);
      else
        File.WriteAllText(unitPath, _DefaultUnit());

      if (File.Exists(helperSource)) {
        File.Copy(helperSource, "/sbin/mount.drivebender", overwrite: true);
        File.SetUnixFileMode("/sbin/mount.drivebender", (UnixFileMode)0b111_101_101 /* 0755 */);
        _TrySymlink("/sbin/mount.drivebender", "/sbin/mount.fuse.drivebender");
      }

      Console.WriteLine($"Installed systemd unit '{unitPath}' and /sbin/mount.drivebender.");
      Console.WriteLine($"Enable this pool at boot with: systemctl enable --now drivebender-pool@{instance}.service");
      Console.WriteLine($"Or add to /etc/fstab: {manifest}  <mountpoint>  fuse.drivebender  defaults,_netdev  0 0");
      return 0;
    } catch (UnauthorizedAccessException) {
      Console.Error.WriteLine("install-systemd needs root — re-run with sudo.");
      return 1;
    } catch (IOException e) {
      Console.Error.WriteLine($"install-systemd failed: {e.Message}");
      return 1;
    }
  }

  private static void _TrySymlink(string existing, string link) {
    try {
      if (File.Exists(link) || Directory.Exists(link))
        File.Delete(link);

      File.CreateSymbolicLink(link, existing);
    } catch (IOException) {
      // the primary helper name is enough; the fuse.-prefixed alias is a convenience
    }
  }

  private static string _DefaultUnit() => """
    [Unit]
    Description=Drive Bender pool %i
    After=network-online.target
    Wants=network-online.target

    [Service]
    Type=simple
    ExecStart=/opt/drivebenderutility/dbmount mount --manifest /etc/drivebenderutility/pools/%i.json
    Restart=on-failure
    RestartSec=10

    [Install]
    WantedBy=multi-user.target
    """;

  #endregion

  #region Windows shell association (FR-MOUNT-WIN-GUI)

  public static int RegisterShellAssociation() {
    if (!OperatingSystem.IsWindows()) {
      Console.Error.WriteLine("register-shell is a Windows command.");
      return 1;
    }

    var exe = _ExecutablePath;
    var commands = new[] {
      @"reg add ""HKCU\Software\Classes\.dbpool.json"" /ve /d ""DriveBender.Pool"" /f",
      @"reg add ""HKCU\Software\Classes\DriveBender.Pool"" /ve /d ""Drive Bender Pool Manifest"" /f",
      $@"reg add ""HKCU\Software\Classes\DriveBender.Pool\shell\mount\command"" /ve /d ""\""{exe}\"" mount --manifest \""%1\"""" /f",
    };

    foreach (var command in commands) {
      var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command) { UseShellExecute = false });
      process!.WaitForExit();
      if (process.ExitCode != 0) {
        Console.Error.WriteLine("register-shell failed writing the file association.");
        return process.ExitCode;
      }
    }

    Console.WriteLine("Registered the right-click \"mount\" action for *.dbpool.json manifests (current user).");
    return 0;
  }

  #endregion

}
