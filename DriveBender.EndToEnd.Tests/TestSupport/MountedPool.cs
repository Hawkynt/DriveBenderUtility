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

  /// <summary>Readiness means the OS can actually enumerate it, not merely that a path exists.</summary>
  private bool _IsUsable() {
    try {
      if (!Directory.Exists(this.MountPath))
        return false;

      Directory.EnumerateFileSystemEntries(this.MountPath).Take(1).ToArray();
      return true;
    } catch (Exception) {
      return false; // still coming up
    }
  }

  public string PathTo(params string[] segments) => Path.Combine([this.MountPath, .. segments]);

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
