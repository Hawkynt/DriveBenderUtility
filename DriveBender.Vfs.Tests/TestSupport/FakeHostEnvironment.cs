using DivisonM.Vfs;

namespace DivisonM.Vfs.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IHostEnvironment"/>: volumes, folders and files are plain
/// dictionaries so discovery, member resolution and manifest redundancy run fully
/// headless (TST-FAKE). Volumes can be taken offline to model unplugged removables and
/// unreachable shares (SAFE-OFFLINE).
/// </summary>
public sealed class FakeHostEnvironment : IHostEnvironment {

  private sealed record FakeVolume(string Root, string PhysicalVolumeId) {
    public long BytesFree { get; set; }
    public long BytesTotal { get; set; }
    public bool Online { get; set; } = true;
  }

  private readonly List<FakeVolume> _volumes = [];
  private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

  public string ConfigRoot { get; init; } = @"C:\ProgramData\DriveBenderUtility";

  public int AtomicWriteCount { get; private set; }

  private static string _Normalize(string path) {
    var normalized = path.Replace('/', '\\').TrimEnd('\\');
    return normalized.Length == 2 && normalized[1] == ':' ? normalized + '\\' : normalized;
  }

  private static string? _Parent(string normalizedPath) {
    var index = normalizedPath.TrimEnd('\\').LastIndexOf('\\');
    if (index < 0)
      return null;

    var parent = normalizedPath[..index];
    return parent.Length == 2 && parent[1] == ':' ? parent + '\\' : parent.Length == 0 ? null : parent;
  }

  public void AddVolume(string root, string? physicalVolumeId = null, long bytesFree = 1L << 40, long bytesTotal = 2L << 40) {
    var normalized = _Normalize(root);
    this._volumes.Add(new(normalized, physicalVolumeId ?? $"VOL-{normalized.TrimEnd('\\', ':')}") { BytesFree = bytesFree, BytesTotal = bytesTotal });
    this._directories.Add(normalized);
  }

  public void SetVolumeOnline(string root, bool online) {
    var normalized = _Normalize(root);
    var volume = this._volumes.FirstOrDefault(v => v.Root.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidOperationException($"No fake volume '{root}'");
    volume.Online = online;
  }

  public void SetVolumeSpace(string root, long bytesFree, long bytesTotal) {
    var normalized = _Normalize(root);
    var volume = this._volumes.FirstOrDefault(v => v.Root.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidOperationException($"No fake volume '{root}'");
    volume.BytesFree = bytesFree;
    volume.BytesTotal = bytesTotal;
  }

  public void AddDirectory(string path) {
    var normalized = _Normalize(path);
    while (true) {
      if (!this._directories.Add(normalized))
        break;

      var parent = _Parent(normalized);
      if (parent == null)
        break;

      normalized = parent;
    }
  }

  public void AddFile(string path, string content) {
    var normalized = _Normalize(path);
    this._files[normalized] = content;
    var parent = _Parent(normalized);
    if (parent != null)
      this.AddDirectory(parent);
  }

  public void RemoveFile(string path) => this._files.Remove(_Normalize(path));

  public string? TryGetFileContent(string path) => this._files.GetValueOrDefault(_Normalize(path));

  private FakeVolume? _VolumeOf(string normalizedPath)
    => this._volumes
      .Where(v => normalizedPath.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase) || normalizedPath.Equals(v.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(v => v.Root.Length)
      .FirstOrDefault();

  private bool _IsReachable(string normalizedPath) => this._VolumeOf(normalizedPath) is not { Online: false };

  public IEnumerable<string> EnumerateVolumeRoots() => this._volumes.Where(v => v.Online).Select(v => v.Root);

  public bool FileExists(string path) {
    var normalized = _Normalize(path);
    return this._IsReachable(normalized) && this._files.ContainsKey(normalized);
  }

  public bool DirectoryExists(string path) {
    var normalized = _Normalize(path);
    return this._IsReachable(normalized) && this._directories.Contains(normalized);
  }

  public string ReadAllText(string path) {
    var normalized = _Normalize(path);
    if (!this._IsReachable(normalized))
      throw new IOException($"Volume offline: {path}");

    return this._files.TryGetValue(normalized, out var content) ? content : throw new FileNotFoundException(path);
  }

  public void WriteAllTextAtomic(string path, string content) {
    var normalized = _Normalize(path);
    if (!this._IsReachable(normalized))
      throw new IOException($"Volume offline: {path}");

    ++this.AtomicWriteCount;
    this.AddFile(normalized, content);
  }

  public void CreateDirectory(string path) {
    var normalized = _Normalize(path);
    if (!this._IsReachable(normalized))
      throw new IOException($"Volume offline: {path}");

    this.AddDirectory(normalized);
  }

  public void DeleteFile(string path) => this._files.Remove(_Normalize(path));

  public IEnumerable<string> EnumerateFiles(string directory, string pattern) {
    var normalized = _Normalize(directory);
    if (!this._IsReachable(normalized) || !this._directories.Contains(normalized))
      yield break;

    var prefix = normalized.EndsWith('\\') ? normalized : normalized + '\\';
    foreach (var file in this._files.Keys) {
      if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        continue;

      var name = file[prefix.Length..];
      if (name.Contains('\\'))
        continue;

      if (_PatternMatches(pattern, name))
        yield return file;
    }
  }

  public IEnumerable<string> EnumerateDirectories(string directory) {
    var normalized = _Normalize(directory);
    if (!this._IsReachable(normalized) || !this._directories.Contains(normalized))
      yield break;

    var prefix = normalized.EndsWith('\\') ? normalized : normalized + '\\';
    foreach (var dir in this._directories) {
      if (!dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        continue;

      var name = dir[prefix.Length..];
      if (name.Length == 0 || name.Contains('\\'))
        continue;

      yield return dir;
    }
  }

  private static bool _PatternMatches(string pattern, string name) {
    if (pattern is "*" or "*.*")
      return true;

    var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
    return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
  }

  public VolumeIdentity GetVolumeIdentity(string path) {
    var normalized = _Normalize(path);
    var volume = this._VolumeOf(normalized);
    return volume == null
      ? new("VOL-UNKNOWN", 0, 0)
      : new(volume.PhysicalVolumeId, volume.BytesFree, volume.BytesTotal);
  }

}
