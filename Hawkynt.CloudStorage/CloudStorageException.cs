namespace Hawkynt.CloudStorage;

/// <summary>The provider-neutral failure kinds every store maps its native errors onto.</summary>
public enum CloudStorageError {
  NotFound,
  AccessDenied,
  Exists,
  NotEmpty,
  NoSpace,
  Offline,
  IoError,
  NotSupported,
}

/// <summary>The single exception type stores raise, carrying a normalized <see cref="CloudStorageError"/>.</summary>
public sealed class CloudStorageException(CloudStorageError error, string message, Exception? innerException = null)
  : Exception(message, innerException) {

  public CloudStorageError Error { get; } = error;

}
