using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DivisonM.Vfs;

/// <summary>
/// Identity of the physical volume backing a path — the failure domain key (SAFE-PHYS) —
/// plus its capacity figures for shared-volume free-space de-duplication (FR-SPACE-SHARED).
/// </summary>
public sealed record VolumeIdentity(string PhysicalVolumeId, long BytesFree, long BytesTotal);

/// <summary>
/// The engine's view of the host machine: volume enumeration, config locations, marker
/// file I/O and physical-volume identity. Faked in tests so discovery, resolution and
/// manifest redundancy run headless (TST-FAKE).
/// </summary>
public interface IHostEnvironment {

  /// <summary>Root directory for machine-global state (registry manifest copies, global config).</summary>
  string ConfigRoot { get; }

  /// <summary>All mounted volume roots that are candidates for member/pool discovery.</summary>
  IEnumerable<string> EnumerateVolumeRoots();

  bool FileExists(string path);
  bool DirectoryExists(string path);
  string ReadAllText(string path);

  /// <summary>Writes via temp + flush + atomic rename; a crash never leaves a torn file (SAFE-MANIFEST).</summary>
  void WriteAllTextAtomic(string path, string content);

  void CreateDirectory(string path);
  void DeleteFile(string path);

  /// <summary>Deletes a directory; recursive removes its contents too. No-op when absent.</summary>
  void DeleteDirectory(string path, bool recursive);

  IEnumerable<string> EnumerateFiles(string directory, string pattern);
  IEnumerable<string> EnumerateDirectories(string directory);

  /// <summary>Physical-volume identity and capacity for the volume backing <paramref name="path"/>.</summary>
  VolumeIdentity GetVolumeIdentity(string path);
}

/// <summary>
/// Pure, host-free helpers for mapping a path to its PHYSICAL DISK (not merely its volume /
/// partition) so SAFE-PHYS actually protects against disk death: two partitions of one spindle
/// must resolve to the SAME failure domain. Extracted so the fiddly parsing is unit-testable
/// without real hardware.
/// </summary>
public static class PhysicalVolumeResolver {

  /// <summary>Un-escapes a /proc/mounts field: the kernel octal-escapes space (\040), tab (\011), newline (\012) and backslash (\134).</summary>
  public static string UnescapeProcField(string field) {
    if (field.IndexOf('\\') < 0)
      return field;

    var sb = new StringBuilder(field.Length);
    for (var i = 0; i < field.Length; ++i) {
      if (field[i] == '\\' && i + 3 < field.Length
          && field[i + 1] is >= '0' and <= '7' && field[i + 2] is >= '0' and <= '7' && field[i + 3] is >= '0' and <= '7') {
        sb.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
        i += 3;
      } else {
        sb.Append(field[i]);
      }
    }

    return sb.ToString();
  }

  /// <summary>The mount SOURCE (device) of the longest mountpoint that actually contains <paramref name="fullPath"/> — with a real path-boundary test so '/mnt/data' never matches '/mnt/database'.</summary>
  public static string? MountSourceFor(string fullPath, IEnumerable<string> procMountsLines) {
    string? bestSource = null;
    var bestLen = -1;
    foreach (var line in procMountsLines) {
      var parts = line.Split(' ');
      if (parts.Length < 2)
        continue;

      var target = UnescapeProcField(parts[1]);
      if (_IsUnder(fullPath, target) && target.Length > bestLen) {
        bestLen = target.Length;
        bestSource = UnescapeProcField(parts[0]);
      }
    }

    return bestSource;
  }

  private static bool _IsUnder(string path, string mount)
    => mount == "/"
      ? path.StartsWith('/')
      : path.Equals(mount, StringComparison.Ordinal) || path.StartsWith(mount + "/", StringComparison.Ordinal);

  /// <summary>
  /// The whole-disk device name for a partition device (SAFE-PHYS): 'sda1'→'sda', 'nvme0n1p2'→
  /// 'nvme0n1', 'mmcblk0p1'→'mmcblk0'. Prefers sysfs (authoritative) via the injected probes and
  /// falls back to the naming convention. A device that is already a whole disk is returned as-is.
  /// </summary>
  public static string WholeDiskName(string deviceName, Func<string, bool> pathExists, Func<string, string?> parentDiskViaSysfs) {
    // only a partition has /sys/class/block/<dev>/partition
    if (pathExists($"/sys/class/block/{deviceName}/partition")) {
      var parent = parentDiskViaSysfs(deviceName);
      if (!string.IsNullOrEmpty(parent))
        return parent;
    } else if (pathExists($"/sys/class/block/{deviceName}")) {
      return deviceName; // sysfs says this is a whole disk, not a partition
    }

    return StripPartitionSuffix(deviceName);
  }

  /// <summary>
  /// Naming-convention fallback for when sysfs is unavailable. Handles the two partition schemes
  /// precisely: 'p&lt;N&gt;' after a digit (nvme0n1p2→nvme0n1, mmcblk0p1→mmcblk0) and classic
  /// letter-suffixed devices (sda1→sda, vdb3→vdb). A whole disk that itself ends in a digit
  /// (nvme0n1, mmcblk0) is returned unchanged — the earlier code wrongly stripped it to nvme0n.
  /// </summary>
  public static string StripPartitionSuffix(string deviceName) {
    // nvme0n1p2 / mmcblk0p1 / loop0p1 → strip 'p<digits>' when a digit precedes the 'p'
    var pMatch = System.Text.RegularExpressions.Regex.Match(deviceName, @"^(.+\d)p\d+$");
    if (pMatch.Success)
      return pMatch.Groups[1].Value;

    // sda1 / sdb15 / vdc3 / hda2 / xvde1 / sr0-style — the disk name is all letters, partitions add digits
    var classic = System.Text.RegularExpressions.Regex.Match(deviceName, @"^((?:sd|hd|vd|xvd)[a-z]+)\d+$");
    return classic.Success ? classic.Groups[1].Value : deviceName;
  }

}

/// <summary>Production implementation over the real machine.</summary>
public sealed class RealHostEnvironment : IHostEnvironment {

  private static class NativeMethods {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathName(string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = "GetDiskFreeSpaceEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, out STORAGE_DEVICE_NUMBER lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_NUMBER {
      public uint DeviceType;
      public uint DeviceNumber;
      public uint PartitionNumber;
    }

    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_SHARE_READ_WRITE = 0x1 | 0x2;

    /// <summary>
    /// The PHYSICAL DISK number backing a volume GUID path (SAFE-PHYS): all partitions of one
    /// spindle share it, so copies split across two partitions are correctly seen as NOT
    /// independent. Needs no elevation (0 desired access). Null when the volume has no single
    /// disk (spanned/UNC/removable) — the caller keeps the volume GUID then.
    /// </summary>
    public static string? PhysicalDiskId(string volumeGuidPath) {
      // \\?\Volume{...}\  → \\.\Volume{...}  (device form, no trailing separator)
      var device = volumeGuidPath.TrimEnd('\\');
      if (device.StartsWith(@"\\?\", StringComparison.Ordinal))
        device = @"\\.\" + device[4..];

      using var handle = CreateFileW(device, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
      if (handle.IsInvalid)
        return null;

      return DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, out var number, (uint)Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(), out _, IntPtr.Zero)
        ? $"PhysicalDrive{number.DeviceNumber}"
        : null;
    }
  }

  public string ConfigRoot {
    get {
      if (OperatingSystem.IsWindows())
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DriveBenderUtility");

      // machine-wide when root (or already provisioned writable); per-user XDG fallback otherwise
      const string machineRoot = "/etc/drivebenderutility";
      if (Directory.Exists(machineRoot) || Environment.IsPrivilegedProcess)
        return machineRoot;

      var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
      var baseDir = string.IsNullOrEmpty(xdg)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
        : xdg;
      return Path.Combine(baseDir, "drivebenderutility");
    }
  }

  public IEnumerable<string> EnumerateVolumeRoots() {
    foreach (var drive in DriveInfo.GetDrives()) {
      if (drive is { IsReady: true, DriveType: DriveType.Fixed or DriveType.Removable or DriveType.Network })
        yield return drive.RootDirectory.FullName;
    }
  }

  public bool FileExists(string path) => File.Exists(path);
  public bool DirectoryExists(string path) => Directory.Exists(path);
  public string ReadAllText(string path) => File.ReadAllText(path);

  public void WriteAllTextAtomic(string path, string content) {
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
      Directory.CreateDirectory(directory);

    var tempPath = path + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
      var bytes = Encoding.UTF8.GetBytes(content);
      stream.Write(bytes, 0, bytes.Length);
      stream.Flush(true);
    }

    if (File.Exists(path))
      File.Replace(tempPath, path, null, true);
    else
      File.Move(tempPath, path);
  }

  public void CreateDirectory(string path) => Directory.CreateDirectory(path);
  public void DeleteFile(string path) => File.Delete(path);

  public void DeleteDirectory(string path, bool recursive) {
    if (Directory.Exists(path))
      Directory.Delete(path, recursive);
  }

  public IEnumerable<string> EnumerateFiles(string directory, string pattern)
    => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern) : [];

  public IEnumerable<string> EnumerateDirectories(string directory)
    => Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [];

  public VolumeIdentity GetVolumeIdentity(string path) {
    var fullPath = Path.GetFullPath(path);
    var volumeId = this._GetPhysicalVolumeId(fullPath);

    if (OperatingSystem.IsWindows() && NativeMethods.GetDiskFreeSpaceEx(fullPath, out var available, out var total, out _))
      return new(volumeId, (long)available, (long)total);

    try {
      var drive = new DriveInfo(fullPath);
      return new(volumeId, drive.AvailableFreeSpace, drive.TotalSize);
    } catch (ArgumentException) {
      return new(volumeId, 0, 0);
    }
  }

  private string _GetPhysicalVolumeId(string fullPath) {
    if (OperatingSystem.IsWindows()) {
      var mountPoint = new StringBuilder(520);
      if (NativeMethods.GetVolumePathName(fullPath, mountPoint, mountPoint.Capacity)) {
        var volumeName = new StringBuilder(520);
        var mountPointText = mountPoint.ToString();
        if (!mountPointText.EndsWith(Path.DirectorySeparatorChar))
          mountPointText += Path.DirectorySeparatorChar;

        if (NativeMethods.GetVolumeNameForVolumeMountPoint(mountPointText, volumeName, volumeName.Capacity)) {
          // SAFE-PHYS: the failure domain is the PHYSICAL DISK, not the volume — two partitions of
          // one spindle must share an id, so duplicating across them is (correctly) seen as
          // co-located. Fall back to the volume GUID when there is no single disk (spanned/removable).
          return NativeMethods.PhysicalDiskId(volumeName.ToString()) ?? volumeName.ToString();
        }

        // UNC shares have no volume GUID; the share itself is the failure domain
        return mountPointText.ToUpperInvariant();
      }

      return (Path.GetPathRoot(fullPath) ?? fullPath).ToUpperInvariant();
    }

    // Linux: resolve the mount source to its whole physical disk (a partition device like
    // /dev/sda1 must map to /dev/sda, else two partitions read as independent domains)
    try {
      var source = PhysicalVolumeResolver.MountSourceFor(fullPath, File.ReadLines("/proc/mounts"));
      if (source != null)
        return _LinuxWholeDisk(source);
    } catch (IOException) {
      // fall through to path root
    } catch (UnauthorizedAccessException) {
      // fall through to path root
    }

    return Path.GetPathRoot(fullPath) ?? fullPath;
  }

  /// <summary>Resolves a Linux mount source (possibly a /dev/mapper or by-id symlink) to its whole-disk device path.</summary>
  private static string _LinuxWholeDisk(string source) {
    // resolve symlinks (mapper/by-id/by-uuid) to the canonical /dev/<name>
    var canonical = source;
    try {
      if (source.StartsWith("/dev/", StringComparison.Ordinal) && File.Exists(source)) {
        var real = new FileInfo(source).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        if (!string.IsNullOrEmpty(real))
          canonical = real;
      }
    } catch (IOException) {
      // keep the raw source
    }

    if (!canonical.StartsWith("/dev/", StringComparison.Ordinal))
      return canonical; // pseudo/network source (tmpfs, nfs://…): its own name is the domain

    var name = canonical["/dev/".Length..];
    var disk = PhysicalVolumeResolver.WholeDiskName(name,
      Directory.Exists,
      dev => {
        // the whole-disk dir is the parent of the partition's sysfs symlink target
        try {
          var link = new DirectoryInfo($"/sys/class/block/{dev}").ResolveLinkTarget(returnFinalTarget: true)?.FullName;
          var parent = link == null ? null : Path.GetFileName(Path.GetDirectoryName(link.TrimEnd('/')) ?? "");
          return string.IsNullOrEmpty(parent) ? null : parent;
        } catch (IOException) {
          return null;
        }
      });

    return "/dev/" + disk;
  }

}
