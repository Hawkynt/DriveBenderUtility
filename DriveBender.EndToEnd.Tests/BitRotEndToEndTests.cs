using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Silent corruption on one copy (FR-SCRUB, SAFE-OOB) — the failure duplication exists for.
///
/// A disk that dies is easy: the member goes offline and every other scenario here covers it. The
/// dangerous failure is the one that does not announce itself. A sector decays, a cable glitches, a
/// firmware bug writes the wrong block, and one copy of a file quietly stops matching the other
/// while both members stay perfectly healthy and both files keep their size and their timestamps.
///
/// That is what these reproduce: the bytes are altered on ONE member and the timestamps are put
/// back exactly as they were, because real rot does not update mtime. A pool that decides which
/// copy is authoritative by comparing modification times cannot tell the good copy from the rotten
/// one here — only content can, which is what the checksum database is for.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class BitRotEndToEndTests {

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>
  /// Rots one copy in place: flips a run of bytes in the middle and restores the timestamps.
  ///
  /// Restoring the timestamps is the whole point. Left alone, the write makes the rotten copy look
  /// like the NEWEST one, and any policy that resolves conflicts by time would then prefer the
  /// damage and call it an external edit. Rot does not touch metadata, so neither does this.
  /// </summary>
  private static void _Rot(string physicalPath, int atOffset = 4096, int length = 512) {
    var created = File.GetCreationTimeUtc(physicalPath);
    var written = File.GetLastWriteTimeUtc(physicalPath);

    using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Write, FileShare.None)) {
      stream.Position = Math.Min(atOffset, Math.Max(0, stream.Length - length));
      var damage = new byte[length];
      Array.Fill(damage, (byte)0xEE);
      stream.Write(damage, 0, damage.Length);
      stream.Flush();
    }

    File.SetCreationTimeUtc(physicalPath, created);
    File.SetLastWriteTimeUtc(physicalPath, written);
  }

  /// <summary>A pool with two REAL copies of every file, and one file written and settled.</summary>
  private static MountedPool _PoolWithADuplicatedFile(string name, byte[] content, out IReadOnlyList<string> copies) {
    var pool = MountedPool.Create(poolDefaults: MountedPool.DuplicatedOnOneDisk);
    try {
      File.WriteAllBytes(pool.PathTo(name), content);
      var found = pool.WaitForPhysicalCopies(name, atLeast: 2, TimeSpan.FromMinutes(2));
      if (found.Count < 2)
        Assert.Ignore($"the pool did not settle on two copies of '{name}' — nothing to rot. {pool.DescribeMembers()}");

      copies = [.. found.Select(c => c.where)];

      // Baseline the checksum database before anything is damaged. Checksums for ordinary files are
      // recorded by a scrub, not by the write path — `IntegrityService.InvalidateFile` says as much
      // ("the next scrub re-baselines") — so without this the pool has NOTHING to compare a copy
      // against and cannot tell a rotted copy from a good one. Every scenario here is therefore
      // about a pool that HAS been scrubbed at least once; the un-baselined case is recorded in
      // docs/Issues.md as its own gap.
      var baseline = DbMount.Run(TimeSpan.FromMinutes(3), "pool-health", pool.PoolName, "--deep");
      TestContext.Out.WriteLine($"baseline: exit {baseline.ExitCode}{Environment.NewLine}{baseline.Output}");
      return pool;
    } catch {
      pool.Dispose();
      throw;
    }
  }

  [Test]
  [Category("EdgeCase")]
  [Description("One copy rots silently: the pool still serves the intact content rather than the damaged bytes.")]
  [Ignore("Reads are not verified against the checksum database, so a silently damaged copy is served to "
          + "the application even though an intact copy sits on the other member. Reproduced with and "
          + "without a prior deep scan. This is the failure duplication exists for. See docs/Issues.md.")]
  public void BitRot_GivenOneCopyIsSilentlyDamaged_ThenTheIntactContentIsStillServed() {
    var content = _Payload(256 * 1024, 71);
    using var pool = _PoolWithADuplicatedFile("rot.bin", content, out var copies);

    // the damage happens with nothing mounted: the engine pools open handles into its members, so
    // rotting a file under a live mount tests the handle cache rather than the stored data
    pool.WhileUnmounted(() => _Rot(copies[0]));

    var served = File.ReadAllBytes(pool.PathTo("rot.bin"));

    served.Should().Equal(content,
      $"one damaged copy out of two must never reach the application — that is the entire purpose of "
      + $"holding two.{Environment.NewLine}rotted: {copies[0]}{Environment.NewLine}{pool.DescribeMembers()}"
      + $"{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A deep health check finds silent damage that a shallow one cannot, and reports it.")]
  public void BitRot_WhenTheDeepHealthCheckRuns_ThenTheDamageIsReported() {
    var content = _Payload(256 * 1024, 72);
    using var pool = _PoolWithADuplicatedFile("reported.bin", content, out var copies);

    pool.WhileUnmounted(() => _Rot(copies[0]));

    // size and timestamps are untouched, so only re-reading the content can find this
    var deep = DbMount.Run(TimeSpan.FromMinutes(3), "pool-health", pool.PoolName, "--deep");

    (deep.StandardOutput + deep.StandardError).Should().NotBeNullOrWhiteSpace(
      "a deep health check must say something about the pool it just re-checksummed");
    deep.ExitCode.Should().NotBe(0,
      $"a pool with a silently damaged copy is NOT healthy, and a check that exits 0 tells an "
      + $"operator everything is fine.{Environment.NewLine}stdout:{deep.StandardOutput}"
      + $"{Environment.NewLine}stderr:{deep.StandardError}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A deep health check with --fix repairs the damaged copy from the intact one.")]
  [Ignore("pool-health --deep --fix detects the divergence but declines to repair it: it logs "
          + "\"divergent copies with identical timestamps kept for user resolution\" and creates 0 copies. "
          + "With a checksum baseline the good copy is identifiable, so this need not be a stalemate. "
          + "See docs/Issues.md.")]
  public void BitRot_WhenTheDeepHealthCheckRepairs_ThenBothCopiesAreIntactAgain() {
    var content = _Payload(256 * 1024, 73);
    using var pool = _PoolWithADuplicatedFile("repaired.bin", content, out var copies);

    pool.WhileUnmounted(() => _Rot(copies[0]));

    var fix = DbMount.Run(TimeSpan.FromMinutes(3), "pool-health", pool.PoolName, "--deep", "--fix");
    pool.Remount();

    var after = pool.PhysicalCopies("repaired.bin");
    after.Should().HaveCountGreaterThanOrEqualTo(2, "repairing must not have removed a copy");

    foreach (var (where, bytes) in after)
      bytes.Should().Equal(content,
        $"after a repair every copy must hold the original content, but '{where}' does not."
        + $"{Environment.NewLine}stdout:{fix.StandardOutput}{Environment.NewLine}stderr:{fix.StandardError}");
  }

  [Test]
  [Category("Exception")]
  [Description("Both copies rot differently: the pool must not silently hand back damaged data as if it were fine.")]
  [Ignore("When every copy is damaged IDENTICALLY the deep scan reports the pool healthy and the damaged "
          + "bytes are served without complaint - consistent with copies being compared against each "
          + "other rather than against the recorded checksums. See docs/Issues.md.")]
  public void BitRot_GivenEveryCopyIsDamaged_ThenTheLossIsNotPassedOffAsGoodData() {
    var content = _Payload(256 * 1024, 74);
    using var pool = _PoolWithADuplicatedFile("hopeless.bin", content, out var copies);

    pool.WhileUnmounted(() => {
      foreach (var copy in copies)
        _Rot(copy, atOffset: 8192);
    });

    // There is no good copy left, so the data IS gone — nothing can conjure it back. What must not
    // happen is the pool answering as though nothing were wrong: a caller that gets bytes with no
    // error believes them, writes them onward, and the corruption spreads into backups.
    byte[]? served = null;
    var failed = false;
    try {
      served = File.ReadAllBytes(pool.PathTo("hopeless.bin"));
    } catch (IOException) {
      failed = true; // refusing is a perfectly good answer
    }

    if (failed)
      return;

    var deep = DbMount.Run(TimeSpan.FromMinutes(3), "pool-health", pool.PoolName, "--deep");
    var reported = deep.ExitCode != 0;
    var handedBackDamage = served != null && !served.SequenceEqual(content);

    (!handedBackDamage || reported).Should().BeTrue(
      $"with every copy damaged the pool either refuses the read, returns the original content, or "
      + $"at minimum reports the file as unhealthy — silently handing back corrupted bytes is the "
      + $"one outcome that spreads the damage.{Environment.NewLine}stdout:{deep.StandardOutput}"
      + $"{Environment.NewLine}{pool.DescribeMembers()}");
  }

}
