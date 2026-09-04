using System.Diagnostics;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests.TestSupport;

/// <summary>
/// The machine's SLOWEST storage device, for the scenarios that need a genuinely slow disk.
///
/// Every other scenario in this suite puts both members in the temp directory, which is right for
/// correctness and mute about tiering: a landing zone in front of capacity storage that is the same
/// device measures the code path and nothing else — <c>docs/Performance.md</c> says so in as many
/// words, and on a host whose temp directory is <c>tmpfs</c> that device is not even a disk. A pool
/// only earns its tiering when the fast tier is genuinely faster, and only a second, slower device
/// can show that.
///
/// The device is found and MEASURED, never assumed. When the machine has none, the scenarios that
/// need one ignore themselves rather than failing — a CI runner has one disk and always will.
/// </summary>
public static class SlowDevice {

  private static readonly Lazy<(double write, double read)> _rates = new(_MeasureRates);

  /// <summary>A device is only "slow" if it is meaningfully slower than the disk this suite normally uses.</summary>
  private const double _MAXIMUM_SLOW_DEVICE_MIB_PER_SECOND = 120.0;

  /// <summary>A writable scratch directory on the slowest device, or null when this machine has none.</summary>
  public static string? PathOrNull => StorageDevices.Slowest?.Path;

  /// <summary>
  /// What the device actually writes at, in bytes per second, measured with an fsync at the end so
  /// the number is the DEVICE and not the page cache.
  ///
  /// Scenarios assert against this rather than against a constant: "faster than the slow disk" is a
  /// claim that survives being run on someone else's hardware, "faster than 40 MiB/s" is not.
  /// </summary>
  public static double WriteBytesPerSecond => _rates.Value.write;

  /// <summary>
  /// What the device actually READS at, with its page cache evicted first.
  ///
  /// Kept separate from the write rate because on removable media the two are nothing alike — a card
  /// that writes at 4 MiB/s commonly reads at five times that. A read assertion measured against the
  /// WRITE number would pass comfortably while the engine served every byte from the slow disk,
  /// which is the exact failure it was written to catch.
  /// </summary>
  public static double ReadBytesPerSecond => _rates.Value.read;

  /// <summary>How much room is left on the device, for scenarios that size their working set to fit.</summary>
  public static long FreeBytes => PathOrNull is { } path ? StorageDevices.FreeBytesOf(path) : 0;

  /// <summary>Skips the calling scenario unless this machine really has a second, slower device.</summary>
  public static string RequireAvailable() {
    var path = PathOrNull;
    if (path == null)
      Assert.Ignore(
        "This scenario needs a SECOND, slower storage device, and this machine has none mounted. "
        + "Set DBE2E_DEVICES to writable directories on the devices to use (a USB stick, an SD card, "
        + $"a spinning disk), each with at least {StorageDevices.MINIMUM_FREE_BYTES / (1024 * 1024)} MiB free.");

    return path!;
  }

  /// <summary>
  /// Skips unless the device is not merely a second disk but a genuinely SLOW one, which is what the
  /// tiering scenarios are about. A second NVMe would pass <see cref="RequireAvailable"/> and then
  /// quietly make every "the fast tier absorbed it" assertion meaningless.
  /// </summary>
  public static string RequireSlow() {
    var path = RequireAvailable();
    var rate = WriteBytesPerSecond;
    if (rate <= 0)
      Assert.Ignore($"Could not measure the write rate of '{path}', so nothing can be asserted against it.");

    var mibPerSecond = rate / (1024 * 1024);
    if (mibPerSecond > _MAXIMUM_SLOW_DEVICE_MIB_PER_SECOND)
      Assert.Ignore(
        $"'{path}' writes at {mibPerSecond:F0} MiB/s, which is not a SLOW device — a tiering claim "
        + "measured against it would not mean anything.");

    return path;
  }

  /// <summary>A one-line description for assertion messages, so a failure says what it ran against.</summary>
  public static string Describe()
    => PathOrNull is { } path
      ? $"slow device '{path}': {WriteBytesPerSecond / (1024 * 1024):F1} MiB/s written, "
        + $"{ReadBytesPerSecond / (1024 * 1024):F1} MiB/s read (page cache evicted)"
      : "no slow device";

  /// <summary>
  /// Writes 8 MiB flushed to the platter, then reads it back with the page cache evicted, timing
  /// each separately. Both defences matter and for the same reason: without the flush the write
  /// number is the page cache's, and without the eviction the read number is.
  /// </summary>
  private static (double write, double read) _MeasureRates() {
    var path = PathOrNull;
    if (path == null)
      return (0, 0);

    const int size = 8 * 1024 * 1024;
    var probe = Path.Combine(path, "dbe2e-rate-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
    try {
      var payload = new byte[size];
      Random.Shared.NextBytes(payload);

      var stopwatch = Stopwatch.StartNew();
      using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20)) {
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
      }

      stopwatch.Stop();
      var write = stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : size / stopwatch.Elapsed.TotalSeconds;

      // an unevicted read here would report RAM, and every scenario comparing against it would pass
      // no matter what the engine did
      if (!PageCache.Drop(probe))
        return (write, 0);

      var buffer = new byte[1 << 20];
      stopwatch.Restart();
      using (var stream = new FileStream(probe, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) {
        while (stream.Read(buffer, 0, buffer.Length) > 0) {
          // the timing is the point; the bytes were already proven by the write
        }
      }

      stopwatch.Stop();
      var read = stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : size / stopwatch.Elapsed.TotalSeconds;
      return (write, read);
    } catch (Exception) {
      return (0, 0);
    } finally {
      try {
        File.Delete(probe);
      } catch (Exception) {
        // best effort
      }
    }
  }

}
