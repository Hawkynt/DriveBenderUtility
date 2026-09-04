namespace DivisonM.Vfs.Engine;

/// <summary>
/// How much life a device looks to have left. Ordered by severity, so a pool's worst member is
/// <c>Max</c> over its members.
/// </summary>
public enum DiskHealth {
  /// <summary>Nothing could be read — no smartctl, no permission, a device that does not answer.</summary>
  Unknown,

  Healthy,

  /// <summary>Wear that is normal but worth watching: running warm, a good part of its rated life used.</summary>
  Aging,

  /// <summary>Real degradation: sectors already reallocated, media errors logged, endurance nearly spent.</summary>
  Warning,

  /// <summary>Get the data off it: the drive failed its own assessment, has sectors pending, or is out of spares.</summary>
  Failing,
}

/// <summary>SMART snapshot for one physical device (§6.15, G16 health monitoring).</summary>
public sealed record SmartStatus(
  string Device,
  DiskHealth Health,
  int? TemperatureCelsius,
  long? ReallocatedSectors,
  long? PendingSectors,
  int? PowerOnHours,
  string? Model,
  string? Detail,

  /// <summary>NVMe: percentage of the drive's rated write endurance consumed (100 means spent).</summary>
  int? PercentageUsed = null,

  /// <summary>NVMe: spare blocks left, as a percentage; below the drive's own threshold it is failing.</summary>
  int? AvailableSparePercent = null,

  /// <summary>NVMe: uncorrectable media errors the controller has logged.</summary>
  long? MediaErrors = null
) {
  public static SmartStatus Unavailable(string device, string? detail = null)
    => new(device, DiskHealth.Unknown, null, null, null, null, null, detail ?? "SMART not available");
}

/// <summary>Reports SMART health for the physical device backing a path; a fake stands in for headless tests.</summary>
public interface ISmartMonitor {
  bool IsSupported { get; }
  SmartStatus Query(string physicalPathOrDevice);
}

/// <summary>
/// Real SMART monitor: shells to <c>smartctl</c> (smartmontools) where present — the one
/// portable source that works on Windows and Linux — and parses its JSON. Absent
/// smartctl, health is reported Unknown rather than guessed.
/// </summary>
public sealed class SmartctlMonitor : ISmartMonitor {

  private readonly string? _smartctl = _Locate();

  public bool IsSupported => this._smartctl != null;

  private static string? _Locate() {
    var names = OperatingSystem.IsWindows() ? new[] { "smartctl.exe" } : ["smartctl"];
    var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
      .Concat(OperatingSystem.IsWindows()
        ? [@"C:\Program Files\smartmontools\bin", @"C:\Program Files (x86)\smartmontools\bin"]
        : ["/usr/sbin", "/usr/bin", "/sbin"]);

    foreach (var dir in dirs)
    foreach (var name in names) {
      try {
        var candidate = Path.Combine(dir, name);
        if (File.Exists(candidate))
          return candidate;
      } catch (ArgumentException) {
        // malformed PATH entry
      }
    }

    return null;
  }

  public SmartStatus Query(string physicalPathOrDevice) {
    if (this._smartctl == null)
      return SmartStatus.Unavailable(physicalPathOrDevice, "smartctl is not installed");

    var device = _DeviceFor(physicalPathOrDevice);
    System.Diagnostics.Process? process = null;
    try {
      // pass the device via ArgumentList (no manual quoting) and read BOTH streams asynchronously:
      // reading only stdout deadlocks if smartctl fills the stderr pipe, and the process was never
      // disposed or killed on timeout — leaking a handle (and an orphan) per query
      var psi = new System.Diagnostics.ProcessStartInfo(this._smartctl) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      psi.ArgumentList.Add("-j");
      // -i is what carries model_name. Without it the health report and the dashboard tooltip name
      // no drive at all, which on a pool of several identical-looking members is most of the value
      // of showing health in the first place. It costs nothing — the same single invocation — and
      // although it also returns a serial number, nothing here reads one: the snapshot is written to
      // the config directory and a serial is identifying information the dashboard has no use for.
      psi.ArgumentList.Add("-i");
      psi.ArgumentList.Add("-H");
      psi.ArgumentList.Add("-A");
      psi.ArgumentList.Add(device);

      process = System.Diagnostics.Process.Start(psi)!;
      var stdout = process.StandardOutput.ReadToEndAsync();
      var stderr = process.StandardError.ReadToEndAsync(); // drained so it never blocks the child
      if (!process.WaitForExit(10000)) {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        DriveBender.Logger($"[Warning]smartctl query for '{device}' timed out");
        return SmartStatus.Unavailable(device, "smartctl did not answer within 10s");
      }

      System.Threading.Tasks.Task.WaitAll(stdout, stderr);
      return SmartParsing.Parse(device, stdout.Result);
    } catch (Exception e) {
      // the REASON travels with the answer: "smartctl is missing", "it was not allowed to open the
      // device" and "it timed out" call for three different actions from whoever reads the
      // dashboard, and all three used to arrive as the same bare "SMART not available"
      DriveBender.Logger($"[Warning]smartctl query for '{device}' failed: {e.Message}");
      return SmartStatus.Unavailable(device, e.Message);
    } finally {
      process?.Dispose();
    }
  }

  private static string _DeviceFor(string physicalPathOrDevice) {
    if (physicalPathOrDevice.StartsWith("/dev/", StringComparison.Ordinal) || physicalPathOrDevice.StartsWith(@"\\.\", StringComparison.Ordinal))
      return physicalPathOrDevice;

    // best effort: map a mount path to its device (Linux) or drive root (Windows)
    if (OperatingSystem.IsWindows())
      return physicalPathOrDevice.Length >= 2 && physicalPathOrDevice[1] == ':' ? physicalPathOrDevice[..2] : physicalPathOrDevice;

    return physicalPathOrDevice;
  }

}

/// <summary>Pure parser for smartctl JSON — extracted so it can be unit-tested without the binary.</summary>
public static class SmartParsing {

  public static SmartStatus Parse(string device, string smartctlJson) {
    if (string.IsNullOrWhiteSpace(smartctlJson))
      return SmartStatus.Unavailable(device);

    try {
      using var document = System.Text.Json.JsonDocument.Parse(smartctlJson);
      var root = document.RootElement;

      // A MISSING smart_status is not a failed one. smartctl answers with well-formed JSON when it
      // could not open the device at all — no permission (the ordinary case for a daemon a user
      // starts), a USB bridge that passes no SMART through, a device that does not implement it —
      // and reading the absent field as "passed: false" reported every such drive as DYING. That is
      // the worst direction for a health signal to be wrong in: indistinguishable from the real
      // thing, fired on hardware that is fine, and once it has cried wolf nobody believes the one
      // that matters.
      bool? passed = root.TryGetProperty("smart_status", out var status) && status.TryGetProperty("passed", out var passedElement)
        ? passedElement.GetBoolean()
        : null;

      if (passed == null && _FirstError(root) is { } reason)
        return SmartStatus.Unavailable(device, reason);

      int? temperature = root.TryGetProperty("temperature", out var temp) && temp.TryGetProperty("current", out var current) ? current.GetInt32() : null;
      var model = root.TryGetProperty("model_name", out var modelElement) ? modelElement.GetString() : null;
      int? powerOnHours = root.TryGetProperty("power_on_time", out var pot) && pot.TryGetProperty("hours", out var hours) ? hours.GetInt32() : null;

      long? reallocated = null, pending = null;
      if (root.TryGetProperty("ata_smart_attributes", out var attrs) && attrs.TryGetProperty("table", out var table))
        foreach (var attr in table.EnumerateArray()) {
          var id = attr.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
          var raw = attr.TryGetProperty("raw", out var rawElement) && rawElement.TryGetProperty("value", out var rawValue) ? rawValue.GetInt64() : 0;
          if (id == 5) reallocated = raw;
          else if (id == 197) pending = raw;
        }

      // NVMe keeps none of that in the ATA table — it has its own log, and without reading it a
      // solid-state drive could only ever be judged on "passed" and its temperature. A drive with
      // its endurance spent, its spare blocks exhausted or media errors logged reported as HEALTHY,
      // which on modern hardware is most drives.
      int? percentageUsed = null, availableSpare = null, spareThreshold = null;
      long? mediaErrors = null;
      var criticalWarning = 0L;
      if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme)) {
        percentageUsed = _Int(nvme, "percentage_used");
        availableSpare = _Int(nvme, "available_spare");
        spareThreshold = _Int(nvme, "available_spare_threshold");
        mediaErrors = _Long(nvme, "media_errors");
        criticalWarning = _Long(nvme, "critical_warning") ?? 0;
        temperature ??= _Int(nvme, "temperature");
        powerOnHours ??= _Int(nvme, "power_on_hours");
      }

      var health = _Classify(passed, temperature, reallocated, pending, criticalWarning, percentageUsed, availableSpare, spareThreshold, mediaErrors);
      var detail = passed switch {
        false => "SMART self-assessment FAILED",
        true => _Describe(health, temperature, reallocated, pending, percentageUsed, availableSpare, mediaErrors),
        _ => "SMART status not reported",
      };

      return new(device, health, temperature, reallocated, pending, powerOnHours, model, detail,
        percentageUsed, availableSpare, mediaErrors);
    } catch (System.Text.Json.JsonException) {
      return SmartStatus.Unavailable(device);
    }
  }

  private static int? _Int(System.Text.Json.JsonElement parent, string name)
    => parent.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetInt32() : null;

  private static long? _Long(System.Text.Json.JsonElement parent, string name)
    => parent.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number ? value.GetInt64() : null;

  /// <summary>smartctl's own complaint, so "no smartctl" can be told apart from "not allowed".</summary>
  private static string? _FirstError(System.Text.Json.JsonElement root) {
    if (!root.TryGetProperty("smartctl", out var smartctl) || !smartctl.TryGetProperty("messages", out var messages)
        || messages.ValueKind != System.Text.Json.JsonValueKind.Array)
      return null;

    foreach (var message in messages.EnumerateArray())
      if (message.TryGetProperty("string", out var text) && text.GetString() is { Length: > 0 } line)
        return line;

    return null;
  }

  /// <summary>
  /// Severity from every signal the device offered, worst wins.
  ///
  /// The thresholds are the drive vendors' own where one exists — a drive states the spare level it
  /// considers critical, and 100% used means the rated endurance is spent — and conservative where
  /// none does.
  /// </summary>
  private static DiskHealth _Classify(bool? passed, int? temperature, long? reallocated, long? pending,
    long criticalWarning, int? percentageUsed, int? availableSpare, int? spareThreshold, long? mediaErrors) {
    if (passed == null)
      return DiskHealth.Unknown; // it was never asked, or never answered

    // get the data off it
    if (passed == false || pending > 0 || criticalWarning != 0
        || (availableSpare is { } spare && spareThreshold is { } threshold && spare < threshold)
        || percentageUsed >= 100)
      return DiskHealth.Failing;

    // degradation that has already happened
    if (reallocated > 0 || mediaErrors > 0 || percentageUsed >= 90 || temperature >= 55)
      return DiskHealth.Warning;

    // wear that is normal but worth watching
    if (percentageUsed >= 75 || temperature >= 50)
      return DiskHealth.Aging;

    return DiskHealth.Healthy;
  }

  private static string _Describe(DiskHealth health, int? temperature, long? reallocated, long? pending,
    int? percentageUsed, int? availableSpare, long? mediaErrors) {
    if (health == DiskHealth.Healthy)
      return "SMART passed";

    var reasons = new List<string>();
    if (pending > 0) reasons.Add($"{pending} sector(s) pending reallocation");
    if (reallocated > 0) reasons.Add($"{reallocated} sector(s) reallocated");
    if (mediaErrors > 0) reasons.Add($"{mediaErrors} media error(s)");
    if (percentageUsed >= 75) reasons.Add($"{percentageUsed}% of rated endurance used");
    if (availableSpare is { } spare and < 20) reasons.Add($"{spare}% spare blocks left");
    if (temperature >= 50) reasons.Add($"running at {temperature} °C");

    return reasons.Count == 0 ? "SMART passed" : string.Join("; ", reasons);
  }

}
