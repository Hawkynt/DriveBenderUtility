using DivisonM.Vfs;
using Hawkynt.CloudStorage;

namespace DivisonM.Backends;

/// <summary>
/// Bridges a provider-neutral <see cref="ICloudStore"/> from Hawkynt.CloudStorage into the
/// pool engine's <see cref="IWholeFileStore"/> contract: entry/metadata records are the same
/// shape, so this is a pure structural translation, while
/// <see cref="CloudStorageException"/>s surface unchanged and are normalized to
/// <see cref="PoolFsException"/> centrally by <see cref="WholeFileVolumeIO"/>.
/// </summary>
public sealed class CloudStoreAdapter(ICloudStore store) : IWholeFileStore {

  public void Connect() => store.Connect();

  public bool Probe() => store.Probe();

  /// <summary>
  /// Provider capabilities pass straight through: the engine decides between a ranged block read
  /// and a whole-object strategy on the strength of what the actual provider supports, so the
  /// flags must not be flattened here.
  /// </summary>
  public StoreCaps Caps {
    get {
      var caps = store.Caps;
      var mapped = StoreCaps.None;
      if ((caps & CloudCaps.RangeRead) != 0)
        mapped |= StoreCaps.RangeRead;
      if ((caps & CloudCaps.StreamingUpload) != 0)
        mapped |= StoreCaps.StreamingUpload;
      if ((caps & CloudCaps.StreamingDownload) != 0)
        mapped |= StoreCaps.StreamingDownload;

      return mapped;
    }
  }

  public byte[] Download(string physicalPath) => store.Download(physicalPath);

  public void Upload(string physicalPath, byte[] content) => store.Upload(physicalPath, content);

  public Stream OpenRead(string physicalPath) => store.OpenRead(physicalPath);

  public Stream OpenReadRange(string physicalPath, long offset, long count) => store.OpenReadRange(physicalPath, offset, count);

  public void Upload(string physicalPath, Stream content, long length = -1) => store.Upload(physicalPath, content, length);

  public void DeleteFile(string physicalPath) => store.DeleteFile(physicalPath);

  public StoreMeta? Stat(string physicalPath)
    => store.Stat(physicalPath) is { } meta ? new(meta.IsFolder, meta.Length, meta.CreatedUtc, meta.ModifiedUtc) : null;

  public void CreateFolder(string physicalPath) => store.CreateFolder(physicalPath);

  public void DeleteFolder(string physicalPath) => store.DeleteFolder(physicalPath);

  public IEnumerable<StoreEntry> List(string physicalFolder) {
    foreach (var entry in store.List(physicalFolder))
      yield return new(entry.Name, entry.IsFolder, entry.Length, entry.ModifiedUtc);
  }

  public void Dispose() => store.Dispose();

}

/// <summary>Maps the provider-neutral <see cref="CloudStorageError"/> onto the pool engine's <see cref="PoolFsError"/>.</summary>
internal static class CloudErrorTranslation {

  public static PoolFsException ToPoolFs(this CloudStorageException e, string member) {
    var error = e.Error switch {
      CloudStorageError.NotFound => PoolFsError.NotFound,
      CloudStorageError.AccessDenied => PoolFsError.AccessDenied,
      CloudStorageError.Exists => PoolFsError.Exists,
      CloudStorageError.NotEmpty => PoolFsError.NotEmpty,
      CloudStorageError.NoSpace => PoolFsError.NoSpace,
      CloudStorageError.Offline => PoolFsError.Offline,
      CloudStorageError.NotSupported => PoolFsError.NotSupported,
      _ => PoolFsError.IoError,
    };
    return new(error, $"{e.Message} (member '{member}')", e);
  }

}
