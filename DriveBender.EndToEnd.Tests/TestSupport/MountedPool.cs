using System.Diagnostics;
using System.Text;
using NUnit.Framework;

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

  /// <summary>
  /// The REAL storage behind each member, in member order — the directory the member's link points
  /// at. Usually a folder beside the link under <see cref="Root"/>, but a member can be placed on
  /// another device entirely, and then it is over there.
  /// </summary>
  public IReadOnlyList<string> StoragePaths { get; }

  public string Root { get; }

  private MountedPool(string poolName, string root, IReadOnlyList<string> members, IReadOnlyList<string> storages,
    string mountTarget, string mountPath,
    string? mountDirectory, Process mountProcess, StringBuilder stdout, StringBuilder stderr) {
    this.PoolName = poolName;
    this.Root = root;
    this.MemberPaths = members;
    this.StoragePaths = storages;
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
  /// <param name="storageDevices">
  /// Where each member's real storage is put, by member index. A null entry (or a short list) means
  /// "beside the link, on whatever device the temp directory is on", which is what every ordinary
  /// scenario wants. Naming a directory on ANOTHER device is what lets a scenario price a genuinely
  /// heterogeneous pool — a fast landing zone with slow capacity behind it — rather than two folders
  /// on one disk, which can only price the code path.
  /// </param>
  /// <param name="storageKinds">
  /// What kind of storage each member sits on, by index — a real device, a simulated one, or both.
  /// A kind with a path puts the member's storage on that device; a kind with a rate limit has the
  /// pool hold the member to it. This is how a scenario builds a pool whose tiers are genuinely
  /// unequal without needing the hardware to be.
  /// </param>
  public static MountedPool Create(int members = 2, string? poolDefaults = null, int landingZones = 0,
    IReadOnlyList<string?>? storageDevices = null, IReadOnlyList<StorageKind?>? storageKinds = null) {
    DriverPrerequisite.RequireAvailable();

    // a kind may carry a device as well as a limit; the explicit device list still wins where given
    if (storageKinds != null && storageDevices == null)
      storageDevices = [.. Enumerable.Range(0, members)
        .Select(i => i < storageKinds.Count ? storageKinds[i]?.Path : null)];

    var poolName = "e2e" + Guid.NewGuid().ToString("N")[..10];
    var root = Path.Combine(Path.GetTempPath(), "dbe2e-" + Guid.NewGuid().ToString("N"));
    // Each member is reached through a REPARSE POINT (a junction on Windows, a symlink on Linux)
    // pointing at the real storage beside it. That is what makes a member genuinely ejectable:
    // removing the link detaches the storage without touching the files behind it, so a disk can
    // be pulled from under LIVE I/O — which renaming the directory cannot do, because Windows
    // refuses to rename a directory that has open files beneath it. Junctions need no privileges.
    // the root has to exist before the first LINK is made inside it. It used to appear as a side
    // effect of creating "root/store0"; a member whose storage lives on another device creates
    // nothing here, and the link then failed with a directory-not-found that named the link rather
    // than the missing parent.
    Directory.CreateDirectory(root);

    var memberPaths = new List<string>();
    var storagePaths = new List<string>();
    string target, path;
    string? createdDirectory;
    try {
      for (var i = 0; i < members; ++i) {
        var device = storageDevices != null && i < storageDevices.Count ? storageDevices[i] : null;
        // an off-device storage gets its own uniquely named folder, because that device is shared
        // with whatever else is on it and outlives this pool
        var storage = device == null
          ? Path.Combine(root, $"store{i}")
          : Path.Combine(device, $"dbe2e-{Path.GetFileName(root)[6..]}-{i}");

        var member = Path.Combine(root, $"m{i}");
        Directory.CreateDirectory(storage);
        storagePaths.Add(storage); // recorded BEFORE the link, so a failed link still gets cleaned up
        _Link(member, storage);
        memberPaths.Add(member);
      }

      (target, path, createdDirectory) = _ChooseMountTarget(root);
    } catch {
      // storage on ANOTHER device is not under the root, so removing the root would leave it there
      foreach (var storage in storagePaths)
        _TryDelete(storage);

      _TryDelete(root);
      throw;
    }

    var arguments = new List<string> { "pool-create", "-n", poolName };
    for (var i = 0; i < memberPaths.Count; ++i) {
      arguments.Add(i < landingZones ? "-l" : "-m"); // the front members form the fast tier
      arguments.Add(memberPaths[i]);
    }

    try {
      DbMount.RunExpectingSuccess(_CLI_TIMEOUT, [.. arguments]);
      if (poolDefaults != null)
        DbMount.SetPoolDefaults(poolName, poolDefaults);

      // rate limits go on BEFORE the first mount: they are read from the manifest when the engine
      // builds its members, so setting them under a live pool would change nothing
      for (var i = 0; storageKinds != null && i < memberPaths.Count && i < storageKinds.Count; ++i)
        if (storageKinds[i] is { IsThrottled: true } kind)
          DbMount.SetMemberLimits(poolName, memberPaths[i], kind.MaxIops, kind.MaxThroughput);
    } catch {
      DbMount.ForgetPool(poolName);
      foreach (var storage in storagePaths)
        _TryDelete(storage);

      _TryDelete(root);
      throw;
    }

    var mountProcess = DbMount.Start(["mount", "--manifest", poolName, "-t", target, "--foreground"], out var stdout, out var stderr);
    var pool = new MountedPool(poolName, root, memberPaths, storagePaths, target, path, createdDirectory, mountProcess, stdout, stderr);
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
  /// A tiered pool whose fast tier is accepted however full the HOST disk is.
  ///
  /// Placement declines a landing zone once it is past its low watermark, which is right, and which
  /// on a CI runner with a nearly-full disk means every write goes straight to capacity and nothing
  /// ever drains. Scenarios about the drainer then have no drain to observe — and skipping them on
  /// that basis makes the suite's own results depend on the host's free space, which is precisely
  /// the kind of run-to-run variation that stops a generated coverage matrix from ever converging.
  ///
  /// Raising the watermarks is not a workaround for a defect; it configures away a condition of the
  /// host so the scenario tests the drainer rather than the runner.
  /// </summary>
  public static MountedPool CreateTieredAlwaysLanding() => Create(members: 2, landingZones: 1,
    poolDefaults: """
      { "tiers": { "fast": { "highWatermark": "100%", "lowWatermark": "99%" } } }
      """);

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

  /// <summary>
  /// Whether the mount child is still running.
  ///
  /// Worth asserting explicitly in any test that waits on the pool converging by itself, because
  /// those tests watch the MEMBER folders rather than the mount: if the mount process dies, nothing
  /// converges and the wait simply times out, which reads as "the background job is broken" when
  /// the real answer is "the process that runs it is gone".
  /// </summary>
  public bool IsMountAlive => !this._mountProcess.HasExited;

  /// <summary>
  /// The largest resident memory the mount process has reached, for tests that assert the engine
  /// STREAMS rather than materialising. A whole-file buffer shows up here and nowhere else.
  /// </summary>
  public long PeakWorkingSetBytes {
    get {
      try {
        this._mountProcess.Refresh();
        return this._mountProcess.PeakWorkingSet64;
      } catch (Exception) {
        return 0; // the process is gone; the caller's other assertions will say so
      }
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
  /// <summary>
  /// Skips the test unless the file really did land on the LANDING ZONE (member 0 of a tiered pool).
  ///
  /// Placement is free to decline the fast tier — and does, once that member is past its low
  /// watermark, which on a CI runner's nearly-full disk is the normal state rather than the
  /// exception. The write then goes straight to capacity, correctly, and there is simply nothing for
  /// the drainer to move. Any scenario about interrupting, reading beside, or shutting down under a
  /// drain has no drain to work with in that case, and should say so rather than fail as though the
  /// pool had misbehaved.
  /// </summary>
  public void RequireLandedOnFastTier(string relativePath) {
    if (File.Exists(Path.Combine(this.MemberPaths[0], relativePath.Replace('/', Path.DirectorySeparatorChar))))
      return;

    Assert.Ignore(
      $"'{relativePath}' did not land on the landing zone, so no drain follows and this scenario has "
      + $"nothing to observe. Placement declines a fast tier past its low watermark, which is what a "
      + $"host with little free space looks like — correct behaviour, wrong conditions for this test."
      + $"{Environment.NewLine}{this.DescribeMembers()}");
  }

  /// <summary>
  /// Half-written staging files on the members — the pool's own bookkeeping mid-copy.
  ///
  /// The only honest way to know a background copy is IN FLIGHT rather than finished or not yet
  /// started, which is what scenarios about interrupting one have to establish.
  /// </summary>
  public IReadOnlyList<string> StagingFiles()
    => [.. this.MemberPaths
      .Where(Directory.Exists)
      .SelectMany(m => Directory.EnumerateFiles(m, "*.TEMP.$DRIVEBENDER", SearchOption.AllDirectories))];

  public IReadOnlyList<(string where, byte[] content)> PhysicalCopies(string relativePath) {
    var found = new List<(string, byte[])>();
    foreach (var member in this.MemberPaths) {
      var primary = Path.Combine(member, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (_TryReadShared(primary, out var primaryContent))
        found.Add((primary, primaryContent));

      // shadow copies live beside their parent in a FOLDER.DUPLICATE.$DRIVEBENDER container
      var parent = Path.GetDirectoryName(primary);
      var shadow = parent == null ? null : Path.Combine(parent, "FOLDER.DUPLICATE.$DRIVEBENDER", Path.GetFileName(primary));
      if (shadow != null && _TryReadShared(shadow, out var shadowContent))
        found.Add((shadow, shadowContent));
    }

    return found;
  }

  /// <summary>
  /// Reads a member's file the way an observer must: sharing everything.
  ///
  /// The engine POOLS open handles into its members, so a copy that is merely idle is still held
  /// open — and <see cref="File.ReadAllBytes"/> asks for a share mode the engine's handle refuses.
  /// A test that used it would report "the copy is not there" for a file plainly sitting on disk.
  /// </summary>
  private static bool _TryReadShared(string path, out byte[] content) {
    content = [];
    try {
      if (!File.Exists(path))
        return false;

      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 1 << 16);
      using var buffer = new MemoryStream();
      stream.CopyTo(buffer);
      content = buffer.ToArray();
      return true;
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }
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

  /// <summary>
  /// The real storage behind a member, reachable even while the member itself is detached — which
  /// is how a test can prove a write did NOT reach a disk that was away.
  /// </summary>
  public IReadOnlyList<byte[]> CopiesOnDetachedStorage(int memberIndex, string relativePath) {
    var storage = this.StoragePaths[memberIndex];
    var primary = Path.Combine(storage, relativePath.Replace('/', Path.DirectorySeparatorChar));
    var parent = Path.GetDirectoryName(primary);
    var shadow = parent == null ? null : Path.Combine(parent, "FOLDER.DUPLICATE.$DRIVEBENDER", Path.GetFileName(primary));

    var found = new List<byte[]>();
    foreach (var candidate in new[] { primary, shadow })
      if (candidate != null && _TryReadShared(candidate, out var content))
        found.Add(content);

    return found;
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
    this._ejected[member] = this.StoragePaths[memberIndex];

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
  /// Makes a member FAIL every operation while still being there — the dying disk, as opposed to
  /// the pulled one.
  ///
  /// The two are different failures and the engine treats them differently: a member that vanishes
  /// is filtered out of placement by the online probe and costs nothing afterwards, while a member
  /// that answers every request with an error stays in the rotation and has to be routed around one
  /// failure at a time. That second case is where a pool-wide stall comes from, and only this can
  /// produce it. Revoking the permission bits on the storage root is the closest a test without root
  /// can get: the path still exists, so the member is still "online", and every open beneath it
  /// fails the way a disk with a dead controller does.
  ///
  /// Handles the engine already has stay valid — POSIX checks permission at open — so a member is
  /// most convincingly crippled just after a remount, when nothing is held open.
  /// </summary>
  public bool Cripple(int memberIndex) {
    if (OperatingSystem.IsWindows())
      return false; // an ACL denial would be the equivalent, and needs a different apparatus

    try {
      var storage = this.StoragePaths[memberIndex];
      File.SetUnixFileMode(storage, UnixFileMode.None);
      this._crippled[memberIndex] = true;
      return (File.GetUnixFileMode(storage) & UnixFileMode.UserRead) == 0;
    } catch (Exception) {
      return false; // a filesystem without permission bits (vfat, say) cannot be crippled this way
    }
  }

  /// <summary>
  /// Makes a member READ-ONLY while leaving it perfectly readable — the way a filesystem that hits
  /// an I/O error remounts itself.
  ///
  /// This is a third failure shape, and the one most likely to be met in the field. A member that
  /// vanishes is filtered out by the online probe; one that fails everything is routed around by the
  /// fault cooldown; a member that answers every READ correctly and refuses every WRITE does neither
  /// — it looks healthy to anything that only reads, and it is still holding the pool's data, so it
  /// must keep being read from while nothing new is placed on it.
  /// </summary>
  public bool MakeReadOnly(int memberIndex) {
    if (OperatingSystem.IsWindows())
      return false; // a deny-write ACL is the equivalent and needs a different apparatus

    try {
      var storage = this.StoragePaths[memberIndex];
      const UnixFileMode readable = UnixFileMode.UserRead | UnixFileMode.UserExecute
                                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

      // the whole tree, not just the root: the engine writes into subfolders it has already opened
      foreach (var directory in Directory.EnumerateDirectories(storage, "*", SearchOption.AllDirectories))
        File.SetUnixFileMode(directory, readable);

      File.SetUnixFileMode(storage, readable);
      this._crippled[memberIndex] = true; // Dispose restores it either way
      return (File.GetUnixFileMode(storage) & UnixFileMode.UserWrite) == 0;
    } catch (Exception) {
      return false; // a filesystem without permission bits (vfat) cannot be made read-only this way
    }
  }

  /// <summary>Gives a crippled member its permissions back, the way a controller reset would.</summary>
  public void Uncripple(int memberIndex) {
    if (!this._crippled.Remove(memberIndex, out _))
      return;

    try {
      const UnixFileMode writable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

      var storage = this.StoragePaths[memberIndex];
      File.SetUnixFileMode(storage, writable);
      // a read-only member had its whole tree stripped, so the whole tree is restored — otherwise
      // the temp root cannot be removed and every later run inherits the leftovers
      foreach (var directory in Directory.EnumerateDirectories(storage, "*", SearchOption.AllDirectories))
        File.SetUnixFileMode(directory, writable);
    } catch (Exception) {
      // best effort; Dispose tries again
    }
  }

  private readonly Dictionary<int, bool> _crippled = [];

  /// <summary>
  /// Unmounts cleanly and mounts again at the same target — the durability boundary a user
  /// crosses on every reboot. Anything the pool acknowledged has to be on disk to survive it.
  /// </summary>
  public void Remount() {
    this._Unmount();
    this._StartMount();
  }

  /// <summary>
  /// Runs something against the members' real folders with the pool CLEANLY UNMOUNTED, then brings
  /// it back.
  ///
  /// Anything that manipulates stored bytes behind the pool's back — simulating bit-rot, an
  /// out-of-band edit, a restored backup — has to happen with nothing mounted. The engine pools
  /// open handles into its members, so touching a file under a live mount tests the handle cache
  /// rather than the stored data, and on Windows is simply refused.
  /// </summary>
  public void WhileUnmounted(Action work) {
    this._Unmount();
    try {
      work();
    } finally {
      this._StartMount();
    }
  }

  private void _Unmount() {
    DbMount.Run(_UNMOUNT_TIMEOUT, "unmount", this.PoolName);
    if (!this._mountProcess.WaitForExit((int)_UNMOUNT_TIMEOUT.TotalMilliseconds))
      DbMount.KillTree(this._mountProcess);

    this._mountProcess.WaitForExit(5000);
    this._mountProcess.Dispose();
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

    this._DetachStaleMount();
    this._StartMount();
  }

  /// <summary>
  /// Detaches a FUSE mount whose server is gone.
  ///
  /// Killing the mount process does not remove the kernel's mount entry: the mountpoint stays in
  /// <c>/proc/mounts</c> and answers every access with "transport endpoint is not connected" until
  /// something unmounts it. Nothing did, on any path but a deliberate crash — so every pool this
  /// harness built left one behind, and a day of runs left the machine carrying dozens of dead
  /// mounts and the directories under them, which also stopped the temp root from being removed.
  /// The lazy flavour is the one that works here, because the entry being detached is already dead.
  /// </summary>
  private void _DetachStaleMount() {
    if (OperatingSystem.IsWindows())
      return;

    foreach (var (file, arguments) in new[] {
               ("fusermount3", new[] { "-u", "-z" }),
               ("fusermount", ["-u", "-z"]),
               ("umount", ["-l"]),
             }) {
      if (!_IsMountPoint(this.MountPath))
        return;

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

    // a storage root with its permissions revoked cannot be enumerated, let alone deleted
    foreach (var member in this._crippled.Keys.ToArray())
      this.Uncripple(member);

    // Teardown is timed and reported when it drags, because a slow one is invisible otherwise: it
    // is charged to whichever scenario happened to own the pool, so a minute spent here reads as a
    // slow TEST rather than a slow unmount, and a whole suite can be paced by it without anyone
    // seeing why.
    var teardown = Stopwatch.StartNew();
    var verb = TimeSpan.Zero;
    var refusal = "";
    var killed = false;
    try {
      // ask for a CLEAN unmount first: it flushes owed copies and detaches properly, which is
      // what the teardown assertions about on-disk state depend on
      if (!this._mountProcess.HasExited) {
        var started = teardown.Elapsed;
        var result = DbMount.Run(_UNMOUNT_TIMEOUT, "unmount", this.PoolName);
        verb = teardown.Elapsed - started;

        // A FAILED unmount used to be discarded here, and that is how a product defect hid inside
        // the harness for the length of a suite: the verb answered "No mounted pool matches" and
        // exited non-zero, the pool stayed mounted, and the only visible symptom was that teardown
        // took the full timeout and then killed the process — which reads as a slow test.
        if (!result.Succeeded)
          refusal = $", and the unmount verb REFUSED with exit {result.ExitCode}: {result.Output.Trim()}";
      }
    } catch (Exception) {
      // fall through to the kill below
    }

    try {
      if (!this._mountProcess.WaitForExit((int)_UNMOUNT_TIMEOUT.TotalMilliseconds)) {
        DbMount.KillTree(this._mountProcess);
        killed = true;
      }
    } catch (Exception) {
      DbMount.KillTree(this._mountProcess);
      killed = true;
    }

    teardown.Stop();
    if (teardown.Elapsed > TimeSpan.FromSeconds(5) || refusal.Length > 0)
      TestContext.Out.WriteLine(
        $"[teardown] pool '{this.PoolName}' took {teardown.Elapsed.TotalSeconds:F1}s to shut down "
        + $"(unmount verb {verb.TotalSeconds:F1}s{(killed ? ", then the process had to be killed" : "")}{refusal}).");

    foreach (var (member, storage) in this._ejected.ToArray())
      try {
        if (!Directory.Exists(member))
          _Link(member, storage); // reattach so the whole tree can be removed
      } catch (Exception) {
        // best effort
      }

    DbMount.ForgetPool(this.PoolName); // no CLI verb deregisters a pool; the registry entry is ours to remove
    this._mountProcess.Dispose();
    this._DetachStaleMount(); // the kernel entry outlives the killed server; without this it accumulates
    if (this._mountDirectory != null)
      _TryDelete(this._mountDirectory);

    _TryDelete(this.Root);

    // storage placed on ANOTHER device is not under Root, so removing the root leaves it behind —
    // on a small removable disk that is a few runs away from filling it up
    foreach (var storage in this.StoragePaths)
      if (!storage.StartsWith(this.Root, StringComparison.Ordinal))
        _TryDelete(storage);
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
