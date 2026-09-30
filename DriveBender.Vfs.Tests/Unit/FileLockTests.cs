using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The per-file reader/writer lock every path lease rests on (FR-CONCURRENCY). It replaced a
/// <see cref="ReaderWriterLockSlim"/> whose internal spin lock serialised readers of one file, so it
/// is held to that class's contract: shared readers, one writer, writers preferred, no recursion,
/// released only by the thread that took it — and it must never lose a wake-up.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FileLockTests {

  private static readonly TimeSpan _long = TimeSpan.FromSeconds(10);

  /// <summary>Runs <paramref name="body"/> on its own thread and waits for it.</summary>
  private static T _OnOtherThread<T>(Func<T> body) {
    T result = default!;
    Exception? failure = null;
    var thread = new Thread(() => {
      try {
        result = body();
      } catch (Exception e) {
        failure = e;
      }
    }) { IsBackground = true };
    thread.Start();
    thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the other thread must finish");
    if (failure != null)
      throw failure;

    return result;
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenAnotherReaderInside_ThenBothHoldItAtOnce() {
    var fileLock = new FileLock();
    fileLock.TryEnterReadLock(_long).Should().BeTrue();

    _OnOtherThread(() => {
      var entered = fileLock.TryEnterReadLock(TimeSpan.Zero);
      if (entered)
        fileLock.ExitReadLock();
      return entered;
    }).Should().BeTrue("readers never exclude each other");

    fileLock.ExitReadLock();
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenAReaderInside_ThenAZeroTimeoutWriterIsRefusedAndGetsInOnceItLeaves() {
    var fileLock = new FileLock();
    fileLock.TryEnterReadLock(_long).Should().BeTrue();

    _OnOtherThread(() => fileLock.TryEnterWriteLock(TimeSpan.Zero)).Should().BeFalse("a reader is inside");
    fileLock.ExitReadLock();

    _OnOtherThread(() => {
      var entered = fileLock.TryEnterWriteLock(TimeSpan.Zero);
      if (entered)
        fileLock.ExitWriteLock();
      return entered;
    }).Should().BeTrue("the lock is free again");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenAWriterIsWaiting_ThenANewReaderIsHeldBackUntilTheWriterHasBeenIn() {
    // writer preference: without it a steady stream of readers starves the flush, the drain and
    // every rename of a busy file
    var fileLock = new FileLock();
    fileLock.TryEnterReadLock(_long).Should().BeTrue();

    using var writerIn = new ManualResetEventSlim();
    using var writerMayLeave = new ManualResetEventSlim();
    var writerEntered = false;
    var writer = new Thread(() => {
      // no assertion here: one failing on a bare thread takes the whole test host down with it
      writerEntered = fileLock.TryEnterWriteLock(_long);
      writerIn.Set();
      if (!writerEntered)
        return;

      writerMayLeave.Wait();
      fileLock.ExitWriteLock();
    }) { IsBackground = true };
    writer.Start();

    // queued for real, not "probably by now": a sleep was a bet on the scheduler, and a slow runner
    // lost it, let the reader in ahead of a writer that had not started, and never saw it leave
    SpinWait.SpinUntil(() => fileLock.HasWaitingWriter, _long).Should().BeTrue("the writer queues behind our read lock");

    _OnOtherThread(() => {
      var entered = fileLock.TryEnterReadLock(TimeSpan.FromMilliseconds(100));
      if (entered)
        fileLock.ExitReadLock();
      return entered;
    }).Should().BeFalse("a new reader must not overtake a waiting writer");

    fileLock.ExitReadLock();
    writerIn.Wait(_long).Should().BeTrue("the writer gets in once the last reader leaves");
    writerEntered.Should().BeTrue("the writer gets in once the last reader leaves");
    writerMayLeave.Set();
    writer.Join(_long).Should().BeTrue();

    _OnOtherThread(() => {
      var entered = fileLock.TryEnterReadLock(TimeSpan.Zero);
      if (entered)
        fileLock.ExitReadLock();
      return entered;
    }).Should().BeTrue();
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenTheWriterItWaitedBehindGivesUp_ThenTheReaderIsLetIn() {
    // the writer's give-up must wake the readers it was holding back, or they sleep until their own
    // timeout even though nothing excludes them any more
    var fileLock = new FileLock();
    fileLock.TryEnterReadLock(_long).Should().BeTrue();

    var writer = Task.Run(() => _OnOtherThread(() => fileLock.TryEnterWriteLock(TimeSpan.FromMilliseconds(300))));
    SpinWait.SpinUntil(() => fileLock.HasWaitingWriter, _long).Should().BeTrue("the writer queues behind our read lock");
    var reader = Task.Run(() => _OnOtherThread(() => {
      var clock = System.Diagnostics.Stopwatch.StartNew();
      var entered = fileLock.TryEnterReadLock(TimeSpan.FromSeconds(20));
      if (entered)
        fileLock.ExitReadLock();
      return (entered, clock.Elapsed);
    }));

    writer.Result.Should().BeFalse("our read lock is still held, so the writer times out");
    var (readerEntered, waited) = reader.Result;
    readerEntered.Should().BeTrue();
    waited.Should().BeLessThan(TimeSpan.FromSeconds(10), "the reader is woken when the writer gives up, not left to its own timeout");
    fileLock.ExitReadLock();
  }

  [Test]
  [Category("Exception")]
  public void Enter_GivenThisThreadAlreadyHoldsIt_ThenRecursionIsRefused() {
    var fileLock = new FileLock();
    fileLock.TryEnterReadLock(_long).Should().BeTrue();

    fileLock.Invoking(l => l.TryEnterReadLock(_long)).Should().Throw<LockRecursionException>();
    fileLock.Invoking(l => l.TryEnterWriteLock(_long)).Should().Throw<LockRecursionException>("a reader upgrading would deadlock against itself");
    fileLock.ExitReadLock();

    fileLock.TryEnterWriteLock(_long).Should().BeTrue();
    fileLock.Invoking(l => l.TryEnterReadLock(_long)).Should().Throw<LockRecursionException>();
    fileLock.ExitWriteLock();
  }

  [Test]
  [Category("Exception")]
  public void Exit_GivenTheLockIsNotHeldByThisThread_ThenItThrowsAndTheHolderKeepsIt() {
    var fileLock = new FileLock();
    fileLock.Invoking(l => l.ExitReadLock()).Should().Throw<SynchronizationLockException>();
    fileLock.Invoking(l => l.ExitWriteLock()).Should().Throw<SynchronizationLockException>();

    fileLock.TryEnterWriteLock(_long).Should().BeTrue();
    _OnOtherThread(() => {
      fileLock.Invoking(l => l.ExitWriteLock()).Should().Throw<SynchronizationLockException>("only the holder may release it");
      return fileLock.TryEnterReadLock(TimeSpan.Zero);
    }).Should().BeFalse("the failed release by a stranger must leave the writer holding it");

    fileLock.ExitWriteLock();
  }

  [Test]
  [Category("EdgeCase")]
  public void Lock_GivenReadersAndWritersWithAndWithoutTimeouts_ThenExclusionHoldsAndNoWakeUpIsLost() {
    var fileLock = new FileLock();
    var readers = 0;
    var writers = 0;
    var violations = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var acquired = new long[12];

    var threads = Enumerable.Range(0, 12).Select(worker => new Thread(() => {
      var random = new Random(worker);
      for (var round = 0; round < 4000; ++round) {
        var write = worker % 4 == 0;
        var timeout = random.Next(4) == 0 ? TimeSpan.Zero : Timeout.InfiniteTimeSpan;
        if (write) {
          if (!fileLock.TryEnterWriteLock(timeout))
            continue;

          if (Interlocked.Increment(ref writers) != 1 || Volatile.Read(ref readers) != 0)
            violations.Enqueue("a writer shared the lock");
          if (round % 64 == 0)
            Thread.Yield();
          Interlocked.Decrement(ref writers);
          fileLock.ExitWriteLock();
        } else {
          if (!fileLock.TryEnterReadLock(timeout))
            continue;

          Interlocked.Increment(ref readers);
          if (Volatile.Read(ref writers) != 0)
            violations.Enqueue("a reader shared the lock with a writer");
          Interlocked.Decrement(ref readers);
          fileLock.ExitReadLock();
        }

        ++acquired[worker];
      }
    }) { IsBackground = true, Name = $"filelock-{worker}" }).ToArray();

    foreach (var thread in threads)
      thread.Start();
    foreach (var thread in threads)
      thread.Join(TimeSpan.FromMinutes(1)).Should().BeTrue("a lost wake-up leaves a waiter asleep for good");

    violations.Should().BeEmpty();
    acquired.Should().OnlyContain(count => count > 0, "every worker, reader or writer, must get in");
    fileLock.TryEnterWriteLock(TimeSpan.Zero).Should().BeTrue("with everyone gone the lock is free");
    fileLock.ExitWriteLock();
  }

}
