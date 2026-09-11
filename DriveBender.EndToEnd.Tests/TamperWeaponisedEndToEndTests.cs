using System.Diagnostics;
using System.Text.Json.Nodes;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Sidecars turned into weapons, rather than merely broken (SAFE-OOB, SEC-PATH).
///
/// `TamperEndToEndTests` asks what happens when a sidecar is damaged — truncated, deleted, filled
/// with prose. These ask the harder question: what happens when one is edited to say something
/// deliberately false but perfectly well-formed. Damage makes the pool forget; a well-formed lie
/// asks it to act, and the two files that can ask it to act destructively are the tombstone log
/// ("these paths were deleted, catch up") and the recycle bin's sidecars ("this file came from
/// there, put it back").
///
/// The rule is the one the path-containment work already established for member listings: content
/// arriving from the disk is INPUT, never instruction. A tombstone may not delete a file that is
/// demonstrably live, and a restore may not write outside the pool because a text file asked it to.
///
/// The last few are about time. Every one of these is an ordinary file in a folder anybody can
/// write to, and making one enormous must cost a proportional read at worst — never a pool that
/// stops answering.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("EdgeCase")]
[NonParallelizable]
public class TamperWeaponisedEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);
  private const string _UTILITY = ".drivebenderutility";

  /// <summary>Deletes go to the recycle bin, so there are sidecars to forge.</summary>
  private const string _TRASH_ON =
    """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "trash": { "enabled": true, "retention": "7d", "maxSize": "50%" } }""";

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  private static IReadOnlyList<string> _Hidden(MountedPool pool, string suffix)
    => [.. pool.MemberPaths
        .Select(m => Path.Combine(m, _UTILITY))
        .Where(Directory.Exists)
        .SelectMany(u => Directory.EnumerateFiles(u, "*", SearchOption.AllDirectories))
        .Where(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))];

  /// <summary>Everything outside the pool's own tree that a restore or replay might have created.</summary>
  /// <summary>
  /// Anything sitting in the pool's PARENT directory under the bait name this scenario planted —
  /// which is where a "../.." in a sidecar lands, and therefore what an escape looks like.
  ///
  /// Scoped to the exact stem, and that matters more than it looks. The parent of a pool root is the
  /// machine's shared temp directory, so an earlier version of this that globbed <c>escaped*</c>
  /// was asking a question about the whole of /tmp: one unrelated file left there by anything, ever,
  /// failed both scenarios permanently, while a clean CI runner passed them. A check whose verdict
  /// is decided by ambient state it does not control is not a check. The trailing wildcard stays so
  /// a half-written <c>.TEMP.$DRIVEBENDER</c> of the bait still counts as an escape.
  /// </summary>
  private static string[] _EscapedEntries(MountedPool pool, string baitStem)
    => [.. Directory.EnumerateFileSystemEntries(
        Path.GetDirectoryName(Path.GetFullPath(pool.Root))!, baitStem + "*", SearchOption.TopDirectoryOnly)];

  /// <summary>
  /// Clears this scenario's bait before it plants it, so the verdict is about THIS run.
  ///
  /// A leftover from a previous run would otherwise be indistinguishable from an escape that just
  /// happened — and since a genuine escape leaves the file behind, the very next run would fail for
  /// the previous run's reason and keep doing so until somebody cleaned the directory by hand.
  /// </summary>
  private static void _ClearEscapeBait(MountedPool pool, string baitStem) {
    foreach (var stale in _EscapedEntries(pool, baitStem))
      try {
        if (Directory.Exists(stale))
          Directory.Delete(stale, recursive: true);
        else
          File.Delete(stale);
      } catch (IOException) {
        // not ours to insist on; the assertion below will report it either way
      }
  }

  [Test]
  [Description("A forged tombstone claims a LIVE file was deleted: replay must not delete a file that is demonstrably there.")]
  public void Tombstones_GivenAForgedRecordNamesALiveFile_ThenReplayDoesNotDeleteIt() {
    // The dangerous direction for this file. A tombstone exists so a member that was away catches
    // up on deletions it slept through, which means the replay path is allowed to REMOVE things.
    // Anyone who can write to the disk can write a well-formed record naming whatever they like.
    var content = _Payload(48 * 1024, 21);
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);

    File.WriteAllBytes(pool.PathTo("precious.bin"), content);
    pool.WaitForPhysicalCopies("precious.bin", atLeast: 2, TimeSpan.FromMinutes(2));

    // a decoy that exists on BOTH members before the eject, so deleting it while one is away is a
    // namespace change that member missed — which is the only thing that writes a tombstone. A
    // write is not one, and an earlier version of this test ejected and then wrote, found no log to
    // forge into, and would have passed having tested nothing.
    File.WriteAllBytes(pool.PathTo("decoy.bin"), _Payload(1024, 22));
    pool.WaitForPhysicalCopies("decoy.bin", atLeast: 2, TimeSpan.FromMinutes(2));

    pool.Eject(1);
    File.Delete(pool.PathTo("decoy.bin"));
    MountedPool.WaitUntil(() => false, TimeSpan.FromSeconds(3));

    var forged = 0;
    pool.WhileUnmounted(() => {
      foreach (var log in _Hidden(pool, "tombstones.jsonl")) {
        var lines = File.ReadAllLines(log).ToList();
        var template = lines.FirstOrDefault(l => l.Contains("\"path\""));
        if (template == null)
          continue;

        // a real record with the path swapped: well-formed, plausible, and a lie
        var record = JsonNode.Parse(template)!.AsObject();
        record["path"] = "precious.bin";
        record["id"] = Guid.NewGuid().ToString();
        lines.Add(record.ToJsonString());
        File.WriteAllLines(log, lines);
        ++forged;
      }
    });

    forged.Should().BeGreaterThan(0, "there must have been a tombstone log to forge a record into");

    pool.Restore(1); // the return is what runs the replay
    MountedPool.WaitUntil(() => false, TimeSpan.FromSeconds(8)); // let the catch-up finish

    File.Exists(pool.PathTo("precious.bin")).Should().BeTrue(
      $"a file that is present on every member was deleted because a text file on one of them said "
      + $"it had been. A tombstone is a record of something the pool did, not an instruction it "
      + $"accepts from the disk.{Environment.NewLine}{pool.DescribeMembers()}");

    File.ReadAllBytes(pool.PathTo("precious.bin")).Should().Equal(content, "and it must be unchanged, not merely present");
  }

  [Test]
  [Description("A recycle-bin sidecar claims its file came from outside the pool: restoring must not write there.")]
  public void Trash_GivenASidecarPointsOutsideThePool_ThenRestoringDoesNotEscape() {
    // The same shape as a hostile directory listing, which path containment already refuses: a
    // string from the disk that becomes a write location. Here it arrives in a .trashinfo rather
    // than in a remote listing, and a restore is exactly the operation that acts on it.
    const string bait = "escaped-from-trash.bin";
    var content = _Payload(16 * 1024, 23);
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);
    _ClearEscapeBait(pool, bait);

    File.WriteAllBytes(pool.PathTo("bin-me.bin"), content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("bin-me.bin").Count >= 1, TimeSpan.FromMinutes(1));
    File.Delete(pool.PathTo("bin-me.bin"));
    MountedPool.WaitUntil(() => _Hidden(pool, ".trashinfo").Count > 0, TimeSpan.FromMinutes(1));

    var poisoned = 0;
    pool.WhileUnmounted(() => {
      foreach (var info in _Hidden(pool, ".trashinfo")) {
        var record = JsonNode.Parse(File.ReadAllText(info))?.AsObject();
        if (record == null || !record.ContainsKey("originalPath"))
          continue;

        record["originalPath"] = "../../escaped-from-trash.bin";
        File.WriteAllText(info, record.ToJsonString());
        ++poisoned;
      }
    });

    poisoned.Should().BeGreaterThan(0, "there must have been a recycle-bin sidecar to poison");

    // restore by the very path the poisoned sidecar advertises — that string IS the attack
    var listed = DbMount.Run(_CLI, "pool-trash-list", pool.PoolName);
    TestContext.Out.WriteLine($"bin listing (exit {listed.ExitCode}):{Environment.NewLine}{listed.Output}");

    var restored = DbMount.Run(_CLI, "pool-trash-restore", pool.PoolName, "../../escaped-from-trash.bin");
    TestContext.Out.WriteLine($"restore exit {restored.ExitCode}:{Environment.NewLine}{restored.Output}");

    _EscapedEntries(pool, bait).Should().BeEmpty(
      $"restoring wrote outside the pool because a sidecar named a path there. Whether the restore "
      + $"succeeds or is refused is a judgement call; writing to '../..' is not."
      + $"{Environment.NewLine}{restored.Output}");
  }

  [Test]
  [Description("A snapshot sidecar claims its stored version belongs outside the pool: nothing is written there.")]
  public void Snapshot_GivenASidecarPointsOutsideThePool_ThenNothingEscapes() {
    const string bait = "escaped-from-snapshot.bin";
    var content = _Payload(16 * 1024, 24);
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    _ClearEscapeBait(pool, bait);

    File.WriteAllBytes(pool.PathTo("snapped.bin"), content);
    pool.WaitForPhysicalCopies("snapped.bin", atLeast: 2, TimeSpan.FromMinutes(2));
    var taken = DbMount.Run(_CLI, "pool-snapshot-take", pool.PoolName, "tampered");
    if (taken.ExitCode != 0)
      Assert.Ignore($"this build could not take a snapshot: {taken.Output}");

    // A snapshot copies nothing when taken; a version is only STORED when the live file changes
    // afterwards. Without this the sidecar the test wants to poison does not exist yet, and the
    // scenario skips itself while looking like it ran.
    File.WriteAllBytes(pool.PathTo("snapped.bin"), _Payload(16 * 1024, 99));
    MountedPool.WaitUntil(() => _Hidden(pool, ".snapinfo").Count > 0, TimeSpan.FromMinutes(1));

    var poisoned = 0;
    pool.WhileUnmounted(() => {
      foreach (var info in _Hidden(pool, ".snapinfo")) {
        var record = JsonNode.Parse(File.ReadAllText(info))?.AsObject();
        if (record == null)
          continue;

        foreach (var key in new[] { "path", "originalPath", "source" })
          if (record.ContainsKey(key))
            record[key] = "../../escaped-from-snapshot.bin";

        File.WriteAllText(info, record.ToJsonString());
        ++poisoned;
      }
    });

    if (poisoned == 0)
      Assert.Ignore("this build stores no per-version snapshot sidecar to poison");

    DbMount.Run(_CLI, "pool-snapshot-restore", pool.PoolName, "tampered", "snapped.bin");
    DbMount.Run(_CLI, "pool-snapshot-restore", pool.PoolName, "tampered", "../../escaped-from-snapshot.bin");
    _EscapedEntries(pool, bait).Should().BeEmpty("a snapshot sidecar must not be able to name a write location outside the pool");
  }

  [Test]
  [Category("Performance")]
  [Description("The tombstone log is inflated to a hundred thousand records: a member's return still completes quickly.")]
  public void Tombstones_GivenAnEnormousLog_ThenAMemberReturnStillCompletes() {
    // The replay runs on the path a returning disk takes, so a log somebody inflated turns a
    // routine reconnection into an outage. Bounded work, not bounded by the attacker.
    var content = _Payload(16 * 1024, 25);
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);

    File.WriteAllBytes(pool.PathTo("survivor.bin"), content);
    pool.WaitForPhysicalCopies("survivor.bin", atLeast: 2, TimeSpan.FromMinutes(2));

    // same as above: it takes a DELETE while a member is away to write a tombstone at all
    File.WriteAllBytes(pool.PathTo("while-away.bin"), _Payload(512, 26));
    pool.WaitForPhysicalCopies("while-away.bin", atLeast: 2, TimeSpan.FromMinutes(2));

    pool.Eject(1);
    File.Delete(pool.PathTo("while-away.bin"));
    MountedPool.WaitUntil(() => false, TimeSpan.FromSeconds(3));

    var inflated = 0;
    pool.WhileUnmounted(() => {
      foreach (var log in _Hidden(pool, "tombstones.jsonl")) {
        ++inflated;
        var padding = Enumerable.Range(0, 100_000).Select(i => new JsonObject {
          ["id"] = Guid.NewGuid().ToString(),
          ["op"] = 3,
          ["path"] = $"never-existed/pad-{i}.bin",
          ["owed"] = new JsonArray(),
        }.ToJsonString());

        File.AppendAllLines(log, padding);
      }
    });

    inflated.Should().BeGreaterThan(0,
      "there must have been a tombstone log to inflate, or this measures an empty file");

    var clock = Stopwatch.StartNew();
    pool.Restore(1);
    var back = MountedPool.WaitUntil(() => pool.IsMountAlive, TimeSpan.FromMinutes(2));
    clock.Stop();

    TestContext.Out.WriteLine($"member return with a 100,000-record tombstone log: {clock.Elapsed.TotalSeconds:F1}s");
    back.Should().BeTrue("the mount must survive a member returning to an inflated log");
    clock.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2),
      $"a returning member took {clock.Elapsed.TotalSeconds:F0}s against an inflated tombstone log — a file "
      + "anybody can append to must not decide how long a reconnection takes");

    File.ReadAllBytes(pool.PathTo("survivor.bin")).Should().Equal(content, "and nothing may be lost on the way");
  }

  [Test]
  [Category("Performance")]
  [Description("The recycle bin holds thousands of entries: listing it stays responsive and the pool keeps working.")]
  public void Trash_GivenThousandsOfEntries_ThenListingStaysResponsive() {
    var content = _Payload(4 * 1024, 27);
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);

    File.WriteAllBytes(pool.PathTo("kept.bin"), content);
    File.WriteAllBytes(pool.PathTo("binned.bin"), _Payload(1024, 28));
    MountedPool.WaitUntil(() => pool.PhysicalCopies("binned.bin").Count >= 1, TimeSpan.FromMinutes(1));
    File.Delete(pool.PathTo("binned.bin"));
    MountedPool.WaitUntil(() => _Hidden(pool, ".trashinfo").Count > 0, TimeSpan.FromMinutes(1));

    pool.WhileUnmounted(() => {
      foreach (var info in _Hidden(pool, ".trashinfo")) {
        var directory = Path.GetDirectoryName(info)!;
        var template = File.ReadAllText(info);
        for (var i = 0; i < 3000; ++i) {
          File.WriteAllText(Path.Combine(directory, $"pad-{i}.bin.trashinfo"), template);
          File.WriteAllBytes(Path.Combine(directory, $"pad-{i}.bin"), [1, 2, 3]);
        }
      }
    });

    var clock = Stopwatch.StartNew();
    var listed = DbMount.Run(_CLI, "pool-trash-list", pool.PoolName);
    clock.Stop();

    TestContext.Out.WriteLine($"listing a bin with 3,000 entries: {clock.Elapsed.TotalSeconds:F1}s (exit {listed.ExitCode})");
    clock.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(1),
      $"listing the recycle bin took {clock.Elapsed.TotalSeconds:F0}s — the bin is a folder anybody can fill");

    File.ReadAllBytes(pool.PathTo("kept.bin")).Should().Equal(content, "and the live pool is unaffected by a crowded bin");
  }

}
