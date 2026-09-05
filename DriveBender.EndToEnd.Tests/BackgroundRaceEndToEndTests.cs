using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Foreground namespace changes racing the pool's own background work.
///
/// The drainer and the healer both COPY A WHOLE FILE while the application is free to delete it,
/// rename it, or overwrite it. Every one of those is a chance to resurrect something the user
/// deleted, to leave a file under two names or none, or — the worst — to finish a copy of the OLD
/// content over the top of a newer write and call the pool converged. None of it is reachable from a
/// unit test that drives the engine directly, because the race needs the real driver, the real
/// scheduler and enough elapsed time to land in.
///
/// The elapsed time is arranged rather than hoped for: the second member is held to a few megabytes
/// a second with the pool's own rate limit, so a background copy takes SECONDS and the foreground
/// operation lands squarely inside it. Racing a fixed sleep against an unthrottled copy is a bet on
/// the machine, and this suite has been burned by that before.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class BackgroundRaceEndToEndTests {

  /// <summary>
  /// Deliberately far slower than any of the shared profiles: a whole-file copy of the working set
  /// below has to take TENS of seconds, so a foreground operation a few seconds in lands squarely
  /// inside it rather than after it.
  ///
  /// The first version of this file used the shared "cloud" profile (6 MiB/s) against a 12 MiB file
  /// and slept three seconds before racing it. The rate limit's bucket holds one second of credit,
  /// so the copy was over in about one second and every scenario here was quietly testing a pool
  /// with nothing in flight — passing, and proving nothing.
  /// </summary>
  private static readonly StorageKind _SLOW = new("crawling (simulated)", null, 8, 1024 * 1024);

  /// <summary>
  /// Three copies on one disk: what the overwrite race needs to be constructible at all.
  ///
  /// A pool that owes a copy has fewer reachable copies than its duplication level, and the ack
  /// floor is min(2, D) — so at D=2 a heal-owing file has one copy and every write to it is refused.
  /// At D=3 the two survivors satisfy the floor while the third is still being healed, which is the
  /// only state in which "a write lands mid-heal" is a thing that can happen.
  /// </summary>
  private const string _TRIPLICATED_ON_ONE_DISK =
    """{ "duplication": 3, "placement": { "shadowNeverSamePhysical": false } }""";

  /// <summary>At the rate above this is roughly half a minute of copying, which is the race window.</summary>
  private const int _SIZE = 24 * 1024 * 1024;

  /// <summary>How long to let the background copy run before the foreground operation interrupts it.</summary>
  private static readonly TimeSpan _INTO_THE_COPY = TimeSpan.FromSeconds(6);

  /// <summary>
  /// Asserts the background copy really is UNFINISHED, so the scenario is a race and not a sequence.
  ///
  /// The destination either has nothing yet or has a partial file; what it must not have is the
  /// whole thing, because then the interesting moment has already passed.
  /// </summary>
  private static void _RequireStillCopying(MountedPool pool, int destination, string name, int fullLength) {
    var landed = Directory.Exists(pool.MemberPaths[destination])
      ? Directory.EnumerateFiles(pool.MemberPaths[destination], name, SearchOption.AllDirectories)
        .Select(p => new FileInfo(p).Length).ToArray()
      : [];

    landed.Should().NotContain(fullLength,
      $"'{name}' is already complete on member {destination}, so the copy this scenario means to "
      + $"interrupt has finished and there is no race left to test. Slow the member down or grow "
      + $"the file.{Environment.NewLine}{pool.DescribeMembers()}");
  }

  /// <summary>
  /// A duplicated pool with a whole-file copy DEMONSTRABLY in flight to the slow member.
  ///
  /// The background copy is arranged rather than hoped for. Waiting for the drainer to pick a file
  /// up depends on which tier placement chose and on when the drain scheduler next runs, and those
  /// differ enough between platforms that the same sleep caught the copy mid-flight on one and long
  /// after it had finished on the other. Emptying a member behind the pool's back and handing it
  /// back OWES a heal, which starts promptly and has the whole file to move at the member's crawl.
  /// </summary>
  private static MountedPool _WithAHealInFlight(string name, byte[] content) {
    var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
      storageKinds: [StorageKind.Ram, _SLOW]);

    try {
      File.WriteAllBytes(pool.PathTo(name), content);
      MountedPool.WaitUntil(() => pool.PhysicalCopies(name).Count >= 2, TimeSpan.FromMinutes(4))
        .Should().BeTrue($"the file must start fully duplicated.{Environment.NewLine}{pool.DescribeMembers()}");

      pool.Eject(1);
      foreach (var candidate in Directory.EnumerateFiles(pool.StoragePaths[1], name, SearchOption.AllDirectories))
        File.Delete(candidate);

      pool.Restore(1);
      Thread.Sleep(_INTO_THE_COPY);
      _RequireStillCopying(pool, 1, name, content.Length);
      return pool;
    } catch {
      pool.Dispose();
      throw;
    }
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file stays readable at full speed while the healer is copying it to another member.")]
  public void Read_WhileTheHealerIsCopying_ThenItIsServedAtOnceRatherThanAtTheCopysPace() {
    // The same question the drainer already answered badly: does the pool's own whole-file copy
    // make the file unreadable for as long as it runs? _HealOne takes the path's exclusive lease
    // for its whole promote/copy sequence, and a foreground read takes a read lease on that path.
    const string name = "healing.bin";
    var content = _Payload(_SIZE, 4100);
    using var pool = _WithAHealInFlight(name, content);

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var served = File.ReadAllBytes(pool.PathTo(name));
    stopwatch.Stop();
    TestContext.Out.WriteLine($"[heal] read of a file mid-heal took {stopwatch.Elapsed.TotalSeconds:F1}s");

    served.Should().Equal(content, "and it is the file, not a half-copied one");
    stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
      $"reading a file the pool happens to be duplicating took {stopwatch.Elapsed.TotalSeconds:F0}s. "
      + $"There is a complete, untouched copy on the other member the whole time — a read has no "
      + $"reason to wait for the copy to finish."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A member returns having lost its copies: every file is still readable at once from the surviving copy, without waiting for the heal.")]
  public void Read_GivenAReturnedMemberLostItsCopies_ThenEveryFileIsStillServedFromTheSurvivor() {
    // The pool is duplicated, so every one of these exists complete on the other member the whole
    // time. Losing a member's copies must therefore cost latency at worst, never an error — the
    // healer restoring duplication is housekeeping, not a precondition for reading.
    const int files = 6;
    var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
      storageKinds: [StorageKind.Ram, _SLOW]);

    using (pool) {
      var written = new Dictionary<string, byte[]>();
      for (var index = 0; index < files; ++index) {
        var content = _Payload(2 * 1024 * 1024, 4200 + index);
        File.WriteAllBytes(pool.PathTo($"kept{index}.bin"), content);
        written[$"kept{index}.bin"] = content;
      }

      foreach (var name in written.Keys)
        MountedPool.WaitUntil(() => pool.PhysicalCopies(name).Count >= 2, TimeSpan.FromMinutes(4))
          .Should().BeTrue($"'{name}' must start fully duplicated.{Environment.NewLine}{pool.DescribeMembers()}");

      // the member goes away, loses everything it held, and comes back — a restored-from-blank disk
      pool.Eject(1);
      foreach (var candidate in Directory.EnumerateFiles(pool.StoragePaths[1], "kept*.bin", SearchOption.AllDirectories))
        File.Delete(candidate);

      pool.Restore(1);

      // read at once, while the healer is still working through the backlog at the slow member's
      // pace — most of these have not been touched by it yet
      var failures = new List<string>();
      foreach (var (name, content) in written)
        try {
          File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content, $"'{name}' must read back whole");
        } catch (Exception e) {
          failures.Add($"{name}: {e.GetType().Name} {e.Message}");
        }

      failures.Should().BeEmpty(
        $"every one of these has a complete copy on the surviving member, so none of them can fail "
        + $"to be read merely because the other member came back empty and the heal has not caught "
        + $"up yet.{Environment.NewLine}{string.Join(Environment.NewLine, failures)}"
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
    }
  }

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>Every physical trace of a pool-relative name, across members, primaries and shadows.</summary>
  private static string[] _EveryTraceOf(MountedPool pool, string name) {
    var found = new List<string>();
    foreach (var member in pool.MemberPaths) {
      if (!Directory.Exists(member))
        continue;

      found.AddRange(Directory.EnumerateFiles(member, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p).StartsWith(name, StringComparison.OrdinalIgnoreCase)));
    }

    return [.. found];
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file deleted while the pool is still copying it stays deleted, rather than reappearing when the copy lands.")]
  public void Delete_WhileACopyIsStillInFlight_ThenTheFileDoesNotComeBack() {
    // Deleting a file while the pool is part way through copying it leaves an in-flight write with
    // nowhere legitimate to land. If it lands anyway, the user's delete is undone by the pool's own
    // housekeeping and the file is back — on the next mount if not immediately.
    using var pool = _WithAHealInFlight("doomed.bin", _Payload(_SIZE, 3001));

    File.Delete(pool.PathTo("doomed.bin"));

    // give the interrupted copy every chance to land after the fact
    Thread.Sleep(25000);

    File.Exists(pool.PathTo("doomed.bin")).Should().BeFalse(
      $"a deleted file must not be resurrected by a copy that was already in flight."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    _EveryTraceOf(pool, "doomed.bin").Should().BeEmpty(
      $"and nothing may be left on any member under that name — a copy the pool still believes in is "
      + $"a file that comes back on the next mount.{Environment.NewLine}{pool.DescribeMembers()}");

    pool.IsMountAlive.Should().BeTrue($"the mount must survive it.{Environment.NewLine}{pool.MountLog}");

    // and the pool is still usable afterwards
    var after = _Payload(64 * 1024, 3002);
    File.WriteAllBytes(pool.PathTo("after.bin"), after);
    File.ReadAllBytes(pool.PathTo("after.bin")).Should().Equal(after);
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file renamed while the pool is still copying it ends under exactly one name, with its content intact.")]
  public void Rename_WhileACopyIsStillInFlight_ThenItEndsUnderExactlyOneName() {
    var content = _Payload(_SIZE, 3100);
    using var pool = _WithAHealInFlight("oldname.bin", content);

    File.Move(pool.PathTo("oldname.bin"), pool.PathTo("newname.bin"));
    Thread.Sleep(25000);

    File.Exists(pool.PathTo("newname.bin")).Should().BeTrue(
      $"the rename was acknowledged, so the file must be at its new name."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.Exists(pool.PathTo("oldname.bin")).Should().BeFalse(
      $"and it must not ALSO still be at the old one — a copy finishing under the old name leaves "
      + $"the user with two files where they made one.{Environment.NewLine}{pool.DescribeMembers()}");

    File.ReadAllBytes(pool.PathTo("newname.bin")).Should().Equal(content,
      "and the content must be what was written, not a half-copied version of it");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file overwritten while the healer is copying the OLD content to a returning member ends with both copies on the NEW content.")]
  public void Overwrite_WhileTheHealerIsCopyingTheOldContent_ThenBothCopiesEndOnTheNewOne() {
    // The heal race, and the one that actually loses data. A member comes back missing a copy, the
    // healer starts copying the file to it, and the application overwrites that file mid-copy. If
    // the healer's write is allowed to land after the overwrite, the returning member ends up
    // holding the OLD content — and the pool believes it is fully duplicated, so nothing ever
    // reconciles it. Whichever copy is read next is then a coin toss between two versions.
    //
    // THREE members at duplication three, so the overwrite can actually happen. The ack floor is
    // min(2, D) copies, and this scenario deliberately leaves the pool owing a copy — at duplication
    // two that means ONE reachable copy, and the pool refuses the write outright rather than
    // acknowledge something it cannot make durable twice. Which is correct, and it meant this
    // scenario never ran its own race: the overwrite could only land once the heal had finished, so
    // what it actually tested was an overwrite AFTER a heal. At duplication three the two surviving
    // copies satisfy the floor, the write is taken, and the healer is still mid-copy when it lands.
    using var pool = MountedPool.Create(members: 3, poolDefaults: _TRIPLICATED_ON_ONE_DISK,
      storageKinds: [StorageKind.Ram, StorageKind.Ram, _SLOW]);

    var original = _Payload(_SIZE, 3200);
    File.WriteAllBytes(pool.PathTo("raced.bin"), original);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("raced.bin").Count >= 3, TimeSpan.FromMinutes(3))
      .Should().BeTrue($"the file must start fully duplicated.{Environment.NewLine}{pool.DescribeMembers()}");

    // pull the slow member and delete its copy behind the pool's back, so its return owes a heal
    pool.Eject(2);
    foreach (var candidate in Directory.EnumerateFiles(pool.StoragePaths[2], "raced.bin", SearchOption.AllDirectories))
      File.Delete(candidate);

    pool.Restore(2);

    // the healer now has the whole file to copy at a crawl; overwrite it while it is doing so
    Thread.Sleep(_INTO_THE_COPY);
    _RequireStillCopying(pool, 2, "raced.bin", _SIZE);
    var replacement = _Payload(_SIZE, 3201);
    File.WriteAllBytes(pool.PathTo("raced.bin"), replacement);

    // let everything settle: the heal finishes, owed copies drain, the pool calls itself converged
    var converged = MountedPool.WaitUntil(() => {
      var copies = pool.PhysicalCopies("raced.bin");
      return copies.Count >= 3 && copies.All(c => c.content.SequenceEqual(replacement));
    }, TimeSpan.FromMinutes(4));

    var copies = pool.PhysicalCopies("raced.bin");
    converged.Should().BeTrue(
      $"every copy must end on the content that was written LAST. A healer that finishes after an "
      + $"overwrite must not leave the older version behind on the member it was healing — the pool "
      + $"then believes it is duplicated while its two copies disagree, and which one a read gets is "
      + $"a coin toss. Copies now: "
      + $"{string.Join(", ", copies.Select(c => $"{c.where} ({c.content.Length} bytes, "
        + $"{(c.content.SequenceEqual(replacement) ? "new" : c.content.SequenceEqual(original) ? "OLD" : "neither")})"))}"
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("raced.bin")).Should().Equal(replacement,
      "and the mount must serve the newest content");
  }

}
