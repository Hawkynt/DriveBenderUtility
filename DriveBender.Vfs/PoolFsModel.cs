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
