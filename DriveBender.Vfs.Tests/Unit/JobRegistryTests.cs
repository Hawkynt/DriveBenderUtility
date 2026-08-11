using DivisonM.Mount;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The management UI's responsiveness guarantee (NFR-UI-LIVE). A deep scan, a restore, scattering
/// a member's data or purging a pool run for as long as the DATA takes. Executed inline they held
/// the browser's request and a daemon thread open for the whole run — a dead spinner that any
/// proxy timeout discarded, a page reload lost, and nothing could stop.
///
/// What has to be true, and is asserted here rather than left to inspection: starting a job
/// RETURNS AT ONCE however long the work runs, progress is observable while it runs, cancellation
/// actually reaches the work, and finished jobs are eventually forgotten so a daemon that has run
/// for months is not still holding every result it ever produced.
/// </summary>
[TestFixture]
[Category("Unit")]
public class JobRegistryTests {

  [Test]
  [Category("HappyPath")]
  public void Start_GivenWorkThatRunsForever_ThenTheCallerIsNotHeldWaitingForIt() {
    var registry = new JobRegistry();
    using var release = new ManualResetEventSlim(false);

    var started = System.Diagnostics.Stopwatch.StartNew();
    var job = registry.Start("slow", "pool-1", (_, _) => {
      release.Wait(TimeSpan.FromSeconds(30));
      return new { ok = true };
    });

    started.Stop();

    // this is the whole point: the endpoint answers in milliseconds, not in however long the
    // operation takes
    started.ElapsedMilliseconds.Should().BeLessThan(1000, "starting a job must not wait for the work");
    job.IsFinished.Should().BeFalse();
    registry.Running().Should().ContainSingle().Which.Id.Should().Be(job.Id);

    release.Set();
    _WaitUntil(() => job.IsFinished).Should().BeTrue();
  }

  [Test]
  [Category("HappyPath")]
  public void Progress_GivenTheWorkReportsIt_ThenItIsVisibleWhileTheJobIsStillRunning() {
    var registry = new JobRegistry();
    using var reported = new ManualResetEventSlim(false);
    using var release = new ManualResetEventSlim(false);

    var job = registry.Start("scan", "pool-1", (_, progress) => {
      progress("scanning member 2 of 3");
      reported.Set();
      release.Wait(TimeSpan.FromSeconds(30));
      return new { ok = true };
    });

    reported.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
    job.Progress.Should().Be("scanning member 2 of 3", "the UI must be able to show what the work is doing, not just that it is running");
    job.IsFinished.Should().BeFalse("progress must be readable BEFORE the job completes");

    release.Set();
    _WaitUntil(() => job.IsFinished).Should().BeTrue();
  }

  [Test]
  [Category("EdgeCase")]
  public void Cancel_GivenARunningJob_ThenTheWorkIsAskedToStopAndTheResultSaysSo() {
    var registry = new JobRegistry();
    using var observed = new ManualResetEventSlim(false);

    var job = registry.Start("purge", "pool-1", (cancellation, _) => {
      observed.Set();
      cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
      cancellation.ThrowIfCancellationRequested();
      return new { ok = true };
    });

    observed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
    registry.Cancel(job.Id).Should().Be(JobRegistry.CancelResult.Cancelling);
    job.IsCancelling.Should().BeTrue("the UI needs to show that a stop was requested");

    _WaitUntil(() => job.IsFinished).Should().BeTrue("cancellation must actually reach the work");
    System.Text.Json.JsonSerializer.Serialize(job.Result).Should().Contain("cancelled",
      "a cancelled job must report as cancelled, not as a crash");
  }

  [Test]
  [Category("EdgeCase")]
  public void Cancel_GivenAnAlreadyFinishedOrUnknownJob_ThenItIsHandledWithoutThrowing() {
    var registry = new JobRegistry();
    var job = registry.Start("quick", "pool-1", (_, _) => new { ok = true });
    _WaitUntil(() => job.IsFinished).Should().BeTrue();

    registry.Cancel(job.Id).Should().Be(JobRegistry.CancelResult.AlreadyFinished, "there is nothing left to stop");
    registry.Cancel("no-such-job").Should().Be(JobRegistry.CancelResult.Unknown);
    registry.Find("no-such-job").Should().BeNull();
  }

  [Test]
  [Category("EdgeCase")]
  public void Cancel_GivenWorkThatCannotBeStopped_ThenItSaysSoInsteadOfPretending() {
    // a system installer part-way through, or a purge already erasing members, cannot be
    // meaningfully un-begun. A Cancel button that silently does nothing is worse than none, so
    // the registry reports the difference and the UI hides the button.
    var registry = new JobRegistry();
    using var release = new ManualResetEventSlim(false);

    var job = registry.Start("install-prereq", "", (_, _) => {
      release.Wait(TimeSpan.FromSeconds(30));
      return new { ok = true };
    }, cancellable: false);

    job.Cancellable.Should().BeFalse();
    registry.Cancel(job.Id).Should().Be(JobRegistry.CancelResult.NotCancellable);
    job.IsCancelling.Should().BeFalse("an unstoppable job must not be left displaying 'cancelling' forever");

    release.Set();
    _WaitUntil(() => job.IsFinished).Should().BeTrue();
  }

  [Test]
  [Category("Exception")]
  public void Start_GivenWorkThatThrows_ThenTheFailureBecomesTheResultRatherThanKillingTheDaemon() {
    var registry = new JobRegistry();
    var job = registry.Start("boom", "pool-1", (_, _) => throw new InvalidOperationException("the member went away"));

    _WaitUntil(() => job.IsFinished).Should().BeTrue();
    System.Text.Json.JsonSerializer.Serialize(job.Result).Should().Contain("the member went away",
      "the error must reach the UI as a result, not take down the management thread");
  }

  [Test]
  [Category("EdgeCase")]
  public void Prune_GivenFinishedJobsOlderThanTheRetention_ThenTheyAreForgottenAndRunningOnesAreKept() {
    var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    var registry = new JobRegistry(() => now);
    using var release = new ManualResetEventSlim(false);

    var finished = registry.Start("done", "pool-1", (_, _) => new { ok = true });
    _WaitUntil(() => finished.IsFinished).Should().BeTrue();

    var running = registry.Start("ongoing", "pool-1", (_, _) => {
      release.Wait(TimeSpan.FromSeconds(30));
      return new { ok = true };
    });

    now += JobRegistry.Retention + TimeSpan.FromMinutes(1);
    registry.Prune();

    registry.Find(finished.Id).Should().BeNull("a result nobody can still be waiting for must not be retained forever");
    registry.Find(running.Id).Should().NotBeNull("a job still running must never be pruned out from under its caller");

    release.Set();
    _WaitUntil(() => running.IsFinished).Should().BeTrue();
  }

  [Test]
  [Category("EdgeCase")]
  public void Registry_GivenManyJobsStartedConcurrently_ThenEveryTicketIsDistinctAndAllComplete() {
    var registry = new JobRegistry();
    const int count = 64;

    var jobs = new JobRegistry.Job[count];
    var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
      jobs[i] = registry.Start($"job{i}", "pool-1", (_, _) => new { ok = true, index = i })) { IsBackground = true }).ToArray();

    foreach (var thread in threads)
      thread.Start();
    foreach (var thread in threads)
      thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("starting a job must never block");

    jobs.Select(job => job.Id).Distinct().Should().HaveCount(count, "two operations must never share a ticket");
    _WaitUntil(() => jobs.All(job => job.IsFinished)).Should().BeTrue();
  }

  private static bool _WaitUntil(Func<bool> condition) {
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (DateTime.UtcNow < deadline) {
      if (condition())
        return true;

      Thread.Sleep(5);
    }

    return false;
  }

}
