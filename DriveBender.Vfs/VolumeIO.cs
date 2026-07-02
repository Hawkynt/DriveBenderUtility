namespace DivisonM.Vfs;

/// <summary>Capabilities a storage backend declares; the engine adapts to gaps (FR-CAP-ADAPT).</summary>
[Flags]
public enum BackendCaps {
  None = 0,
  RandomRead = 1,
  RandomWrite = 2,
  AtomicRename = 4,
  DurableFlush = 8,
  List = 16,
  Delete = 32,
  Timestamps = 64,
  ServerCredentials = 128,
}

/// <summary>
/// Byte-range and placement primitives over one pool member (CMP-IO). Core is
/// file-granular; the engine talks to members exclusively through this contract so the
/// whole engine runs against an in-memory fake in tests (TST-FAKE).
/// </summary>
public interface IVolumeIO {
  Guid MemberId { get; }
  string DisplayName { get; }

  /// <summary>Identity of the underlying physical volume — the failure domain (SAFE-PHYS).</summary>
  string PhysicalVolumeId { get; }

  bool IsOnline { get; }
  long BytesFree { get; }
  long BytesTotal { get; }
  BackendCaps Caps { get; }

  Stream OpenRead(string relativePath, bool shadow);

  /// <summary>Opens for positional writes; the returned stream's Flush is a durability barrier where <see cref="BackendCaps.DurableFlush"/> is declared.</summary>
  Stream OpenWrite(string relativePath, bool shadow, bool create);

  void Truncate(string relativePath, bool shadow, long length);
  void Delete(string relativePath, bool shadow);
  void EnsureFolder(string relativeFolder, bool shadow);
  void DeleteFolder(string relativeFolder, bool shadow);

  /// <summary>
  /// Atomically publishes staged content: the temp name (written under
  /// *.TEMP.$DRIVEBENDER) replaces the final name in one rename (SAFE-ATOMIC). The only
  /// way new content ever becomes visible.
  /// </summary>
  void AtomicReplace(string tempRelative, string finalRelative, bool shadow);

  /// <summary>Metadata of one physical file, or null when it does not exist.</summary>
  FileMeta? Stat(string relativePath, bool shadow);

  bool FileExists(string relativePath, bool shadow);
  bool FolderExists(string relativeFolder, bool shadow);
  IEnumerable<VolumeEntry> List(string relativeFolder, bool shadow);
  void SetTimestamps(string relativePath, bool shadow, DateTime? creationTimeUtc, DateTime? lastWriteTimeUtc);
}

/// <summary>Descriptor a backend needs to open a member (path/URI, tuning, credential reference).</summary>
public sealed record MemberDescriptor(
  Guid MemberId,
  string DisplayName,
  string Path,
  string? CredentialReference = null,
  TimeSpan? Timeout = null,
  int Retries = 0
);

/// <summary>
/// Resolves credential references (e.g. "cred-ref:MyPool-server") from the OS credential
/// store; secrets never live in a manifest, log, or metric (SEC-CRED).
/// </summary>
public interface ICredentialResolver {
  NetworkCredential? Resolve(string credentialReference);
}

public sealed record NetworkCredential(string UserName, string Secret);

/// <summary>Factory for <see cref="IVolumeIO"/> implementations, keyed by URI scheme (§6.1).</summary>
public interface IVolumeIOBackend {
  string Scheme { get; }
  BackendCaps Caps { get; }
  IVolumeIO Open(MemberDescriptor member, ICredentialResolver? credentials);
}
