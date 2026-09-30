using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DivisonM.Vfs;

/// <summary>
/// The operating-system calls behind the space-saving capabilities of a local member
/// (docs/SpaceSavings.md): hard links, block cloning, sparse ranges, link counts and allocated size.
/// .NET exposes none of them. Every call reports failure instead of throwing for "not supported
/// here", so a caller can fall back; a real I/O error still surfaces as an exception.
/// </summary>
internal static class SpaceNative {

  #region what a volume can do

  /// <summary>What the filesystem behind <paramref name="rootPath"/> supports; probed once per member.</summary>
  public static BackendCaps CapabilitiesOf(string rootPath, string probeFolder) {
    try {
      return OperatingSystem.IsWindows() ? _WindowsCapabilities(rootPath)
        : OperatingSystem.IsLinux() ? _LinuxCapabilities(probeFolder)
        : BackendCaps.None;
    } catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) {
      return BackendCaps.None;
    }
  }

  private const uint _FILE_SUPPORTS_SPARSE_FILES = 0x00000040;
  private const uint _FILE_SUPPORTS_HARD_LINKS = 0x00400000;
  private const uint _FILE_SUPPORTS_BLOCK_REFCOUNTING = 0x08000000;

  private static BackendCaps _WindowsCapabilities(string rootPath) {
    var volume = Path.GetPathRoot(Path.GetFullPath(rootPath));
    if (string.IsNullOrEmpty(volume))
      return BackendCaps.None;

    if (!volume.EndsWith('\\'))
      volume += "\\";
    if (!GetVolumeInformationW(volume, null, 0, out _, out _, out var flags, null, 0))
      return BackendCaps.None;

    var caps = BackendCaps.None;
    if ((flags & _FILE_SUPPORTS_HARD_LINKS) != 0)
      caps |= BackendCaps.HardLinks;
    if ((flags & _FILE_SUPPORTS_SPARSE_FILES) != 0)
      caps |= BackendCaps.Sparse;
    if ((flags & _FILE_SUPPORTS_BLOCK_REFCOUNTING) != 0)
      caps |= BackendCaps.BlockClone;
    return caps;
  }

  /// <summary>Linux has no one call that says what a filesystem supports, so each capability is tried once on a probe file.</summary>
  private static BackendCaps _LinuxCapabilities(string probeFolder) {
    Directory.CreateDirectory(probeFolder);
    var source = Path.Combine(probeFolder, "space-probe-" + Guid.NewGuid().ToString("N"));
    var link = source + ".link";
    var clone = source + ".clone";
    var caps = BackendCaps.None;
    try {
      File.WriteAllBytes(source, new byte[64 * 1024]);
      if (TryHardLink(source, link))
        caps |= BackendCaps.HardLinks;
      if (TryClone(source, clone))
        caps |= BackendCaps.BlockClone;
      if (TryPunchHole(source, 0, 64 * 1024))
        caps |= BackendCaps.Sparse;
    } finally {
      foreach (var file in new[] { source, link, clone })
        try {
          File.Delete(file);
        } catch (IOException) {
        }
    }

    return caps;
  }

  #endregion

  #region hard links and link counts

  public static bool TryHardLink(string existingPath, string newPath)
    => OperatingSystem.IsWindows() ? CreateHardLinkW(newPath, existingPath, IntPtr.Zero) : link(existingPath, newPath) == 0;

  public static int LinkCount(string path) {
    if (OperatingSystem.IsWindows()) {
      using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      return GetFileInformationByHandle(handle, out var info) ? (int)info.NumberOfLinks : 1;
    }

    return OperatingSystem.IsLinux() && _Statx(path) is { } statx ? (int)statx.Links : 1;
  }

  #endregion

  #region block cloning

  /// <summary>Creates <paramref name="targetPath"/> as a block-sharing clone of <paramref name="sourcePath"/>; false when the filesystem cannot.</summary>
  public static bool TryClone(string sourcePath, string targetPath) {
    try {
      using var source = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      using var target = File.OpenHandle(targetPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
      var cloned = OperatingSystem.IsWindows() ? _WindowsClone(source, target, RandomAccess.GetLength(source))
        : OperatingSystem.IsLinux() && ioctl((int)target.DangerousGetHandle(), _FICLONE, (int)source.DangerousGetHandle()) == 0;
      if (cloned)
        return true;
    } catch (IOException) {
      // fall through: whatever was created is removed below
    }

    try {
      File.Delete(targetPath);
    } catch (IOException) {
    }

    return false;
  }

  private const ulong _FICLONE = 0x40049409;
  private const uint _FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x00098344;
  private const uint _FSCTL_SET_SPARSE = 0x000900C4;
  private const uint _FSCTL_SET_ZERO_DATA = 0x000980C8;

  /// <summary>ReFS block cloning: the target must be as long as the source, and ranges must cover whole clusters.</summary>
  private static bool _WindowsClone(SafeFileHandle source, SafeFileHandle target, long length) {
    RandomAccess.SetLength(target, length);
    if (length == 0)
      return true;

    const long chunk = 1L << 30; // the call copies at most a few GiB at once
    for (long offset = 0; offset < length; offset += chunk) {
      var count = Math.Min(chunk, length - offset);
      var request = new DuplicateExtentsData { FileHandle = source.DangerousGetHandle(), SourceFileOffset = offset, TargetFileOffset = offset, ByteCount = _RoundUp(count, 64 * 1024) };
      if (!DeviceIoControl(target, _FSCTL_DUPLICATE_EXTENTS_TO_FILE, ref request, Marshal.SizeOf<DuplicateExtentsData>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
        return false;
    }

    RandomAccess.SetLength(target, length); // the last range was rounded up to whole clusters
    return true;
  }

  private static long _RoundUp(long value, long unit) => (value + unit - 1) / unit * unit;

  #endregion

  #region sparse ranges

  /// <summary>Releases [offset, offset+length) to the filesystem; the range then reads as zeros and the size is unchanged.</summary>
  public static bool TryPunchHole(string path, long offset, long length) {
    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
    if (OperatingSystem.IsWindows()) {
      if (!DeviceIoControl(handle, _FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        return false;

      var zero = new ZeroDataInformation { FileOffset = offset, BeyondFinalZero = offset + length };
      return DeviceIoControl(handle, _FSCTL_SET_ZERO_DATA, ref zero, Marshal.SizeOf<ZeroDataInformation>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    const int punchHoleKeepSize = 0x02 | 0x01; // FALLOC_FL_PUNCH_HOLE | FALLOC_FL_KEEP_SIZE
    return OperatingSystem.IsLinux() && fallocate((int)handle.DangerousGetHandle(), punchHoleKeepSize, offset, length) == 0;
  }

  /// <summary>Bytes the file occupies on disk, or -1 when the platform cannot say.</summary>
  public static long AllocatedBytes(string path) {
    if (OperatingSystem.IsWindows()) {
      var low = GetCompressedFileSizeW(path, out var high);
      return low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0 ? -1 : ((long)high << 32) | low;
    }

    return OperatingSystem.IsLinux() && _Statx(path) is { } statx ? (long)statx.Blocks * 512 : -1;
  }

  #endregion

  #region Windows declarations

  // Pack = 4: the three FILETIMEs are pairs of DWORDs, 4-byte aligned. Declared as longs without it,
  // each is padded to 8 and every field after the first shifts — the link count read back as 1114112.
  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  private struct ByHandleFileInformation {
    public uint FileAttributes;
    public long CreationTime, LastAccessTime, LastWriteTime;
    public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct DuplicateExtentsData {
    public IntPtr FileHandle;
    public long SourceFileOffset, TargetFileOffset, ByteCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ZeroDataInformation {
    public long FileOffset, BeyondFinalZero;
  }

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern bool GetVolumeInformationW(string rootPathName, char[]? volumeName, int volumeNameSize,
    out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags, char[]? fileSystemName, int fileSystemNameSize);

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr inBuffer, int inSize,
    IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, ref DuplicateExtentsData inBuffer, int inSize,
    IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, ref ZeroDataInformation inBuffer, int inSize,
    IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

  #endregion

  #region Linux declarations

  private readonly record struct StatxResult(uint Links, ulong Blocks);

  /// <summary>
  /// statx, not stat: struct stat's layout differs between architectures, statx's is fixed — the
  /// link count at byte 16, the 512-byte block count at byte 48.
  /// </summary>
  private static StatxResult? _Statx(string path) {
    const int atFdCwd = -100;
    const uint statxNlink = 0x004, statxBlocks = 0x400;
    var buffer = new byte[256];
    if (statx(atFdCwd, path, 0, statxNlink | statxBlocks, buffer) != 0)
      return null;

    return new(BitConverter.ToUInt32(buffer, 16), BitConverter.ToUInt64(buffer, 48));
  }

  [DllImport("libc", SetLastError = true)]
  private static extern int link(string oldPath, string newPath);

  [DllImport("libc", SetLastError = true)]
  private static extern int ioctl(int fd, ulong request, int argument);

  [DllImport("libc", SetLastError = true)]
  private static extern int fallocate(int fd, int mode, long offset, long length);

  [DllImport("libc", SetLastError = true)]
  private static extern int statx(int dirFd, string path, int flags, uint mask, byte[] buffer);

  #endregion

}
