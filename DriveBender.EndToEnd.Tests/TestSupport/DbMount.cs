using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DivisonM.EndToEnd.Tests.TestSupport;

/// <summary>The result of one CLI invocation — everything needed to explain a failure in CI output.</summary>
public sealed record CliResult(int ExitCode, string StandardOutput, string StandardError) {
  public bool Succeeded => this.ExitCode == 0;

  /// <summary>stdout and stderr together, for assertion messages.</summary>
  public string Output => (this.StandardOutput + Environment.NewLine + this.StandardError).Trim();
}

/// <summary>
/// Runs the SHIPPED <c>dbmount</c> binary the way a user does.
///
/// Everything in this suite goes through this: no project reference to the engine, no in-process
/// shortcuts. A test that links the engine directly can pass while the thing the user installs is
/// broken — which is precisely what happened with the member online-probe, green across the whole
/// unit suite because nothing in it ever built the real local backend.
/// </summary>
public static class DbMount {

  private static readonly Lazy<string> _executable = new(_Resolve);

  public static string Executable => _executable.Value;

  /// <summary>
  /// The Windows build (<c>net10.0-windows</c>) is the only one that can mount on Windows; the
  /// portable build is the one that can mount on Linux. CI sets DBMOUNT_EXE explicitly; locally we
  /// discover the right flavour under the mount project's output.
  /// </summary>
  private static string _Resolve() {
    var name = OperatingSystem.IsWindows() ? "dbmount.exe" : "dbmount";
    var flavour = OperatingSystem.IsWindows() ? "net10.0-windows" : "net10.0";
    var tried = new List<string>();

    // A configured path may be RELATIVE — CI naturally writes it relative to the repository root,
    // while `dotnet test` runs with the test assembly's output directory as the working directory,
    // so resolving it against the CWD silently fails. Try the obvious bases rather than making the
    // caller know which one applies.
    if (Environment.GetEnvironmentVariable("DBMOUNT_EXE") is { Length: > 0 } configured) {
      foreach (var candidate in _Bases().Select(b => Path.GetFullPath(Path.Combine(b, configured))).Prepend(configured)) {
        tried.Add(candidate);
        if (File.Exists(candidate))
          return candidate;
      }

      throw new FileNotFoundException(
        $"DBMOUNT_EXE is '{configured}', which resolved to nothing that exists. Tried:{Environment.NewLine}"
        + string.Join(Environment.NewLine, tried.Select(t => "  " + t)));
    }

    // walk up from the test assembly to the repository root, then into the mount project's output
    foreach (var root in _Bases()) {
      var candidate = Path.Combine(root, "DriveBender.Mount", "bin", "Release", flavour, name);
      tried.Add(candidate);
      if (File.Exists(candidate))
        return candidate;
    }

    throw new FileNotFoundException(
      $"Could not find '{name}' for '{flavour}' on {Platform}. Build the solution in Release, or set "
      + $"DBMOUNT_EXE to the binary under test. Tried:{Environment.NewLine}"
      + string.Join(Environment.NewLine, tried.Select(t => "  " + t)));
  }

  /// <summary>Every plausible base a relative path could be meant against, nearest first.</summary>
  private static IEnumerable<string> _Bases() {
    yield return Directory.GetCurrentDirectory();

    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
      yield return directory.FullName;
  }

  /// <summary>Runs a verb to completion and captures both pipes concurrently (reading one to the end first deadlocks).</summary>
  public static CliResult Run(TimeSpan timeout, params string[] arguments) {
    using var process = Start(arguments, out var stdout, out var stderr);
    if (!process.WaitForExit((int)timeout.TotalMilliseconds)) {
      _KillTree(process);
      process.WaitForExit(5000);
      throw new TimeoutException(
        $"`dbmount {string.Join(' ', arguments)}` did not finish within {timeout.TotalSeconds:F0}s.{Environment.NewLine}"
        + $"stdout so far:{Environment.NewLine}{stdout}{Environment.NewLine}stderr so far:{Environment.NewLine}{stderr}");
    }

    process.WaitForExit(); // the pipes are drained; this only reaps the exit code
    return new(process.ExitCode, stdout.ToString(), stderr.ToString());
  }

  /// <summary>Runs a verb and fails loudly, with the process output, when it does not succeed.</summary>
  public static CliResult RunExpectingSuccess(TimeSpan timeout, params string[] arguments) {
    var result = Run(timeout, arguments);
    if (!result.Succeeded)
      throw new InvalidOperationException(
        $"`dbmount {string.Join(' ', arguments)}` exited {result.ExitCode}:{Environment.NewLine}{result.Output}");

    return result;
  }

  /// <summary>
  /// Starts a long-lived invocation (a foreground mount, the management daemon) and streams both
  /// pipes into the supplied builders so a hang can be diagnosed from what it printed first.
  /// </summary>
  public static Process Start(string[] arguments, out StringBuilder stdout, out StringBuilder stderr) {
    var outBuilder = new StringBuilder();
    var errBuilder = new StringBuilder();
    stdout = outBuilder;
    stderr = errBuilder;

    var start = new ProcessStartInfo {
      FileName = Executable,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    var process = Process.Start(start)
                  ?? throw new InvalidOperationException($"Could not start '{Executable}'");

    process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outBuilder) outBuilder.AppendLine(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errBuilder) errBuilder.AppendLine(e.Data); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
  }

  public static void KillTree(Process process) => _KillTree(process);

  private static void _KillTree(Process process) {
    try {
      if (!process.HasExited)
        process.Kill(entireProcessTree: true);
    } catch (Exception) {
      // already gone, or not ours to signal
    }
  }

  /// <summary>Snapshot of a builder that background readers are still appending to.</summary>
  public static string Snapshot(StringBuilder builder) {
    lock (builder)
      return builder.ToString();
  }

  /// <summary>A short description of the platform, for skip and failure messages.</summary>
  public static string Platform => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

  /// <summary>
  /// Where <c>pool-create</c> registers a pool. There is no CLI verb to deregister one — the UI
  /// does it over the API — so a test that creates a pool removes its registry entry itself,
  /// otherwise every run leaves a phantom behind in the user's dashboard.
  /// </summary>
  public static string PoolRegistryDirectory {
    get {
      if (OperatingSystem.IsWindows())
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DriveBenderUtility", "pools");

      const string machineRoot = "/etc/drivebenderutility";
      if (Directory.Exists(machineRoot))
        return Path.Combine(machineRoot, "pools");

      var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
      var baseDir = string.IsNullOrEmpty(xdg)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
        : xdg;

      return Path.Combine(baseDir, "drivebenderutility", "pools");
    }
  }

  /// <summary>
  /// Sets the manifest's <c>defaults</c> block for a pool the CLI just created.
  ///
  /// There is no CLI verb for this — the UI does it over the API — and standing up the daemon
  /// just to configure a fixture would drag the whole management stack into a driver test. The
  /// registry manifest is a documented JSON file, so it is edited directly, BEFORE the pool is
  /// mounted and therefore before anything can be reading it.
  /// </summary>
  public static void SetPoolDefaults(string poolName, string defaultsJson) {
    var directory = PoolRegistryDirectory;
    foreach (var path in Directory.EnumerateFiles(directory, "*.json")) {
      var text = File.ReadAllText(path);
      if (!text.Contains($"\"{poolName}\"", StringComparison.Ordinal))
        continue;

      var manifest = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
      manifest["defaults"] = System.Text.Json.Nodes.JsonNode.Parse(defaultsJson);
      manifest["version"] = (manifest["version"]?.GetValue<int>() ?? 1) + 1; // highest version wins on mount
      File.WriteAllText(path, manifest.ToJsonString(new() { WriteIndented = true }));
      return;
    }

    throw new FileNotFoundException($"No registry manifest for pool '{poolName}' under '{directory}'");
  }

  /// <summary>
  /// Sets a member's rate limits (<c>maxIops</c>, <c>maxThroughput</c>) in the pool's manifest.
  ///
  /// These are the product's own way of saying "this storage is only this fast" — a mechanical drive
  /// shared with something else, a cloud endpoint with a rate limit — and they are the only way to
  /// put a pool on storage of KNOWN, REPEATABLE speed. Real devices are better evidence and worse
  /// experiments: a machine has whatever disks it has, they differ between hosts, and their measured
  /// rate wanders with whatever else is running. A limit is the same on every machine and every run.
  ///
  /// There is no CLI verb for them, so the registry manifest is edited directly and BEFORE the pool
  /// is mounted, exactly as <see cref="SetPoolDefaults"/> does.
  /// </summary>
  public static void SetMemberLimits(string poolName, string memberPath, int maxIops, long maxThroughput)
    => _EditMember(poolName, memberPath, entry => {
      entry["maxIops"] = maxIops;
      entry["maxThroughput"] = maxThroughput;
    });

  /// <summary>
  /// The FINE-GRAINED shape (§6.4): a separate byte rate per kind of operation, written into the
  /// member's <c>limits</c> block.
  ///
  /// What the simple pair cannot express, and what a tiering scenario needs: hold the pool's own
  /// background copying to a crawl while leaving the application's reads and writes alone. Limiting
  /// everything instead would make the setup write take as long as the drain it is trying to catch.
  /// </summary>
  public static void SetMemberThroughput(string poolName, string memberPath, long read = 0, long write = 0, long background = 0)
    => _EditMember(poolName, memberPath, entry => entry["limits"] = new System.Text.Json.Nodes.JsonObject {
      ["readThroughput"] = read,
      ["writeThroughput"] = write,
      ["backgroundThroughput"] = background,
    });

  private static void _EditMember(string poolName, string memberPath, Action<System.Text.Json.Nodes.JsonObject> edit) {
    var directory = PoolRegistryDirectory;
    foreach (var path in Directory.EnumerateFiles(directory, "*.json")) {
      var text = File.ReadAllText(path);
      if (!text.Contains($"\"{poolName}\"", StringComparison.Ordinal))
        continue;

      var manifest = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
      var members = manifest["members"]?.AsArray()
                    ?? throw new InvalidOperationException($"Manifest for '{poolName}' has no members array");

      var matched = false;
      foreach (var member in members) {
        if (member is not System.Text.Json.Nodes.JsonObject entry)
          continue;

        // the manifest records the path the pool was created with, which is the member LINK
        if (!string.Equals(entry["path"]?.GetValue<string>(), memberPath, StringComparison.Ordinal))
          continue;

        edit(entry);
        matched = true;
      }

      if (!matched)
        throw new InvalidOperationException(
          $"No member '{memberPath}' in the manifest for '{poolName}'; it holds "
          + string.Join(", ", members.Select(m => m?["path"]?.GetValue<string>() ?? "?")));

      manifest["version"] = (manifest["version"]?.GetValue<int>() ?? 1) + 1; // highest version wins on mount
      File.WriteAllText(path, manifest.ToJsonString(new() { WriteIndented = true }));
      return;
    }

    throw new FileNotFoundException($"No registry manifest for pool '{poolName}' under '{directory}'");
  }

  /// <summary>The pool id recorded in a pool's registry manifest.</summary>
  public static Guid PoolIdOf(string poolName) {
    foreach (var path in Directory.EnumerateFiles(PoolRegistryDirectory, "*.json")) {
      var text = File.ReadAllText(path);
      if (!text.Contains($"\"{poolName}\"", StringComparison.Ordinal))
        continue;

      var manifest = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
      if (manifest["poolId"]?.GetValue<Guid>() is { } id)
        return id;
    }

    throw new FileNotFoundException($"No registry manifest for pool '{poolName}' under '{PoolRegistryDirectory}'");
  }

  /// <summary>
  /// Asks a MOUNTED pool to re-read its manifest, the way the management UI does when a setting is
  /// changed under a running pool.
  ///
  /// There is no CLI verb for it — only the daemon files this request — so the marker is written
  /// straight into the cross-process channel directory, in the same documented shape and for the
  /// same reason the manifest itself is edited directly here: standing up the whole management
  /// stack to flip one setting would drag it into a driver test.
  /// </summary>
  public static void RequestLiveReload(string poolName) {
    var mounts = Path.Combine(Path.GetDirectoryName(PoolRegistryDirectory)!, "mounts");
    Directory.CreateDirectory(mounts);
    File.WriteAllText(Path.Combine(mounts, $"{PoolIdOf(poolName):D}.reload"), DateTime.UtcNow.ToString("O"));
  }

  /// <summary>
  /// The live metrics snapshot the MOUNT process publishes for a pool, or null before the first one.
  ///
  /// This is the mount's own view — free space, latency, member health — written to the channel
  /// directory once a second for the management daemon to read. Going to it directly lets a scenario
  /// assert what the ENGINE observed without standing up the daemon in between.
  /// </summary>
  public static System.Text.Json.JsonElement? TryReadMetrics(string poolName) {
    try {
      var mounts = Path.Combine(Path.GetDirectoryName(PoolRegistryDirectory)!, "mounts");
      var path = Path.Combine(mounts, $"{PoolIdOf(poolName):D}.metrics.json");
      if (!File.Exists(path))
        return null;

      return System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    } catch (Exception) {
      return null; // a half-written snapshot is read again on the next poll
    }
  }

  /// <summary>Removes a test pool's registry entry by name; never throws, cleanup must not fail a run.</summary>
  public static void ForgetPool(string poolName) {
    try {
      var directory = PoolRegistryDirectory;
      if (!Directory.Exists(directory))
        return;

      foreach (var manifest in Directory.EnumerateFiles(directory, "*.json"))
        try {
          if (File.ReadAllText(manifest).Contains($"\"{poolName}\"", StringComparison.Ordinal))
            File.Delete(manifest);
        } catch (Exception) {
          // a manifest we cannot read is not ours to delete
        }
    } catch (Exception) {
      // cleanup is best effort
    }
  }

}
