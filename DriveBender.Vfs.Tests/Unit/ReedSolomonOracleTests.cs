using System.Diagnostics;
using DivisonM.Vfs.Integrity;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Byte-for-byte agreement with the original: the unmodified Backblaze JavaReedSolomon sources
/// (MIT, vendored under Oracle/JavaReedSolomon with their licence) are compiled with javac, a small
/// driver encodes LCG data both sides generate identically, and the parity must match exactly.
/// Skipped where no JDK is installed.
/// </summary>
[TestFixture]
[Category("Oracle")]
[NonParallelizable]
public class ReedSolomonOracleTests {

  private string? _classes;
  private string? _java;

  [OneTimeSetUp]
  public void CompileOracle() {
    var javac = FindOnPath("javac");
    this._java = FindOnPath("java");
    if (javac == null || this._java == null)
      return;

    var sources = Path.Combine(TestContext.CurrentContext.TestDirectory, "Oracle");
    var files = Directory.GetFiles(sources, "*.java", SearchOption.AllDirectories);
    this._classes = Path.Combine(Path.GetTempPath(), "rs-oracle-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._classes);

    var (exit, output) = Run(javac, ["-nowarn", "-d", this._classes, .. files]);
    if (exit != 0)
      Assert.Fail("javac failed on the oracle sources:\n" + output);
  }

  [OneTimeTearDown]
  public void Cleanup() {
    if (this._classes != null)
      try {
        Directory.Delete(this._classes, true);
      } catch (IOException) {
        // best effort
      }
  }

  [TestCase(1L, 1, 1, 1)]
  [TestCase(2L, 1, 16, 100)]
  [TestCase(3L, 7, 2, 1000)]
  [TestCase(4L, 5, 8, 4097)]
  [TestCase(5L, 10, 16, 333)]
  [TestCase(6L, 32, 1, 31)]
  [TestCase(7L, 32, 2, 65536)]
  [TestCase(8L, 32, 4, 65536 + 7)]
  [TestCase(9L, 32, 8, 20000)]
  [TestCase(10L, 32, 16, 65536)]
  [TestCase(11L, 17, 3, 777)]
  public void Parity_GivenTheSameData_ThenMatchesJavaReedSolomon(long seed, int k, int m, int size) {
    if (this._classes == null)
      Assert.Ignore("no JDK (javac/java) on PATH");

    var output = Path.Combine(this._classes, $"parity-{seed}.bin");
    var (exit, log) = Run(this._java!, ["-cp", this._classes, "OracleMain", seed.ToString(), k.ToString(), m.ToString(), size.ToString(), output]);
    exit.Should().Be(0, log);
    var expected = File.ReadAllBytes(output);
    expected.Length.Should().Be(m * size);

    var shards = ReedSolomonTests.NewShards(seed, k, m, size);
    new ReedSolomon(k, m).EncodeParity(shards, 0, size);

    for (var p = 0; p < m; ++p) {
      var mismatch = shards[k + p].AsSpan().CommonPrefixLength(expected.AsSpan(p * size, size));
      if (mismatch != size)
        Assert.Fail($"k={k} m={m} size={size}: parity shard {p} differs from Java at byte {mismatch}");
    }

    TestContext.Out.WriteLine($"k={k} m={m} size={size}: {m * size} parity bytes identical to JavaReedSolomon");
  }

  private static string? FindOnPath(string tool) {
    var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe" } : new[] { tool };
    var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
    var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
    if (!string.IsNullOrEmpty(javaHome))
      directories.Insert(0, Path.Combine(javaHome, "bin"));

    foreach (var directory in directories)
    foreach (var name in names) {
      var candidate = Path.Combine(directory, name);
      if (File.Exists(candidate))
        return candidate;
    }

    return null;
  }

  private static (int Exit, string Output) Run(string file, IEnumerable<string> arguments) {
    var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in arguments)
      info.ArgumentList.Add(argument);

    using var process = Process.Start(info)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout + stderr.Result);
  }
}
