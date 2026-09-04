namespace DivisonM.EndToEnd.Tests.TestSupport;

/// <summary>
/// One kind of storage a pool member can sit on, whether that is a REAL device or a simulated one.
///
/// The two are complementary and neither is sufficient alone. A real device is the honest evidence —
/// an SD card really does write at four megabytes a second and really does fill up — but a machine
/// has whatever disks it happens to have, they differ between hosts, and their measured speed wanders
/// with whatever else is running, so a real device cannot be the basis of a repeatable matrix. A
/// SIMULATED device is the product's own <c>maxIops</c>/<c>maxThroughput</c> limit applied to ordinary
/// storage: identical on every machine and every run, available in CI, and able to model hardware
/// nobody has to hand. The scenarios below run over both.
///
/// <see cref="Path"/> is null for "wherever the pool's own temp root is", which on this suite's usual
/// host is tmpfs — RAM, and therefore the fastest thing available.
/// </summary>
public sealed record StorageKind(string Name, string? Path, int MaxIops, long MaxThroughput) {

  private const long _KIB = 1024;
  private const long _MIB = 1024 * _KIB;

  /// <summary>Whether the pool has to be told to hold this member to a rate.</summary>
  public bool IsThrottled => this.MaxIops > 0 || this.MaxThroughput > 0;

  public override string ToString() => this.Name;

  #region simulated storage — the product's own rate limits, so every host sees the same thing

  /// <summary>Unlimited, on whatever the temp directory is. The control every other kind is read against.</summary>
  public static readonly StorageKind Ram = new("RAM", null, 0, 0);

  /// <summary>A fast solid-state disk: no seek cost worth modelling, a wide but finite pipe.</summary>
  public static readonly StorageKind SimulatedSsd = new("SSD (simulated)", null, 0, 400 * _MIB);

  /// <summary>
  /// A mechanical disk: the operations budget is what runs out first, which is the whole difference
  /// between spinning rust and everything else.
  /// </summary>
  public static readonly StorageKind SimulatedHardDisk = new("HDD (simulated)", null, 240, 140 * _MIB);

  /// <summary>Removable flash: a narrow pipe and a modest operation budget, like the card in the reader.</summary>
  public static readonly StorageKind SimulatedSdCard = new("SD card (simulated)", null, 90, 16 * _MIB);

  /// <summary>A remote endpoint: slow, and metered by operation as much as by byte.</summary>
  public static readonly StorageKind SimulatedCloud = new("cloud (simulated)", null, 25, 6 * _MIB);

  #endregion

  /// <summary>A real device found on this machine, unthrottled — the honest half of the evidence.</summary>
  public static StorageKind Real(string name, string path) => new(name, path, 0, 0);

  /// <summary>
  /// Every REAL device this machine offers, fastest first, with the temp directory included as the
  /// baseline. Two entries at most beyond the baseline on an ordinary desk; none in CI.
  /// </summary>
  public static IReadOnlyList<StorageKind> RealDevices {
    get {
      var kinds = new List<StorageKind> { Ram with { Name = "temp directory" } };
      foreach (var device in StorageDevices.Fastest)
        // The path, and NOTHING MEASURED. A parameterised scenario's name becomes its identity —
        // it is what the generated coverage matrix lists and what any history is keyed on — so a
        // measured rate in it renames the case on every run. That is not cosmetic: CI regenerates
        // the matrix from the results and commits it when the content changed, so a name that
        // changes every run means a fresh commit every run, which resets the branch's checks and
        // never converges. The rate belongs in the scenario's OUTPUT, where it is evidence rather
        // than identity.
        //
        // The whole path rather than its last segment, because "/var/tmp" and "/tmp" both end in
        // "tmp" and a case named for the wrong one of those is worse than a long name.
        kinds.Add(Real(device.Path, device.Path));

      return kinds;
    }
  }

}
