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
  [Description("A file deleted while the drainer is still copying it down to capacity storage stays deleted, rather than reappearing when the copy lands.")]
  public void Delete_WhileTheDrainerIsStillCopying_ThenTheFileDoesNotComeBack() {
    // A drain is read-from-landing, write-to-capacity, then drop the landing copy. Deleting the file
    // in the middle of that leaves an in-flight write with nowhere legitimate to land: if it lands
    // anyway, the user's delete is undone by the pool's own housekeeping, and the file is back.
    using var pool = MountedPool.Create(members: 2, landingZones: 1, storageKinds: [StorageKind.Ram, _SLOW]);

    File.WriteAllBytes(pool.PathTo("doomed.bin"), _Payload(_SIZE, 3001));

    // let the drainer notice and start, then interrupt it PART WAY through
    Thread.Sleep(_INTO_THE_COPY);
    _RequireStillCopying(pool, 1, "doomed.bin", _SIZE);
    File.Delete(pool.PathTo("doomed.bin"));

    // give the interrupted copy every chance to land after the fact
    Thread.Sleep(25000);

    File.Exists(pool.PathTo("doomed.bin")).Should().BeFalse(
      $"a deleted file must not be resurrected by a drain that was already in flight."
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
  [Description("A file renamed while the drainer is copying it ends under exactly one name, with its content intact.")]
  public void Rename_WhileTheDrainerIsStillCopying_ThenItEndsUnderExactlyOneName() {
    using var pool = MountedPool.Create(members: 2, landingZones: 1, storageKinds: [StorageKind.Ram, _SLOW]);

    var content = _Payload(_SIZE, 3100);
    File.WriteAllBytes(pool.PathTo("oldname.bin"), content);

    Thread.Sleep(_INTO_THE_COPY);
    _RequireStillCopying(pool, 1, "oldname.bin", _SIZE);
    File.Move(pool.PathTo("oldname.bin"), pool.PathTo("newname.bin"));
    Thread.Sleep(20000);

    var oldExists = File.Exists(pool.PathTo("oldname.bin"));
    var newExists = File.Exists(pool.PathTo("newname.bin"));

    newExists.Should().BeTrue(
      $"the rename was acknowledged, so the file must be at its new name."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    oldExists.Should().BeFalse(
      $"and it must not ALSO still be at the old one — a drain finishing under the old name leaves "
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
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
      storageKinds: [StorageKind.Ram, _SLOW]);

    var original = _Payload(_SIZE, 3200);
    File.WriteAllBytes(pool.PathTo("raced.bin"), original);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("raced.bin").Count >= 2, TimeSpan.FromMinutes(3))
      .Should().BeTrue($"the file must start fully duplicated.{Environment.NewLine}{pool.DescribeMembers()}");

    // pull the slow member and delete its copy behind the pool's back, so its return owes a heal
    pool.Eject(1);
    foreach (var candidate in Directory.EnumerateFiles(pool.StoragePaths[1], "raced.bin", SearchOption.AllDirectories))
      File.Delete(candidate);

    pool.Restore(1);

    // the healer now has the whole file to copy at a crawl; overwrite it while it is doing so
    Thread.Sleep(_INTO_THE_COPY);
    _RequireStillCopying(pool, 1, "raced.bin", _SIZE);
    var replacement = _Payload(_SIZE, 3201);
    File.WriteAllBytes(pool.PathTo("raced.bin"), replacement);

    // let everything settle: the heal finishes, owed copies drain, the pool calls itself converged
    var converged = MountedPool.WaitUntil(() => {
      var copies = pool.PhysicalCopies("raced.bin");
      return copies.Count >= 2 && copies.All(c => c.content.SequenceEqual(replacement));
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
