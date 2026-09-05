using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Power cuts in the middle of the drainer moving a file DOWN A TIER.
///
/// The durability suite cuts the power under foreground writes; this one cuts it under the pool's
/// own relocation, which has a different and nastier shape. A drain copies a whole file from the
/// landing zone to capacity and only then removes the original, so the crash can land in three
/// places: before the copy is complete (a half-written file on capacity, the good one still on the
/// fast tier), after the copy but before the delete (the same file on BOTH tiers), or between the
/// delete and the journal entry that says so. The middle one is the interesting one — recovery has
/// to decide which of two real copies is the file, and getting that wrong either loses the data or
/// leaves the pool serving a duplicate forever.
///
/// The window is reachable because the landing zone can be held to a crawl. That is a recent
/// capability: a rate limit used to bound only the member being written TO, so limiting the disk a
/// drain reads FROM did nothing at all and the copy finished before any kill could land.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class DrainCrashEndToEndTests {

  private const int _SIZE = 8 * 1024 * 1024;

  /// <summary>
  /// Slow enough that draining <see cref="_SIZE"/> takes about eight seconds — a window a kill lands
  /// inside comfortably, without being so wide that a clean unmount elsewhere has to wait it out.
  /// </summary>
  private const long _CRAWL = 1024 * 1024;

  /// <summary>
  /// Holds the landing zone's BACKGROUND copying to a crawl, before anything is written to it.
  ///
  /// Both halves matter. Per-kind, because limiting the member outright would slow the setup write
  /// as much as the drain it exists to catch. Beforehand, because on tmpfs the drain of a settled
  /// file completes in well under a second: applying the limit after the write is a race the test
  /// loses every time, and it loses it invisibly, by passing.
  /// </summary>
  private static void _CrawlTheDrain(MountedPool pool) {
    DbMount.SetMemberThroughput(pool.PoolName, pool.MemberPaths[0], background: _CRAWL);
    DbMount.RequestLiveReload(pool.PoolName);
    Thread.Sleep(2500); // the pump consumes the reload on its next tick
  }

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>Which members hold the file, by index — member 0 is the landing zone, member 1 is capacity.</summary>
  private static IReadOnlyList<int> _MembersHolding(MountedPool pool, string name) {
    var holders = new List<int>();
    for (var index = 0; index < pool.MemberPaths.Count; ++index)
      if (File.Exists(Path.Combine(pool.MemberPaths[index], name)))
        holders.Add(index);

    return holders;
  }

  [Test]
  [Category("EdgeCase")]
  [Description("The power goes off while the drainer is copying a file down to capacity: the file comes back whole, on one tier or the other.")]
  public void Crash_GivenADrainWasInFlight_ThenTheFileSurvivesWholeOnOneTier() {
    using var pool = MountedPool.CreateTieredAlwaysLanding();
    var content = _Payload(_SIZE, 31);

    _CrawlTheDrain(pool);
    File.WriteAllBytes(pool.PathTo("draining.bin"), content);
    pool.RequireLandedOnFastTier("draining.bin");

    // Wait for a HALF-WRITTEN file on capacity — the copy in flight, not merely begun and not
    // finished. This is assertable rather than hoped for precisely because the rate was set rather
    // than measured: eight megabytes at one per second is eight seconds on any machine, so unlike a
    // race against the host's speed there is no fast runner on which the window closes.
    var sawStaging = MountedPool.WaitUntil(() => pool.StagingFiles().Count > 0, TimeSpan.FromMinutes(3));
    sawStaging.Should().BeTrue(
      $"the drain must be caught mid-copy, or the crash lands somewhere this scenario is not about."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    pool.CrashAndRemount();

    var holders = _MembersHolding(pool, "draining.bin");
    holders.Should().NotBeEmpty(
      $"a file the pool accepted cannot vanish because the power went off while IT chose to move it."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("draining.bin")).Should().Equal(content,
      $"the drain must not alter a byte, however it was interrupted."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A crash mid-drain leaves no half-written staging file visible to the user after the pool comes back.")]
  public void Crash_GivenADrainWasInFlight_ThenNoStagingFileIsExposed() {
    using var pool = MountedPool.CreateTieredAlwaysLanding();
    var content = _Payload(_SIZE, 32);

    _CrawlTheDrain(pool);
    File.WriteAllBytes(pool.PathTo("staged.bin"), content);
    pool.RequireLandedOnFastTier("staged.bin");

    MountedPool.WaitUntil(() => pool.StagingFiles().Count > 0, TimeSpan.FromMinutes(3)).Should().BeTrue(
      $"there has to BE a staging file to leak before its absence afterwards means anything."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    pool.CrashAndRemount();

    // whatever is left on the disks, the NAMESPACE the user sees holds exactly one entry and it is
    // theirs — a *.TEMP.$DRIVEBENDER showing through would be the pool leaking its own bookkeeping
    var visible = Directory.EnumerateFiles(pool.MountPath, "*", SearchOption.AllDirectories)
      .Select(Path.GetFileName)
      .ToList();

    visible.Should().NotContain(n => n!.EndsWith(".TEMP.$DRIVEBENDER", StringComparison.OrdinalIgnoreCase),
      $"an interrupted drain's staging file must never appear in the pool."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("staged.bin")).Should().Equal(content, "and the real file is intact");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("The same file left on two members, as a crash between a relocation's copy and its delete leaves it: the pool serves one entry, not two.")]
  public void Recovery_GivenOnePathOnTwoMembers_ThenThePoolServesItOnceAndWhole() {
    using var pool = MountedPool.Create(members: 2);
    var content = _Payload(4 * 1024 * 1024, 33);

    File.WriteAllBytes(pool.PathTo("both.bin"), content);
    var copies = pool.WaitForPhysicalCopies("both.bin");
    copies.Should().NotBeEmpty($"the write must reach a member.{Environment.NewLine}{pool.DescribeMembers()}");

    // Forge the state rather than racing for it. Every relocation the pool performs — a drain down a
    // tier, a heal, a scatter, a media replace — copies the file to its destination and only then
    // removes the original, so a power cut in between leaves exactly this: one path, two members,
    // identical bytes. Racing a real drain for that window is a bet on the host being slow; the
    // state itself is what recovery has to handle, and it can simply be built.
    var other = pool.MemberPaths.First(m => !copies[0].where.StartsWith(m, StringComparison.Ordinal));
    pool.WhileUnmounted(() => {
      var forged = Path.Combine(other, "both.bin");
      Directory.CreateDirectory(Path.GetDirectoryName(forged)!);
      File.Copy(copies[0].where, forged, overwrite: true);
    });

    Directory.EnumerateFiles(pool.MountPath, "both.bin", SearchOption.AllDirectories).Should().HaveCount(1,
      $"two physical copies of one path are a duplication detail, never two entries to the user."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("both.bin")).Should().Equal(content,
      $"and reading it returns the file, not a concatenation, a truncation or an empty result."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

}
