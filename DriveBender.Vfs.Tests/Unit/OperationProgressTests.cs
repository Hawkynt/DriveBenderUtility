using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Long pool operations have to be watchable and stoppable.
///
/// A deep scrub or a duplication restore runs for as long as the DATA takes — minutes to hours on
/// a real pool. Until now the only thing either could say was the worker's most recent line of
/// text, and neither could be stopped at all once relayed into a mounted pool's own process. Both
/// gaps are about the same thing: an operation that owns the machine for an hour owes the person
/// watching it a count and a way out.
/// </summary>
[TestFixture]
[Category("Unit")]
public class OperationProgressTests {

  private static FakeVolumeIO _Member(string name, string physical, int files) {
    var member = new FakeVolumeIO(Guid.NewGuid(), name, physical, capacity: 1L << 30);
    for (var i = 0; i < files; ++i)
      member.Seed($"file{i:D3}.bin", false, [(byte)i, (byte)(i + 1), (byte)(i + 2)]);

    return member;
  }

  [Test]
  [Category("HappyPath")]
  public void Scrub_WhenItRuns_ThenItReportsWhichFileAndHowManyOfHowMany() {
    var member = _Member("m0", "PHYS-0", files: 12);
    var integrity = new IntegrityService([member]);
    var steps = new List<OperationStep>();

    integrity.ScrubAll(operation: new(Report: steps.Add));

    // the denominator is the point: "still working" and "eleven of twelve" are different answers
    var counted = steps.Where(s => s.Total > 0).ToArray();
    counted.Should().NotBeEmpty("a scrub knows how many files it is going to look at");
    counted.Should().OnlyContain(s => s.Total == 12, "the total must not move once the pass has started");
    counted.Select(s => s.Item).Should().Contain("file007.bin", "and it must say WHICH file, not just how many");
    counted.Last().Completed.Should().Be(12, "the last step accounts for every file");
  }

  [Test]
  [Category("EdgeCase")]
  public void Scrub_WhenAskedToStop_ThenItStopsBetweenFilesAndKeepsWhatItLearned() {
    var member = _Member("m0", "PHYS-0", files: 40);
    var integrity = new IntegrityService([member]);
    using var stopping = new CancellationTokenSource();
    var seen = 0;

    var scrub = () => integrity.ScrubAll(operation: new(stopping.Token, step => {
      if (step.Total > 0 && ++seen == 5)
        stopping.Cancel();
    }));

    scrub.Should().Throw<OperationCanceledException>("a stopped operation says so rather than reporting a partial pass as complete");
    seen.Should().BeLessThan(40, "it must actually have stopped early, or this proves nothing");

    // Cancelling means "stop starting new work", not "throw away what you learned". The checksum
    // baseline the pass established for the files it DID reach is persisted, so the next scrub
    // resumes from a real baseline instead of starting from nothing every time it is interrupted.
    var resumed = new List<OperationStep>();
    integrity.ScrubAll(operation: new(Report: resumed.Add));
    resumed.Where(s => s.Total > 0).Last().Completed.Should().Be(40, "the pool is still fully scrubbable afterwards");
  }

  [Test]
  [Category("EdgeCase")]
  public void Restore_WhenAskedToStop_ThenItStopsBetweenFilesRatherThanMidCopy() {
    var first = _Member("m0", "PHYS-0", files: 30);
    var second = new FakeVolumeIO(Guid.NewGuid(), "m1", "PHYS-1", capacity: 1L << 30);
    var media = new MediaLifecycle([first, second], new Journal(new MemberJournalStore([first, second])), duplicationLevel: 2, allowSamePhysical: true);
    using var stopping = new CancellationTokenSource();
    var seen = 0;

    var restore = () => media.RestorePool(new(stopping.Token, step => {
      if (step.Total > 0 && ++seen == 3)
        stopping.Cancel();
    }));

    restore.Should().Throw<OperationCanceledException>();

    // Whatever copies it managed before the stop are WHOLE — the check happens between files, so a
    // copy is never abandoned half-written, which is the very state a restore exists to repair.
    // Only the pool's own files are inspected; the journal sidecar is the engine's bookkeeping and
    // has nothing to do with duplication.
    var copied = second.FilePaths.Where(p => p.Contains("file", StringComparison.Ordinal) && p.EndsWith(".bin", StringComparison.Ordinal)).ToArray();
    copied.Should().NotBeEmpty("it must have copied something before stopping, or this proves nothing");
    foreach (var path in copied)
      second.GetContent(PoolPaths.GetName(path), shadow: true)!.Length
        .Should().Be(3, $"'{path}' was copied whole or not at all");
  }

  [Test]
  [Category("HappyPath")]
  public void Step_GivenNoDenominator_ThenItReadsAsAPhaseRatherThanAFraction() {
    OperationStep.Phase("listing the pool").Fraction.Should().BeNull("a total of zero means unknown, not empty");
    OperationStep.Phase("listing the pool").ToString().Should().Be("listing the pool");
    new OperationStep(3, 12, "a.bin").Fraction.Should().BeApproximately(0.25, 0.001);
    new OperationStep(3, 12, "a.bin").ToString().Should().Be("3 of 12 — a.bin");
  }

}
