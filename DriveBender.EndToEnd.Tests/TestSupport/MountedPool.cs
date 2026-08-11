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

  private Process _mountProcess;
  private StringBuilder _stdout;
  private StringBuilder _stderr;
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
  /// Pool defaults that put a REAL second copy of every file on the second member.
  ///
  /// A test machine has one physical disk, and placement refuses by default to put redundant
  /// copies in one failure domain (SAFE-PHYS) — entirely correct, and it means an unconfigured
  /// two-member test pool stores each file once. Resilience tests need a survivor, so they opt
  /// out of the co-location rule explicitly; that is the ONLY thing relaxed here.
  /// </summary>
  public const string DuplicatedOnOneDisk =
    """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false } }""";

  /// <summary>
  /// Creates a two-member pool under a fresh temp root and mounts it.
  /// </summary>
  /// <param name="poolDefaults">JSON for the manifest's "defaults" block, or null for the built-ins.</param>
  /// <param name="landingZones">How many of the members form the fast tier, taken from the front.</param>
  public static MountedPool Create(int members = 2, string? poolDefaults = null, int landingZones = 0) {
    DriverPrerequisite.RequireAvailable();

    var poolName = "e2e" + Guid.NewGuid().ToString("N")[..10];
    var root = Path.Combine(Path.GetTempPath(), "dbe2e-" + Guid.NewGuid().ToString("N"));
    // Each member is reached through a REPARSE POINT (a junction on Windows, a symlink on Linux)
    // pointing at the real storage beside it. That is what makes a member genuinely ejectable:
    // removing the link detaches the storage without touching the files behind it, so a disk can
    // be pulled from under LIVE I/O — which renaming the directory cannot do, because Windows
    // refuses to rename a directory that has open files beneath it. Junctions need no privileges.
    var memberPaths = new List<string>();
    for (var i = 0; i < members; ++i) {
      var storage = Path.Combine(root, $"store{i}");
      var member = Path.Combine(root, $"m{i}");
      Directory.CreateDirectory(storage);
      _Link(member, storage);
      memberPaths.Add(member);
    }

    var (target, path, createdDirectory) = _ChooseMountTarget(root);

    var arguments = new List<string> { "pool-create", "-n", poolName };
    for (var i = 0; i < memberPaths.Count; ++i) {
      arguments.Add(i < landingZones ? "-l" : "-m"); // the front members form the fast tier
      arguments.Add(memberPaths[i]);
    }

    try {
      DbMount.RunExpectingSuccess(_CLI_TIMEOUT, [.. arguments]);
      if (poolDefaults != null)
        DbMount.SetPoolDefaults(poolName, poolDefaults);
    } catch {
      DbMount.ForgetPool(poolName);
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
  /// A TIERED pool: member 0 is a landing zone (the SSD in a real deployment), member 1 is
  /// capacity. New data lands on the fast tier and the drainer moves it down (FR-LZ-DRAIN).
  /// </summary>
  public static MountedPool CreateTiered() => Create(members: 2, landingZones: 1);

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

  #region storage that goes away and comes back

  private readonly Dictionary<string, string> _ejected = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Creates a directory reparse point — a junction on Windows, a symlink elsewhere.</summary>
  private static void _Link(string link, string target) {
    if (!OperatingSystem.IsWindows()) {
      Directory.CreateSymbolicLink(link, target);
      return;
    }

    // mklink /J makes a junction, which needs no privileges; Directory.CreateSymbolicLink would
    // make a symlink, which on Windows requires elevation or Developer Mode
    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
      FileName = "cmd.exe",
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      ArgumentList = { "/c", "mklink", "/J", link, target },
    })!;

    process.WaitForExit(30_000);
    if (process.ExitCode != 0 || !Directory.Exists(link))
      throw new InvalidOperationException($"Could not create a junction '{link}' -> '{target}'");
  }

  /// <summary>Removes the link itself, leaving the storage behind it untouched.</summary>
  private static void _Unlink(string link) {
    try {
      Directory.Delete(link); // on a reparse point this removes the LINK, never its target
    } catch (IOException) {
      File.Delete(link); // a symlink-to-directory may present as a file
    }
  }

  /// <summary>
  /// Takes a member off the machine, the way pulling a disk or dropping a share does: the path
  /// stops existing, which is exactly what <c>LocalVolumeIO.IsOnline</c> probes for.
  ///
  /// Detaching the LINK rather than moving the directory is what lets this happen while I/O is in
  /// flight: the files the engine holds open live behind the link, and Windows resolves a reparse
  /// point at open time, so removing it neither requires those handles to close nor disturbs
  /// them — exactly like yanking a disk whose files something still has open.
  /// </summary>
  public void Eject(int memberIndex) {
    var member = this.MemberPaths[memberIndex];
    _Unlink(member);
    this._ejected[member] = Path.Combine(this.Root, $"store{memberIndex}");

    // the engine probes reachability on a ~1s cache, so an operation issued in the very next
    // millisecond can still be routed at the member that just vanished. Loss is documented as
    // noticed "within about a second"; the scenarios are about behaviour AFTER that.
    Thread.Sleep(2500);
  }

  /// <summary>
  /// Plugs the storage back in, contents and all.
  ///
  /// The engine RECREATES a missing member's root while it is away — writing pool scaffolding to
  /// the path the disk used to be at. On a real machine that is data landing on whatever
  /// filesystem hosted the mount point rather than on the disk. Whatever it left behind is
  /// discarded here so the returning disk brings back its OWN contents, which is what a disk
  /// coming back actually means.
  /// </summary>
  public void Restore(int memberIndex, TimeSpan? timeout = null) {
    var member = this.MemberPaths[memberIndex];
    if (!this._ejected.TryGetValue(member, out var storage))
      return;

    var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
    while (true)
      try {
        // the engine RECREATES a missing member's root while it is away, so a real directory can
        // be sitting where the link belongs; that scaffolding is discarded, because a disk coming
        // back brings its OWN contents
        if (Directory.Exists(member))
          Directory.Delete(member, recursive: true);

        _Link(member, storage);
        this._ejected.Remove(member);
        return;
      } catch (Exception) when (DateTime.UtcNow < deadline) {
        Thread.Sleep(250);
      }
  }

  /// <summary>
  /// Unmounts cleanly and mounts again at the same target — the durability boundary a user
  /// crosses on every reboot. Anything the pool acknowledged has to be on disk to survive it.
  /// </summary>
  public void Remount() {
    DbMount.Run(_UNMOUNT_TIMEOUT, "unmount", this.PoolName);
    if (!this._mountProcess.WaitForExit((int)_UNMOUNT_TIMEOUT.TotalMilliseconds))
      DbMount.KillTree(this._mountProcess);

    this._mountProcess.WaitForExit(5000);
    this._mountProcess.Dispose();
    this._StartMount();
  }

  /// <summary>
  /// Cuts the power: kills the mount outright — no unmount, no flush, no shutdown hook — and brings
  /// the pool back up on the same members, exactly as a machine that lost power mid-write does.
  ///
  /// This is the boundary that decides whether "we never lose data" is true. A clean unmount can
  /// paper over almost anything by flushing on the way out; only a kill shows what was actually
  /// DURABLE at the moment the lights went off.
  /// </summary>
  public void CrashAndRemount() {
    DbMount.KillTree(this._mountProcess);
    this._mountProcess.WaitForExit(15_000);
    this._mountProcess.Dispose();

    // a FUSE mount whose server was killed leaves a stale entry the kernel still routes at; the
    // lazy unmount detaches it so the same mountpoint can be used again
    if (!OperatingSystem.IsWindows())
      foreach (var (file, arguments) in new[] {
                 ("fusermount3", new[] { "-u", "-z" }),
                 ("fusermount", ["-u", "-z"]),
                 ("umount", ["-l"]),
               }) {
        if (!_IsMountPoint(this.MountPath))
          break;

        try {
          var startInfo = new ProcessStartInfo { FileName = file, UseShellExecute = false, CreateNoWindow = true };
          foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

          startInfo.ArgumentList.Add(this.MountPath);
          Process.Start(startInfo)?.WaitForExit(15_000);
        } catch (Exception) {
          // that tool is not installed; try the next
        }
      }

    this._StartMount();
  }

  private void _StartMount() {
    this._mountProcess = DbMount.Start(["mount", "--manifest", this.PoolName, "-t", this.MountPath, "--foreground"],
      out var stdout, out var stderr);
    this._stdout = stdout;
    this._stderr = stderr;
    this._WaitUntilMounted();
  }

  /// <summary>Waits for a condition the pool reaches on its own (heal, drain, owed-copy sync).</summary>
  public static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null) {
    var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
    while (DateTime.UtcNow < deadline) {
      if (condition())
        return true;

      Thread.Sleep(250);
    }

    return condition();
  }

  #endregion

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

    foreach (var (member, storage) in this._ejected.ToArray())
      try {
        if (!Directory.Exists(member))
          _Link(member, storage); // reattach so the whole tree can be removed
      } catch (Exception) {
        // best effort
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
