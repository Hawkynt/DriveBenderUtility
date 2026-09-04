using System.Diagnostics;

namespace DivisonM.EndToEnd.Tests.TestSupport;

/// <summary>One writable scratch directory on one device, with what that device measured at.</summary>
public sealed record StorageDevice(string Path, string DeviceId, double WriteBytesPerSecond) {
  public double WriteMiBPerSecond => this.WriteBytesPerSecond / (1024 * 1024);
  public override string ToString() => $"'{this.Path}' ({this.WriteMiBPerSecond:F1} MiB/s written)";
}

/// <summary>
/// Every storage device this machine can lend the suite, ranked by what it actually writes at.
///
/// The suite's default home is the temp directory, and on a machine where that is <c>tmpfs</c> it is
/// RAM — so "both members on one disk" is not even one disk, and no scenario run there can say
/// anything about devices. Discovering the real ones is what lets a scenario put a member on THIS
/// disk and a member on THAT one and then mean something by the difference.
///
/// Devices are ranked by measurement rather than by their names, because a second internal NVMe and
/// a card reader are both mounted under <c>/run/media</c> and are indistinguishable by path.
/// </summary>
public static class StorageDevices {

  private static readonly Lazy<IReadOnlyList<StorageDevice>> _devices = new(_Discover);

  /// <summary>Bytes a scenario must be able to put on a device before it is worth using.</summary>
  public const long MINIMUM_FREE_BYTES = 48L * 1024 * 1024;

  /// <summary>Everything usable, slowest first. Empty on a machine with nothing but its system disk.</summary>
  public static IReadOnlyList<StorageDevice> All => _devices.Value;

  /// <summary>The slowest usable device — what a scenario about a slow tier wants.</summary>
  public static StorageDevice? Slowest => All.Count > 0 ? All[0] : null;

  /// <summary>
  /// The fastest usable devices, fastest first, no two of them the same physical device.
  ///
  /// A scenario claiming that two storages COMBINE their throughput is worthless unless the two are
  /// genuinely separate, so the device id is what makes them distinct here rather than the path.
  /// </summary>
  public static IReadOnlyList<StorageDevice> Fastest => [.. All.Reverse()];

  /// <summary>Free space on the filesystem holding a path — the PATH on Unix, the drive root on Windows.</summary>
  public static long FreeBytesOf(string path) {
    try {
      return new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(path)! : path).AvailableFreeSpace;
    } catch (Exception) {
      return 0;
    }
  }

  /// <summary>The kernel's device id for whatever filesystem holds a path, or null when unknown.</summary>
  public static string? DeviceIdOf(string path) {
    if (OperatingSystem.IsWindows())
      return Path.GetPathRoot(path)?.ToUpperInvariant();

    try {
      // `stat -c %d` is the device id, and comparing it is how "the same disk?" is actually answered
      var process = Process.Start(new ProcessStartInfo {
        FileName = "stat",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList = { "-c", "%d", path },
      });

      if (process == null)
        return null;

      var output = process.StandardOutput.ReadToEnd().Trim();
      process.WaitForExit(5000);
      return process.ExitCode == 0 && output.Length > 0 ? output : null;
    } catch (Exception) {
      return null;
    }
  }

  private static IReadOnlyList<StorageDevice> _Discover() {
    var found = new List<StorageDevice>();
    var seen = new HashSet<string>(StringComparer.Ordinal);

    // the temp directory is where an ordinary pool already lives, so a candidate on that same
    // device is not a SECOND device and a scenario built on it would be measuring one disk twice
    if (DeviceIdOf(Path.GetTempPath()) is { } temporary)
      seen.Add(temporary);

    foreach (var candidate in _Candidates()) {
      if (!_Usable(candidate))
        continue;

      var deviceId = DeviceIdOf(candidate);
      if (deviceId == null || !seen.Add(deviceId))
        continue; // the same disk under a second mount point would prove nothing

      var rate = ProbeWriteRate(candidate, 4 * 1024 * 1024);
      if (rate > 0)
        found.Add(new(candidate, deviceId, rate));
    }

    found.Sort((left, right) => left.WriteBytesPerSecond.CompareTo(right.WriteBytesPerSecond));
    return found;
  }

  /// <summary>
  /// Directories that might sit on a device of their own.
  ///
  /// <c>DBE2E_DEVICES</c> overrides the search with an explicit list, which is how a machine whose
  /// disks are mounted somewhere unusual — or a CI runner with a deliberately provisioned second
  /// volume — says so without this having to guess.
  /// </summary>
  private static IEnumerable<string> _Candidates() {
    if (Environment.GetEnvironmentVariable("DBE2E_DEVICES") is { Length: > 0 } configured) {
      foreach (var path in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        yield return path;

      yield break;
    }

    if (OperatingSystem.IsWindows()) {
      foreach (var drive in _WindowsDrives())
        yield return drive;

      yield break;
    }

    // a scratch directory on the system disk, which on a tmpfs-temp machine is a DIFFERENT device
    // from the temp directory and frequently the only second one there is
    yield return "/var/tmp";

    string[] lines;
    try {
      lines = File.ReadAllLines("/proc/mounts");
    } catch (Exception) {
      lines = [];
    }

    foreach (var line in lines) {
      var fields = line.Split(' ');
      if (fields.Length < 3)
        continue;

      var source = fields[0];
      var target = fields[1].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
      var type = fields[2];

      if (!source.StartsWith("/dev/", StringComparison.Ordinal))
        continue;

      if (type is "swap" or "devtmpfs" or "tmpfs")
        continue;

      // removable media land under a per-user directory; a filesystem mounted anywhere else is part
      // of the running system and not somewhere to drop scratch data
      if (!target.StartsWith("/run/media/", StringComparison.Ordinal)
          && !target.StartsWith("/media/", StringComparison.Ordinal)
          && !target.StartsWith("/mnt/", StringComparison.Ordinal))
        continue;

      yield return target;
    }
  }

  private static IEnumerable<string> _WindowsDrives() {
    DriveInfo[] drives;
    try {
      drives = DriveInfo.GetDrives();
    } catch (Exception) {
      yield break;
    }

    foreach (var drive in drives) {
      var usable = false;
      try {
        usable = drive is { IsReady: true, DriveType: DriveType.Removable or DriveType.Fixed };
      } catch (Exception) {
        // a card reader with no card in it throws on IsReady
      }

      if (usable)
        yield return drive.RootDirectory.FullName;
    }
  }

  /// <summary>Writable and roomy enough to be worth putting a pool member on.</summary>
  private static bool _Usable(string path) {
    try {
      if (!Directory.Exists(path))
        return false;

      var probe = Path.Combine(path, "dbe2e-writable-" + Guid.NewGuid().ToString("N")[..8]);
      Directory.CreateDirectory(probe);
      Directory.Delete(probe);

      return FreeBytesOf(path) >= MINIMUM_FREE_BYTES;
    } catch (Exception) {
      return false; // not writable, or not a filesystem we can ask about
    }
  }

  /// <summary>
  /// A flushed write, timed.
  ///
  /// The flush is the whole point: without it the number is the page cache's, which on a 4 MiB/s
  /// card reads back as gigabytes per second — the measurement mistake that would rank a card ahead
  /// of an NVMe and make every assertion built on the ranking vacuous.
  /// </summary>
  public static double ProbeWriteRate(string path, int size) {
    var probe = Path.Combine(path, "dbe2e-probe-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
    try {
      var payload = new byte[size];
      var stopwatch = Stopwatch.StartNew();
      using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20)) {
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
      }

      stopwatch.Stop();
      return stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : size / stopwatch.Elapsed.TotalSeconds;
    } catch (Exception) {
      return 0;
    } finally {
      try {
        File.Delete(probe);
      } catch (Exception) {
        // best effort
      }
    }
  }

}
