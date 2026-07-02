using System.Runtime.InteropServices;
using System.Text;

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

        if (NativeMethods.GetVolumeNameForVolumeMountPoint(mountPointText, volumeName, volumeName.Capacity))
          return volumeName.ToString();

        // UNC shares have no volume GUID; the share itself is the failure domain
        return mountPointText.ToUpperInvariant();
      }

      return (Path.GetPathRoot(fullPath) ?? fullPath).ToUpperInvariant();
    }

    // Linux: the mount source from /proc/mounts (longest matching mountpoint) is the failure domain
    try {
      var best = ("", "");
      foreach (var line in File.ReadLines("/proc/mounts")) {
        var parts = line.Split(' ');
        if (parts.Length < 2)
          continue;

        var mountTarget = parts[1];
        if (fullPath.StartsWith(mountTarget, StringComparison.Ordinal) && mountTarget.Length > best.Item2.Length)
          best = (parts[0], mountTarget);
      }

      if (best.Item1.Length > 0)
        return best.Item1;
    } catch (IOException) {
      // fall through to path root
    } catch (UnauthorizedAccessException) {
      // fall through to path root
    }

    return Path.GetPathRoot(fullPath) ?? fullPath;
  }

}
