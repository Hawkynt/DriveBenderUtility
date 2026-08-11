using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Thread-pool isolation for blocking members (NFR-UI-LIVE). Every cloud provider is driven
/// sync-over-async — the driver callbacks are synchronous, so the engine cannot be async end to
/// end — which means each in-flight remote read parks a thread for its whole round trip. The
/// point of <see cref="BlockingIoScheduler"/> is that those parked threads are the engine's own
/// and finite, never the shared pool's: the pool grows by roughly one thread per second once
/// saturated, so a burst of cloud reads would otherwise stall everything else in the process for
/// seconds — the background scheduler, other pools, and the management daemon.
/// </summary>
[TestFixture]
[Category("Unit")]
public class BlockingIoSchedulerTests {

  [Test]
  [Category("HappyPath")]
  public void Scheduler_GivenNoBlockingWork_ThenItOwnsNoThreads() {
    using var scheduler = new BlockingIoScheduler(8);
    scheduler.LiveThreads.Should().Be(0, "a pool with no remote member must not pay for threads it never uses");
  }

  [Test]
  [Category("HappyPath")]
  public void Scheduler_GivenMoreBlockingWorkThanItsCap_ThenConcurrencyStopsAtTheCapAndAllWorkCompletes() {
    const int cap = 3;
    const int items = 24;
    using var scheduler = new BlockingIoScheduler(cap);

    var concurrent = 0;
    var peak = 0;
    var completed = 0;
    using var release = new ManualResetEventSlim(false);

    var tasks = Enumerable.Range(0, items).Select(_ => Task.Factory.StartNew(() => {
      var now = Interlocked.Increment(ref concurrent);
      for (var observed = Volatile.Read(ref peak); now > observed; observed = Volatile.Read(ref peak))
        Interlocked.CompareExchange(ref peak, now, observed);

      release.Wait(TimeSpan.FromSeconds(10)); // models a remote round trip: the thread is PARKED
      Interlocked.Decrement(ref concurrent);
      Interlocked.Increment(ref completed);
    }, CancellationToken.None, TaskCreationOptions.None, scheduler)).ToArray();

    // let the cap fill, then prove it did not grow past it however much work is queued
    var spin = new SpinWait();
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (Volatile.Read(ref concurrent) < cap && DateTime.UtcNow < deadline)
      spin.SpinOnce();

    Volatile.Read(ref peak).Should().Be(cap, "a stalled remote must never grow past the scheduler's cap");
    scheduler.LiveThreads.Should().BeLessThanOrEqualTo(cap);

    release.Set();
    Task.WaitAll(tasks, TimeSpan.FromSeconds(30)).Should().BeTrue("every queued item must still complete — the cap throttles, it does not drop");
    completed.Should().Be(items);
  }

  [Test]
  [Category("EdgeCase")]
  public void Scheduler_GivenBlockingWork_ThenTheSharedThreadPoolIsLeftAlone() {
    // the property that matters: parking N remote calls must not consume N thread-pool threads
    const int blocking = 12;
    using var scheduler = new BlockingIoScheduler(4);
    using var release = new ManualResetEventSlim(false);

    ThreadPool.GetAvailableThreads(out var availableBefore, out _);

    var parked = 0;
    var tasks = Enumerable.Range(0, blocking).Select(_ => Task.Factory.StartNew(() => {
      Interlocked.Increment(ref parked);
      release.Wait(TimeSpan.FromSeconds(10));
    }, CancellationToken.None, TaskCreationOptions.None, scheduler)).ToArray();

    var spin = new SpinWait();
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (Volatile.Read(ref parked) < 4 && DateTime.UtcNow < deadline)
      spin.SpinOnce();

    ThreadPool.GetAvailableThreads(out var availableDuring, out _);
    release.Set();
    Task.WaitAll(tasks, TimeSpan.FromSeconds(30)).Should().BeTrue();

    // a couple of threads of noise is normal on a shared runner; consuming one per blocked call
    // is what this must never do
    (availableBefore - availableDuring).Should().BeLessThan(4,
      "blocking remote I/O consumed shared thread-pool threads — it must run on the engine's own threads");
  }

  [Test]
  [Category("EdgeCase")]
  public void Scheduler_GivenWorkThatThrows_ThenTheWorkerSurvivesAndKeepsServing() {
    using var scheduler = new BlockingIoScheduler(2);

    var failing = Task.Factory.StartNew(() => throw new InvalidOperationException("remote blew up"),
      CancellationToken.None, TaskCreationOptions.None, scheduler);
    var waiting = () => failing.Wait(TimeSpan.FromSeconds(10));
    waiting.Should().Throw<AggregateException>("the failure must surface to the caller, not be swallowed");

    var after = Task.Factory.StartNew(() => 42, CancellationToken.None, TaskCreationOptions.None, scheduler);
    after.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("a worker must not die with the task it ran");
    after.Result.Should().Be(42);
  }

}
