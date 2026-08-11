using System.Diagnostics;
using System.Text;

namespace DivisonM.EndToEnd.Tests.TestSupport;

/// <summary>
/// A real pool, created and mounted through the CLI, exposed at a path the OS can address.
///
/// Everything a test does with it goes through <see cref="System.IO"/> against
/// <see cref="MountPath"/> — that is, through the filesystem driver, the adapter, and the engine,
/// exactly as an application on the user's machine would. Nothing here talks to the engine
/// directly.
/// </summary>
public sealed class MountedPool : IDisposable {

  private static readonly TimeSpan _CLI_TIMEOUT = TimeSpan.FromMinutes(2);
  private static readonly TimeSpan _MOUNT_READY_TIMEOUT = TimeSpan.FromSeconds(90);
  private static readonly TimeSpan _UNMOUNT_TIMEOUT = TimeSpan.FromSeconds(60);

  private readonly Process _mountProcess;
  private readonly StringBuilder _stdout;
  private readonly StringBuilder _stderr;
  private readonly string? _mountDirectory; // Linux mountpoint we created and must remove
  private bool _disposed;

  public string PoolName { get; }
  public string MountPath { get; }
  public IReadOnlyList<string> MemberPaths { get; }
  public string Root { get; }

  private MountedPool(string poolName, string root, IReadOnlyList<string> members, string mountTarget, string mountPath,
    string? mountDirectory, Process mountProcess, StringBuilder stdout, StringBuilder stderr) {
    this.PoolName = poolName;
    this.Root = root;
    this.MemberPaths = members;
    this.MountPath = mountPath;
    this._mountDirectory = mountDirectory;
    this._mountProcess = mountProcess;
    this._stdout = stdout;
    this._stderr = stderr;
    _ = mountTarget;
  }

  /// <summary>Everything the mount child has printed — the first thing to look at when it misbehaves.</summary>
  public string MountLog => $"stdout:{Environment.NewLine}{DbMount.Snapshot(this._stdout)}{Environment.NewLine}"
                            + $"stderr:{Environment.NewLine}{DbMount.Snapshot(this._stderr)}";

  /// <summary>
  /// Creates a two-member pool under a fresh temp root and mounts it. Duplication defaults apply,
  /// so both members should converge on every file — which is what the tests check.
  /// </summary>
  public static MountedPool Create(int members = 2) {
    DriverPrerequisite.RequireAvailable();

    var poolName = "e2e" + Guid.NewGuid().ToString("N")[..10];
    var root = Path.Combine(Path.GetTempPath(), "dbe2e-" + Guid.NewGuid().ToString("N"));
    var memberPaths = new List<string>();
    for (var i = 0; i < members; ++i) {
      var member = Path.Combine(root, $"m{i}");
      Directory.CreateDirectory(member);
      memberPaths.Add(member);
    }

    var (target, path, createdDirectory) = _ChooseMountTarget(root);

    var arguments = new List<string> { "pool-create", "-n", poolName };
    foreach (var member in memberPaths) {
      arguments.Add("-m");
      arguments.Add(member);
    }

    try {
      DbMount.RunExpectingSuccess(_CLI_TIMEOUT, [.. arguments]);
    } catch {
      _TryDelete(root);
      throw;
    }

    var mountProcess = DbMount.Start(["mount", "--manifest", poolName, "-t", target, "--foreground"], out var stdout, out var stderr);
    var pool = new MountedPool(poolName, root, memberPaths, target, path, createdDirectory, mountProcess, stdout, stderr);
    try {
      pool._WaitUntilMounted();
      return pool;
    } catch {
      pool.Dispose();
      throw;
    }
  }

  /// <summary>
  /// Windows mounts at a drive letter (no elevation needed — only installing the driver is);
  /// Linux mounts at an empty directory we own.
  /// </summary>
  private static (string target, string path, string? createdDirectory) _ChooseMountTarget(string root) {
    if (!OperatingSystem.IsWindows()) {
      var mountpoint = Path.Combine(root, "mnt");
      Directory.CreateDirectory(mountpoint);
      return (mountpoint, mountpoint, mountpoint);
    }

    var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
    foreach (var letter in "XYZWVUTSRQPONM") // from the end, to stay clear of real volumes
      if (!taken.Contains(letter))
        return ($"{letter}:\\", $"{letter}:\\", null);

    throw new InvalidOperationException("No free drive letter is available to mount a test pool at");
  }

  /// <summary>
  /// Waits for the mount to become usable — and fails FAST with the child's output if the child
  /// dies, rather than sitting out the whole timeout. A driver that is installed but broken
  /// (a version-skewed native DLL, say) takes the process down, and the log is the only evidence.
  /// </summary>
  private void _WaitUntilMounted() {
    var deadline = DateTime.UtcNow + _MOUNT_READY_TIMEOUT;
    while (DateTime.UtcNow < deadline) {
      if (this._mountProcess.HasExited)
        throw new InvalidOperationException(
          $"The mount process exited with code {this._mountProcess.ExitCode} before the mount became usable "
          + $"on {DbMount.Platform}.{Environment.NewLine}{this.MountLog}");

      if (this._IsUsable())
        return;

      Thread.Sleep(250);
    }

    throw new TimeoutException(
      $"'{this.MountPath}' did not become usable within {_MOUNT_READY_TIMEOUT.TotalSeconds:F0}s "
      + $"on {DbMount.Platform}.{Environment.NewLine}{this.MountLog}");
  }

  /// <summary>
  /// Readiness means the OS can enumerate the pool — not merely that a path exists.
  ///
  /// On Linux the mountpoint is a DIRECTORY WE CREATED, so it exists, is enumerable and is
  /// writable well before FUSE attaches anything to it. A readiness check that only looks for the
  /// path therefore passes immediately, and every subsequent write lands in the plain directory
  /// underneath the mount: the data reads back perfectly (it is a real file on the host
  /// filesystem) while no pool member ever sees it. That is precisely the "written data never
  /// reaches the members" failure this suite reported — a defect in the harness, not the product,
  /// and intermittent exactly as a race between the test and the mount would be. So on Linux the
  /// mountpoint must actually BE a mount before the pool counts as ready.
  /// </summary>
  private bool _IsUsable() {
    try {
      if (!Directory.Exists(this.MountPath))
        return false;

      if (!OperatingSystem.IsWindows() && !_IsMountPoint(this.MountPath))
        return false;

      Directory.EnumerateFileSystemEntries(this.MountPath).Take(1).ToArray();
      return true;
    } catch (Exception) {
      return false; // still coming up
    }
  }

  /// <summary>True when the kernel reports something mounted at this exact path.</summary>
  private static bool _IsMountPoint(string path) {
    try {
      var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
      foreach (var line in File.ReadLines("/proc/mounts")) {
        // fields: source target fstype options ... — the target is octal-escaped
        var fields = line.Split(' ');
        if (fields.Length < 2)
          continue;

        var target = fields[1].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
        if (string.Equals(Path.TrimEndingDirectorySeparator(target), resolved, StringComparison.Ordinal))
          return true;
      }

      return false;
    } catch (Exception) {
      return false;
    }
  }

  public string PathTo(params string[] segments) => Path.Combine([this.MountPath, .. segments]);

  /// <summary>
  /// Everything actually present on the members, relative to each member root. Without this an
  /// assertion about a missing copy says only "the collection was empty", which cannot distinguish
  /// "the engine never wrote it" from "it is there under a name the test did not look for" — a
  /// staged <c>*.TEMP.$DRIVEBENDER</c> that was never published, say.
  /// </summary>
  public string DescribeMembers() {
    var description = new StringBuilder();
    foreach (var member in this.MemberPaths) {
      description.AppendLine($"member '{member}':");
      try {
        var entries = Directory.EnumerateFileSystemEntries(member, "*", SearchOption.AllDirectories)
          .Select(p => Path.GetRelativePath(member, p))
          .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
          .Take(60)
          .ToArray();

        if (entries.Length == 0)
          description.AppendLine("  (empty)");
        foreach (var entry in entries)
          description.AppendLine("  " + entry);
      } catch (Exception e) {
        description.AppendLine($"  <could not enumerate: {e.Message}>");
      }
    }

    return description.ToString();
  }

  /// <summary>The copies of a pool-relative file across every member, primary or shadow container.</summary>
  public IReadOnlyList<(string where, byte[] content)> PhysicalCopies(string relativePath) {
    var found = new List<(string, byte[])>();
    foreach (var member in this.MemberPaths) {
      var primary = Path.Combine(member, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(primary))
        found.Add((primary, File.ReadAllBytes(primary)));

      // shadow copies live beside their parent in a FOLDER.DUPLICATE.$DRIVEBENDER container
      var parent = Path.GetDirectoryName(primary);
      var shadow = parent == null ? null : Path.Combine(parent, "FOLDER.DUPLICATE.$DRIVEBENDER", Path.GetFileName(primary));
      if (shadow != null && File.Exists(shadow))
        found.Add((shadow, File.ReadAllBytes(shadow)));
    }

    return found;
  }

  /// <summary>
  /// Waits for a file to reach the members' real folders.
  ///
  /// A write is acknowledged once the ack quorum is durable, and the remaining copies converge in
  /// the BACKGROUND — that is the product's design, not a defect. On top of that a FUSE release
  /// is asynchronous, so the kernel may not have told the engine the handle is closed by the time
  /// the caller's <c>File.WriteAllBytes</c> has returned. Sampling the members the instant a write
  /// returns therefore tests the timing of the harness, not the behaviour of the pool.
  /// </summary>
  public IReadOnlyList<(string where, byte[] content)> WaitForPhysicalCopies(string relativePath, int atLeast = 1, TimeSpan? timeout = null) {
    var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
    var found = this.PhysicalCopies(relativePath);
    while (found.Count < atLeast && DateTime.UtcNow < deadline) {
      Thread.Sleep(250);
      found = this.PhysicalCopies(relativePath);
    }

    return found;
  }

  /// <summary>Waits for a file to disappear from every member's real folder.</summary>
  public IReadOnlyList<(string where, byte[] content)> WaitForNoPhysicalCopies(string relativePath, TimeSpan? timeout = null) {
    var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
    var found = this.PhysicalCopies(relativePath);
    while (found.Count > 0 && DateTime.UtcNow < deadline) {
      Thread.Sleep(250);
      found = this.PhysicalCopies(relativePath);
    }

    return found;
  }

  public void Dispose() {
    if (this._disposed)
      return;

    this._disposed = true;

    try {
      // ask for a CLEAN unmount first: it flushes owed copies and detaches properly, which is
      // what the teardown assertions about on-disk state depend on
      if (!this._mountProcess.HasExited)
        DbMount.Run(_UNMOUNT_TIMEOUT, "unmount", this.PoolName);
    } catch (Exception) {
      // fall through to the kill below
    }

    try {
      if (!this._mountProcess.WaitForExit((int)_UNMOUNT_TIMEOUT.TotalMilliseconds))
        DbMount.KillTree(this._mountProcess);
    } catch (Exception) {
      DbMount.KillTree(this._mountProcess);
    }

    DbMount.ForgetPool(this.PoolName); // no CLI verb deregisters a pool; the registry entry is ours to remove
    this._mountProcess.Dispose();
    if (this._mountDirectory != null)
      _TryDelete(this._mountDirectory);

    _TryDelete(this.Root);
  }

  private static void _TryDelete(string path) {
    for (var attempt = 0; attempt < 5; ++attempt)
      try {
        if (Directory.Exists(path))
          Directory.Delete(path, true);

        return;
      } catch (Exception) {
        Thread.Sleep(200); // a just-unmounted target can stay busy briefly
      }
  }

}
