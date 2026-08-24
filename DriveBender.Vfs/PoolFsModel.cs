namespace DivisonM.Vfs;

/// <summary>
/// Platform-neutral error contract (FR-ERRNO). Every engine failure maps to one of
/// these; the mount adapters translate them to NTSTATUS respectively errno.
/// </summary>
public enum PoolFsError {
  None = 0,
  NotFound,
  AccessDenied,
  Exists,
  NotEmpty,
  NoSpace,
  IoError,
  StaleHandle,
  NotSupported,
  Offline,
  InvalidArgument,
  NotADirectory,
  IsADirectory,
}

/// <summary>Engine-level exception carrying the platform-neutral error code (FR-ERRNO).</summary>
public class PoolFsException(PoolFsError error, string message, Exception? inner = null) : IOException(message, inner) {
  public PoolFsError Error { get; } = error;
}

/// <summary>Metadata of one physical file as seen by an <see cref="IVolumeIO"/> backend.</summary>
public readonly record struct FileMeta(long Length, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc, FileAttributes Attributes) {
  public bool IsDirectory => (this.Attributes & FileAttributes.Directory) != 0;
}

/// <summary>One entry of a per-volume directory listing.</summary>
public readonly record struct VolumeEntry(string Name, bool IsDirectory, long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// Maps pool-relative logical paths onto the physical Drive Bender on-disk layout
/// (SAFE-COMPAT) and identifies the on-disk names that must stay hidden from the
/// mounted namespace (FR-HIDE).
/// </summary>
public static class PoolPaths {

  /// <summary>
  /// How two pool-relative paths are compared for identity — the single authority for it.
  ///
  /// Windows says <c>Report.txt</c> and <c>REPORT.TXT</c> are one file; POSIX says they are two.
  /// The engine used to answer OrdinalIgnoreCase everywhere, on both platforms, and that is not a
  /// cosmetic difference on Linux: the handle table, the page and metadata caches, the write
  /// buffer, the staging map and the directory merge all collapsed the two names onto one entry,
  /// so copying a case-sensitive tree into a pool lost whichever file landed second — silently,
  /// with the bytes of one name serving reads of the other.
  ///
  /// The approximation is the PLATFORM rather than the member's actual filesystem, which is what
  /// <see cref="LocalVolumeIO"/> already assumes when it proves containment. A case-INsensitive
  /// filesystem mounted under Linux (an NTFS volume, a ciopfs) would therefore be treated as
  /// sensitive and could still collide at the member; that is a narrower and much rarer case than
  /// the one this fixes, and it needs a per-member capability probe rather than a constant.
  /// </summary>
  public static StringComparison PathComparison { get; } =
    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

  /// <summary><see cref="PathComparison"/> as a comparer, for the path-keyed dictionaries and sets.</summary>
  public static StringComparer PathComparer { get; } =
    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

  /// <summary>Marker folder for DriveBenderUtility sidecars (manifest mirror, member identity, trash, conflicts).</summary>
  public const string UtilityFolderName = ".drivebenderutility";
  public const string MemberMarkerFileName = "member.json";
  public const string ManifestMirrorFileName = "pool.json";

  public static string Normalize(string relativePath) {
    if (relativePath == null)
      throw new PoolFsException(PoolFsError.InvalidArgument, "Path must not be null");

    var result = relativePath.Replace('\\', '/').Trim('/');
    if (result.Contains("//"))
      result = string.Join('/', result.Split('/', StringSplitOptions.RemoveEmptyEntries));

    foreach (var segment in result.Split('/'))
      if (segment is ".." or ".")
        throw new PoolFsException(PoolFsError.InvalidArgument, $"Path must not contain relative segments: {relativePath}");

    return result;
  }

  /// <summary>
  /// Translates a normalized pool-relative path to its physical location on one member;
  /// a shadow copy lives in the parent folder's FOLDER.DUPLICATE.$DRIVEBENDER subfolder.
  /// </summary>
  public static string ToPhysical(string relativePath, bool shadow) {
    var normalized = Normalize(relativePath);
    if (!shadow)
      return normalized;

    var lastSlash = normalized.LastIndexOf('/');
    return lastSlash < 0
      ? $"{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}/{normalized}"
      : $"{normalized[..lastSlash]}/{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}/{normalized[(lastSlash + 1)..]}";
  }

  /// <summary>
  /// Physical location of a folder: its shadow side is the folder's own
  /// FOLDER.DUPLICATE.$DRIVEBENDER container (the marker that enables duplication and
  /// holds its files' shadow copies) — unlike files, whose shadow lives in the parent.
  /// </summary>
  public static string ToPhysicalFolder(string relativeFolder, bool shadow) {
    var normalized = Normalize(relativeFolder);
    if (!shadow)
      return normalized;

    return normalized.Length == 0
      ? DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME
      : $"{normalized}/{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}";
  }

  /// <summary>Names that never appear in the mounted namespace (FR-HIDE).</summary>
  public static bool IsHiddenName(string name)
    => name.Equals(DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase)
       || name.Equals(UtilityFolderName, StringComparison.OrdinalIgnoreCase)
       || name.EndsWith("." + DriveBender.DriveBenderConstants.TEMP_EXTENSION, StringComparison.OrdinalIgnoreCase)
       || name.EndsWith("." + DriveBender.DriveBenderConstants.INFO_EXTENSION, StringComparison.OrdinalIgnoreCase);

  public static string GetParent(string relativePath) {
    var normalized = Normalize(relativePath);
    var lastSlash = normalized.LastIndexOf('/');
    return lastSlash < 0 ? string.Empty : normalized[..lastSlash];
  }

  public static string GetName(string relativePath) {
    var normalized = Normalize(relativePath);
    var lastSlash = normalized.LastIndexOf('/');
    return lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
  }

}
