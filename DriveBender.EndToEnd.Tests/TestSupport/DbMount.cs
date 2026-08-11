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
