using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Copying a whole folder tree into the pool — the first thing anybody does with a new drive.
///
/// Reported from real use: copying lots of files into the pool with Explorer failed repeatedly with
/// write errors and "directory does not exist"; retrying got a few seconds further and failed again.
/// Nothing else in the suite copies a TREE: the scenarios write single files or flat batches, so the
/// shape that interleaves creating folders with filling them, thousands of times, at the speed a
/// copy engine does it, was never exercised.
///
/// The pool settings are the reporter's own, taken from their manifest: two members on one disk,
/// duplication 2, the "performance" write policy, and the balancer and duplicator both running.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class BulkCopyEndToEndTests {

  private const string _REPORTED_SETTINGS = """
    {
      "duplication": 2,
      "placement": { "shadowNeverSamePhysical": false, "strategy": "most-free-space" },
      "resilience": { "onMemberLoss": "discard-inaccessible" },
      "integrity": { "checksumDb": true },
      "background": { "balancerEnabled": true, "duplicatorEnabled": true },
      "safety": { "journalEnabled": true },
      "write": { "policy": "performance" },
      "readAhead": { "enabled": true },
      "cache": { "size": "2GiB" }
    }
    """;

  private string _source = "";

  [SetUp]
  public void BuildASourceTree() {
    // A tree shaped like a real one: nested folders, many small files, a few larger ones, and
    // folders created and filled in the same pass the way a copy engine walks them.
    this._source = Path.Combine(Path.GetTempPath(), "dbe2e-src-" + Guid.NewGuid().ToString("N")[..8]);
    var random = new Random(7);
    for (var top = 0; top < 12; ++top)
    for (var mid = 0; mid < 6; ++mid) {
      var folder = Path.Combine(this._source, $"project-{top:00}", $"module-{mid}", "src");
      Directory.CreateDirectory(folder);
      for (var file = 0; file < 25; ++file) {
        var size = file == 0 ? 256 * 1024 : random.Next(200, 12 * 1024);
        var payload = new byte[size];
        random.NextBytes(payload);
        File.WriteAllBytes(Path.Combine(folder, $"file-{file:00}.dat"), payload);
      }
    }
  }

  [TearDown]
  public void RemoveTheSourceTree() {
    try {
      Directory.Delete(this._source, true);
    } catch (Exception) {
      // best effort
    }
  }

  private static string[] _RelativeFiles(string root)
    => [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order()];

  private void _AssertIdentical(MountedPool pool, string target, string how) {
    var expected = _RelativeFiles(this._source);
    var missing = new List<string>();
    var different = new List<string>();
    foreach (var relative in expected) {
      var copied = Path.Combine(target, relative);
      if (!File.Exists(copied)) {
        missing.Add(relative);
        continue;
      }

      if (!File.ReadAllBytes(copied).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(this._source, relative))))
        different.Add(relative);
    }

    missing.Should().BeEmpty($"every file {how} must arrive ({missing.Count} of {expected.Length} missing)."
                             + $"{Environment.NewLine}{pool.MountLog}");
    different.Should().BeEmpty($"and arrive intact ({different.Count} differ).{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("HappyPath")]
  [Description("A folder tree is copied in file by file, folders created as they are reached: every file arrives, nothing is refused.")]
  public void Copy_GivenATreeCopiedFolderByFolder_ThenEveryFileArrivesWithoutAnError() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _REPORTED_SETTINGS);
    var target = pool.PathTo("copied");

    var errors = new List<string>();
    var stopwatch = Stopwatch.StartNew();
    foreach (var file in _RelativeFiles(this._source)) {
      var destination = Path.Combine(target, file);
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Path.Combine(this._source, file), destination, overwrite: false);
      } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
        errors.Add($"{file}: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
      }
    }

    TestContext.Out.WriteLine($"copied {_RelativeFiles(this._source).Length} files in {stopwatch.Elapsed.TotalSeconds:F1}s, {errors.Count} errors");
    errors.Should().BeEmpty($"a plain tree copy must not be refused.{Environment.NewLine}"
                            + $"{string.Join(Environment.NewLine, errors.Take(15))}{Environment.NewLine}{pool.MountLog}");
    this._AssertIdentical(pool, target, "copied folder by folder");
  }

  [Test]
  [Category("HappyPath")]
  [Description("The same tree copied by robocopy with eight threads — the Windows copy engine, run concurrently: every file arrives.")]
  public void Copy_GivenATreeCopiedByTheWindowsCopyEngineConcurrently_ThenEveryFileArrives() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("robocopy is the Windows copy engine; the folder-by-folder scenario covers Linux");

    using var pool = MountedPool.Create(members: 2, poolDefaults: _REPORTED_SETTINGS);
    var target = pool.PathTo("robocopied");

    // robocopy calls CopyFileEx like Explorer does; /MT overlaps folder creation with filling. /R:0
    // so a refusal is reported rather than silently retried away — a retry that eventually wins is
    // precisely the "gets a few seconds further, then fails again" that was reported.
    var process = Process.Start(new ProcessStartInfo {
      FileName = "robocopy.exe",
      ArgumentList = { this._source, target, "/E", "/MT:8", "/R:0", "/W:0", "/NP", "/NFL", "/NDL" },
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    })!;

    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit(TimeSpan.FromMinutes(5));

    // robocopy exit codes: 0-7 are success variants, 8 and above mean at least one copy failed
    TestContext.Out.WriteLine(output.Split('\n').TakeLast(14).Aggregate("", (a, l) => a + l + "\n"));
    process.ExitCode.Should().BeLessThan(8,
      $"robocopy reported failed copies.{Environment.NewLine}{output}{Environment.NewLine}{pool.MountLog}");
    this._AssertIdentical(pool, target, "copied by robocopy");
  }

}
