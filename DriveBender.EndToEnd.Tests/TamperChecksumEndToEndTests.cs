using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The checksum sidecar, edited by somebody who should not have (SAFE-OOB, FR-CHECKSUM).
///
/// `TamperEndToEndTests` covers the journal, the tombstone log, the recycle bin and the snapshot
/// store. It does not cover `checksums.json`, and that file is the most dangerous of the set to get
/// wrong — because it is the one the pool acts on DESTRUCTIVELY. The others make the pool forget
/// something; this one can make it "repair" a perfectly good file, overwriting content that was
/// never damaged with content from somewhere else, or quarantine it away from the user entirely.
///
/// So the rule these hold the pool to is sharper than "do not crash": <b>a sidecar is evidence, not
/// authority</b>. When one file on one disk disagrees with the actual bytes sitting on two members,
/// the bytes win. A checksum database that has been edited, truncated, emptied or filled with
/// nonsense may cost the pool its ability to DETECT rot; it must never gain it the ability to
/// CAUSE any.
///
/// The second half is about time rather than bytes. A sidecar is an ordinary file that anybody can
/// make enormous, and a pool that reads one at mount must not become unusable because somebody
/// pasted a hundred megabytes into it.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("EdgeCase")]
[NonParallelizable]
public class TamperChecksumEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);
  private const string _UTILITY = ".drivebenderutility";
  private const string _CHECKSUMS = _UTILITY + "/checksums.json";

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>Every member's copy of the checksum sidecar that actually exists.</summary>
  private static IReadOnlyList<string> _Sidecars(MountedPool pool)
    => [.. pool.MemberPaths.Select(m => Path.Combine(m, _CHECKSUMS.Replace('/', Path.DirectorySeparatorChar)))
        .Where(File.Exists)];

  /// <summary>A pool with a file on it and a checksum baseline recorded, ready to be vandalised.</summary>
  private static MountedPool _PoolWithABaseline(string name, byte[] content) {
    var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    try {
      File.WriteAllBytes(pool.PathTo(name), content);
      pool.WaitForPhysicalCopies(name, atLeast: 2, TimeSpan.FromMinutes(2));

      // the baseline is taken unmounted: a scrub in one process and a mount in another both own
      // this sidecar, and the merge that makes that safe is not what is under test here
      pool.WhileUnmounted(() => DbMount.Run(_CLI, "pool-health", pool.PoolName, "--deep"));
      return pool;
    } catch {
      pool.Dispose();
      throw;
    }
  }

  private static void _AssertIntact(MountedPool pool, string name, byte[] expected, string what) {
    File.Exists(pool.PathTo(name)).Should().BeTrue(
      $"{what} must not cost the pool a file.{Environment.NewLine}{pool.DescribeMembers()}");
    File.ReadAllBytes(pool.PathTo(name)).Should().Equal(expected,
      $"{what} must not change a single byte of user data — a sidecar is evidence, not authority."
      + $"{Environment.NewLine}{pool.DescribeMembers()}");
  }

  [Test]
  [Description("The checksum sidecar is deleted from every member: the pool mounts, keeps its files, and rebuilds a baseline.")]
  public void Checksums_GivenTheSidecarIsDeletedEverywhere_ThenThePoolMountsAndKeepsItsFiles() {
    var content = _Payload(64 * 1024, 11);
    using var pool = _PoolWithABaseline("kept.bin", content);

    pool.WhileUnmounted(() => {
      foreach (var sidecar in _Sidecars(pool))
        File.Delete(sidecar);
    });

    _AssertIntact(pool, "kept.bin", content, "deleting the checksum database");

    // and the pool can earn a baseline again rather than being permanently blind
    var rebuilt = DbMount.Run(_CLI, "pool-health", pool.PoolName, "--deep");
    rebuilt.Output.Should().NotBeNullOrWhiteSpace("a health check must still report on a pool whose sidecar was deleted");
  }

  [Test]
  [Description("The checksum sidecar is replaced with text that is not JSON: the pool mounts and keeps its files.")]
  public void Checksums_GivenTheSidecarIsGarbage_ThenThePoolMountsAndKeepsItsFiles() {
    var content = _Payload(64 * 1024, 12);
    using var pool = _PoolWithABaseline("garbage.bin", content);

    pool.WhileUnmounted(() => {
      foreach (var sidecar in _Sidecars(pool))
        File.WriteAllText(sidecar, "this is not json, it is a note somebody left in the folder\n");
    });

    _AssertIntact(pool, "garbage.bin", content, "a checksum database full of prose");
  }

  [Test]
  [Description("The checksum sidecar is truncated mid-object: the pool mounts and keeps its files.")]
  public void Checksums_GivenTheSidecarIsTruncatedMidObject_ThenThePoolMountsAndKeepsItsFiles() {
    var content = _Payload(64 * 1024, 13);
    using var pool = _PoolWithABaseline("truncated.bin", content);

    pool.WhileUnmounted(() => {
      foreach (var sidecar in _Sidecars(pool)) {
        var text = File.ReadAllText(sidecar);
        File.WriteAllText(sidecar, text[..(text.Length / 2)]); // stops mid-record, like a full disk would
      }
    });

    _AssertIntact(pool, "truncated.bin", content, "a half-written checksum database");
  }

  [Test]
  [Description("The sidecar claims a WRONG hash for a healthy file: --fix must not overwrite content that two members agree on.")]
  public void Checksums_GivenTheSidecarLiesAboutAHealthyFile_ThenAFixDoesNotDestroyIt() {
    // The scenario that makes this file different from the other sidecars. A tampered journal makes
    // the pool forget; a tampered checksum database can make it ACT — "repairing" a file nobody
    // damaged, or quarantining it away from its owner. Both members hold identical, correct bytes
    // here, so the only thing claiming corruption is the edited sidecar, and two disks agreeing is
    // stronger evidence than one file somebody opened in an editor.
    var content = _Payload(64 * 1024, 14);
    using var pool = _PoolWithABaseline("slandered.bin", content);

    var rewritten = 0;
    pool.WhileUnmounted(() => {
      foreach (var sidecar in _Sidecars(pool)) {
        var root = JsonNode.Parse(File.ReadAllText(sidecar))?.AsObject();
        if (root == null)
          continue;

        foreach (var (key, entry) in root.ToArray()) {
          if (entry is not JsonObject record || !record.ContainsKey("hash"))
            continue;

          record["hash"] = "DEADBEEFDEADBEEF"; // a plausible-looking hash for content that never had it
          ++rewritten;
          _ = key;
        }

        File.WriteAllText(sidecar, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
      }
    });

    rewritten.Should().BeGreaterThan(0, "the baseline must have recorded something for the tampering to be about");

    var fix = DbMount.Run(_CLI, "pool-health", pool.PoolName, "--deep", "--fix");
    TestContext.Out.WriteLine($"--fix said (exit {fix.ExitCode}):{Environment.NewLine}{fix.Output}");

    // NON-VACUITY: the lie has to have reached the classifier. If the health check calls this pool
    // healthy then the tampered sidecar was never consulted and the assertions below prove nothing
    // about how the pool treats a sidecar it disagrees with. The correct answer is to notice that
    // EVERY copy fails its recorded checksum, conclude that the record is the odd one out, and
    // leave the data exactly where it is.
    fix.ExitCode.Should().NotBe(0,
      $"a pool whose every copy contradicts its recorded checksum is not 'healthy', and a check that "
      + $"says so never looked at the tampering this test performed.{Environment.NewLine}{fix.Output}");

    pool.Remount();

    _AssertIntact(pool, "slandered.bin", content,
      $"a checksum database that lies about a healthy file{Environment.NewLine}stdout:{fix.StandardOutput}");

    // every physical copy too: a "repair" that rewrote one member would leave the mount looking
    // fine while the redundancy quietly became two different versions
    foreach (var (where, bytes) in pool.PhysicalCopies("slandered.bin"))
      bytes.Should().Equal(content, $"the copy at '{where}' was rewritten on the word of an edited sidecar");
  }

  [Test]
  [Description("The sidecar names files that do not exist: the pool mounts, works, and a health check does not choke.")]
  public void Checksums_GivenTheSidecarNamesFilesThatAreNotThere_ThenNothingChokes() {
    var content = _Payload(32 * 1024, 15);
    using var pool = _PoolWithABaseline("real.bin", content);

    pool.WhileUnmounted(() => {
      foreach (var sidecar in _Sidecars(pool)) {
        var root = JsonNode.Parse(File.ReadAllText(sidecar))?.AsObject() ?? [];
        for (var i = 0; i < 500; ++i)
          root[$"ghosts/never-existed-{i}.bin"] = new JsonObject {
            ["size"] = 1234,
            ["mtime"] = 638000000000000000,
            ["hash"] = "0123456789ABCDEF",
          };

        File.WriteAllText(sidecar, root.ToJsonString());
      }
    });

    _AssertIntact(pool, "real.bin", content, "a sidecar full of records for files that were never there");

    var health = DbMount.Run(_CLI, "pool-health", pool.PoolName, "--deep");
    health.Output.Should().NotContain("Unhandled",
      "records for absent files are somebody else's mess, not a reason to fall over");
  }

  [Test]
  [Description("The sidecar is replaced by a DIRECTORY of that name: the pool still mounts and keeps its files.")]
  public void Checksums_GivenTheSidecarIsReplacedByADirectory_ThenThePoolStillMountsAndKeepsItsFiles() {
    // the shape a naive sync tool or an interrupted archive extraction leaves behind, and one that
    // File.ReadAllText reports quite differently from a missing file
    var content = _Payload(32 * 1024, 16);
    using var pool = _PoolWithABaseline("dirswap.bin", content);

    pool.WhileUnmounted(() => {
      foreach (var sidecar in _Sidecars(pool)) {
        File.Delete(sidecar);
        Directory.CreateDirectory(sidecar);
      }
    });

    _AssertIntact(pool, "dirswap.bin", content, "a directory standing where the checksum database belongs");
  }

  [Test]
  [Category("Performance")]
  [Description("Somebody inflates the checksum sidecar to a hundred thousand records: mounting and reading stay quick.")]
  public void Checksums_GivenAnEnormousSidecar_ThenMountingAndReadingStayQuick() {
    // A sidecar is an ordinary file in a folder anybody can write to. Being made huge must cost the
    // pool time proportional to reading it once, not per operation - a pool that re-parses a
    // hundred thousand records on every read has been turned off by whoever pasted them in.
    var content = _Payload(32 * 1024, 17);
    using var pool = _PoolWithABaseline("quick.bin", content);

    pool.WhileUnmounted(() => {
      foreach (var sidecar in _Sidecars(pool)) {
        var root = JsonNode.Parse(File.ReadAllText(sidecar))?.AsObject() ?? [];
        for (var i = 0; i < 100_000; ++i)
          root[$"bulk/pad-{i}.bin"] = new JsonObject {
            ["size"] = i,
            ["mtime"] = 638000000000000000,
            ["hash"] = "0123456789ABCDEF",
          };

        File.WriteAllText(sidecar, root.ToJsonString());
      }
    });

    var mounted = Stopwatch.StartNew();
    _AssertIntact(pool, "quick.bin", content, "an inflated checksum database");
    mounted.Stop();

    TestContext.Out.WriteLine($"first read through an inflated sidecar: {mounted.ElapsedMilliseconds} ms");

    // generous, because this measures a mount plus a read on whatever hardware is running it; it
    // catches the failure that matters, which is per-operation re-parsing rather than a slow start
    var repeated = Stopwatch.StartNew();
    for (var i = 0; i < 200; ++i)
      File.ReadAllBytes(pool.PathTo("quick.bin"));

    repeated.Stop();
    repeated.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
      $"200 reads took {repeated.Elapsed.TotalSeconds:F1}s with a 100,000-record sidecar in place — that is a "
      + "per-read cost scaling with a file a stranger controls, which is a denial of service rather than a slow mount");
  }

  [Test]
  [Description("The sidecar is tampered with AND a member is away: the surviving member still serves the file unchanged.")]
  public void Checksums_GivenTheSidecarLiesAndAMemberIsAway_ThenTheSurvivorStillServesTheFile() {
    // combinations are where this sort of thing actually bites: one disk gone is when the pool
    // leans hardest on what it believes about the copies it still has
    var content = _Payload(64 * 1024, 18);
    using var pool = _PoolWithABaseline("degraded.bin", content);

    pool.WhileUnmounted(() => {
      foreach (var sidecar in _Sidecars(pool)) {
        var root = JsonNode.Parse(File.ReadAllText(sidecar))?.AsObject();
        if (root == null)
          continue;

        foreach (var (_, entry) in root.ToArray())
          if (entry is JsonObject record && record.ContainsKey("hash"))
            record["hash"] = "BADBADBADBADBAD0";

        File.WriteAllText(sidecar, root.ToJsonString());
      }
    });

    pool.Eject(1);
    try {
      _AssertIntact(pool, "degraded.bin", content,
        "a lying sidecar while a member is away — the last copy is exactly what must not be 'repaired'");
    } finally {
      pool.Restore(1);
    }
  }

}
