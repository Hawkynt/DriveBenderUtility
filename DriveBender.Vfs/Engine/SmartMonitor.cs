namespace DivisonM.Vfs.Engine;

public enum DiskHealth {
  Unknown,
  Healthy,
  Warning,
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
  string? Detail
) {
  public static SmartStatus Unavailable(string device) => new(device, DiskHealth.Unknown, null, null, null, null, null, "SMART not available");
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
      return SmartStatus.Unavailable(physicalPathOrDevice);

    var device = _DeviceFor(physicalPathOrDevice);
    try {
      var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(this._smartctl, $"-j -H -A \"{device}\"") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
      })!;
      var json = process.StandardOutput.ReadToEnd();
      process.WaitForExit(10000);
      return SmartParsing.Parse(device, json);
    } catch (Exception e) {
      DriveBender.Logger($"[Warning]smartctl query for '{device}' failed: {e.Message}");
      return SmartStatus.Unavailable(device);
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

      var passed = root.TryGetProperty("smart_status", out var status) && status.TryGetProperty("passed", out var passedElement) && passedElement.GetBoolean();
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

      var health = _Classify(passed, temperature, reallocated, pending);
      return new(device, health, temperature, reallocated, pending, powerOnHours, model, passed ? "SMART passed" : "SMART reported a problem");
    } catch (System.Text.Json.JsonException) {
      return SmartStatus.Unavailable(device);
    }
  }

  private static DiskHealth _Classify(bool passed, int? temperature, long? reallocated, long? pending) {
    if (!passed || pending > 0)
      return DiskHealth.Failing;
    if (reallocated > 0 || temperature >= 55)
      return DiskHealth.Warning;
    if (temperature >= 50)
      return DiskHealth.Warning;

    return DiskHealth.Healthy;
  }

}
