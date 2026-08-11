using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The one-state-per-path invariant (FR-CONCURRENCY).
///
/// Every lock in the engine is per file, and "per file" means per PATH: readers, writers,
/// the owed-copy flush, the drainer and heal all serialise by taking the lock on the state the
/// handle table maps that path to. The whole scheme is worth exactly as much as the guarantee
/// that there is only ever ONE such state per path — with two, both sides believe they hold
/// "the file's lock" while excluding nobody, and the result is a file read back with one block
/// from each writer.
///
/// A replacing rename is what can break it: it repoints a path at the state that moved onto it,
/// leaving the displaced state still pinned by its own open handles.
/// </summary>
[TestFixture]
[Category("Unit")]
public class HandleTableTests {

  [Test]
  [Category("EdgeCase")]
  public void ReplacingRename_WhenTheDisplacedStateIsClosed_ThenThePathKeepsTheStateThatOwnsIt() {
    var table = new HandleTable();

    // a handle is open on a.bin, then a.bin is renamed away — the open handle follows it
    var original = table.Open("a.bin", AccessMode.ReadWrite);
    table.RenamePath("a.bin", "b.bin");
    original.File.Path.Should().Be("b.bin", "an open handle follows its file across a rename");

    // meanwhile something else opens the now-free name, creating a SECOND state
    var displaced = table.Open("a.bin", AccessMode.ReadWrite);
    displaced.File.Should().NotBeSameAs(original.File);

    // the rename comes back the other way and replaces it: a.bin now means the original state
    table.RenamePath("b.bin", "a.bin");
    original.File.Path.Should().Be("a.bin");

    // Closing the DISPLACED handle must not unkey the path — its state's Path names an entry that
    // now belongs to somebody else. Removing it blindly let the next caller create a THIRD state
    // for a.bin while `original` was still open on it, and the two would lock different objects.
    table.Close(displaced.Handle);

    using var lease = table.AcquireWrite("a.bin");
    lease.File.Should().BeSameAs(original.File,
      "a lease on the path must lock the very state its open handles use — otherwise the lock excludes nothing");

    table.Close(original.Handle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Lease_GivenAPathWithAnOpenHandle_ThenBothResolveToTheSameState() {
    var table = new HandleTable();
    var open = table.Open("f.bin", AccessMode.ReadWrite);

    using (var lease = table.AcquireRead("f.bin"))
      lease.File.Should().BeSameAs(open.File, "handles and leases must pin the same state, or background work serialises against nothing");

    table.Close(open.Handle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Lease_GivenItIsHeldExclusively_ThenAZeroTimeoutTryReturnsNullInsteadOfWaiting() {
    // the drainer and heal use a zero-timeout try so a busy file is SKIPPED rather than stalling
    // the background pump — a try that never succeeds would stall convergence just as badly
    var table = new HandleTable();
    HandleTable.PathLease? fromOtherThread = null;

    using (var held = table.AcquireWrite("busy.bin")) {
      // from ANOTHER thread: the lock is not recursive, so the holder asking again is a bug in the
      // caller, not a case to be served
      var other = new Thread(() => fromOtherThread = table.TryAcquireWrite("busy.bin", TimeSpan.Zero)) { IsBackground = true };
      other.Start();
      other.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("a zero-timeout try must return at once, never block");
    }

    fromOtherThread.Should().BeNull("a file someone else owns must be skipped, not waited on");

    using var afterRelease = table.TryAcquireWrite("busy.bin", TimeSpan.Zero);
    afterRelease.Should().NotBeNull("once free, the zero-timeout try must still succeed");
  }

  [Test]
  [Category("EdgeCase")]
  public void IsOpen_GivenOnlyALease_ThenThePathDoesNotCountAsOpen() {
    // background jobs take a lease on the path they are about to touch and then check IsOpen to
    // see whether a FOREGROUND handle is using it; counting their own lease would make every job
    // skip its own work
    var table = new HandleTable();

    using (var lease = table.AcquireWrite("bg.bin"))
      table.IsOpen("bg.bin").Should().BeFalse("a lease is not a handle");

    var open = table.Open("bg.bin", AccessMode.Read);
    table.IsOpen("bg.bin").Should().BeTrue();
    table.Close(open.Handle);
    table.IsOpen("bg.bin").Should().BeFalse();
  }

  [Test]
  [Category("EdgeCase")]
  public void Table_GivenRenameChurn_ThenAnExclusiveLeaseOnAPathStaysExclusive() {
    // The invariant that matters is not object identity — a displaced state legitimately lingers
    // while its own handles are open — but MUTUAL EXCLUSION: however the renames interleave, two
    // threads must never both believe they hold the exclusive lease on one path. Two states for
    // one path is exactly how that happened, and it read back as a file torn between two writers.
    var table = new HandleTable();
    var violations = new System.Collections.Concurrent.ConcurrentBag<string>();
    var inside = 0;
    const int rounds = 500;

    var workers = Enumerable.Range(0, 6).Select(worker => new Thread(() => {
      for (var round = 0; round < rounds; ++round)
        switch (worker % 3) {
          case 0: {
            // an open handle on the path is what keeps a displaced state alive
            var open = table.Open("shared.bin", AccessMode.ReadWrite);
            Thread.Yield();
            table.Close(open.Handle);
            break;
          }

          case 1: {
            using var lease = table.AcquireWrite("shared.bin");
            if (Interlocked.Increment(ref inside) != 1)
              violations.Add("two threads held the exclusive lease on 'shared.bin' at once");

            Thread.Yield();
            Interlocked.Decrement(ref inside);
            break;
          }

          default: {
            // renames hold an EXCLUSIVE lease on both endpoints for the whole flip, ordered so two
            // opposing renames cannot deadlock — RenamePath is only ever called under them, and
            // that is what makes the repointing safe
            _Rename(table, "shared.bin", "moved.bin");
            Thread.Yield();
            _Rename(table, "moved.bin", "shared.bin");
            break;
          }
        }
    }) { IsBackground = true, Name = $"handles-{worker}" }).ToArray();

    foreach (var thread in workers)
      thread.Start();
    foreach (var thread in workers)
      thread.Join(TimeSpan.FromMinutes(1)).Should().BeTrue("handle-table operations must never deadlock");

    violations.Should().BeEmpty();
  }

  /// <summary>Renames the way the engine does: both endpoints leased exclusively, in a fixed order.</summary>
  private static void _Rename(HandleTable table, string from, string to) {
    var first = string.CompareOrdinal(from, to) <= 0 ? from : to;
    var second = first == from ? to : from;
    using var leaseFirst = table.AcquireWrite(first);
    using var leaseSecond = table.AcquireWrite(second);
    table.RenamePath(from, to);
  }

}
