namespace DivisonM.Vfs.Engine;

/// <summary>
/// The per-file reader/writer lock behind every path lease: many readers or one writer, writers
/// preferred, not recursive, released by the thread that took it — the contract the engine had
/// from <see cref="ReaderWriterLockSlim"/> with <see cref="LockRecursionPolicy.NoRecursion"/>, and
/// keeps, down to the exceptions a misuse gets.
///
/// Why not that class: it guards its own state with an internal spin lock, taken on EVERY enter
/// and exit, so readers of one file serialise on it even though none of them excludes another.
/// Measured with twenty threads of cached reads on one file: once the engine's global locks were
/// gone, entering and leaving that spin lock was ~60% of the read path. Here an uncontended read
/// enter or exit is one atomic add on the state, and only a thread that has to WAIT touches a monitor.
///
/// STATE: <see cref="_state"/> is the number of readers inside, or <see cref="_WRITER"/> while a
/// writer holds it. A reader may enter only while no writer holds it AND none is waiting — so a
/// stream of readers cannot starve a flush, a drain or a rename. Wake-ups cannot be lost: a waiter
/// announces itself (<see cref="_waiters"/>) and re-checks the state under <see cref="_gate"/>
/// before it sleeps, and a releaser changes the state and then, seeing any waiter, pulses under
/// that same gate. Both sides use full fences, so at least one of them sees the other.
/// </summary>
/// <param name="clock">Milliseconds since some fixed point, for the timeouts; <see cref="Environment.TickCount64"/> unless a test drives it.</param>
public sealed class FileLock(Func<long>? clock = null) {

  private readonly Func<long> _now = clock ?? (static () => Environment.TickCount64);

  private const int _WRITER = -1;

  private readonly object _gate = new();
  private int _state;
  private int _waitingWriters;
  private int _waiters;

  /// <summary>The locks this thread holds, for the recursion and ownership checks. A thread holds a handful at most.</summary>
  [ThreadStatic]
  private static List<FileLock>? _held;

  /// <summary>Whether a writer is queued for the lock, holding new readers back. For diagnostics and tests; it can change the moment it is read.</summary>
  public bool HasWaitingWriter => Volatile.Read(ref this._waitingWriters) > 0;

  public bool TryEnterReadLock(TimeSpan timeout) {
    this._RefuseRecursion();
    if (!this._TryEnterReadFast() && !this._Wait(write: false, timeout))
      return false;

    (_held ??= []).Add(this);
    return true;
  }

  public bool TryEnterWriteLock(TimeSpan timeout) {
    this._RefuseRecursion();
    if (Interlocked.CompareExchange(ref this._state, _WRITER, 0) != 0 && !this._Wait(write: true, timeout))
      return false;

    (_held ??= []).Add(this);
    return true;
  }

  public void ExitReadLock() {
    if (this._state <= 0)
      throw new SynchronizationLockException("The read lock is being released without being held.");

    this._Release();
    if (Interlocked.Decrement(ref this._state) == 0 && Volatile.Read(ref this._waiters) > 0)
      this._WakeAll();
  }

  public void ExitWriteLock() {
    if (this._state != _WRITER)
      throw new SynchronizationLockException("The write lock is being released without being held.");

    this._Release();
    Interlocked.Exchange(ref this._state, 0);
    if (Volatile.Read(ref this._waiters) > 0)
      this._WakeAll();
  }

  private bool _TryEnterReadFast() {
    for (var state = Volatile.Read(ref this._state); state >= 0 && Volatile.Read(ref this._waitingWriters) == 0; state = Volatile.Read(ref this._state))
      if (Interlocked.CompareExchange(ref this._state, state + 1, state) == state)
        return true;

    return false;
  }

  private bool _Wait(bool write, TimeSpan timeout) {
    var deadline = timeout == Timeout.InfiniteTimeSpan ? long.MaxValue : this._now() + (long)Math.Max(0, timeout.TotalMilliseconds);
    lock (this._gate) {
      Interlocked.Increment(ref this._waiters);
      if (write)
        Interlocked.Increment(ref this._waitingWriters);

      try {
        while (true) {
          if (write ? Interlocked.CompareExchange(ref this._state, _WRITER, 0) == 0 : this._TryEnterReadFast())
            return true;

          // an infinite wait is decided by the deadline, never by the number: a remaining of exactly
          // -1 IS Timeout.Infinite, so a wait that overran its deadline by one millisecond slept for ever
          if (deadline == long.MaxValue) {
            Monitor.Wait(this._gate, Timeout.Infinite);
            continue;
          }

          var remaining = deadline - this._now();
          if (remaining <= 0)
            return false;

          Monitor.Wait(this._gate, (int)Math.Min(remaining, int.MaxValue));
        }
      } finally {
        Interlocked.Decrement(ref this._waiters);
        if (write && Interlocked.Decrement(ref this._waitingWriters) == 0)
          Monitor.PulseAll(this._gate); // readers held back for this writer may go now — whether it got in or gave up
      }
    }
  }

  private void _WakeAll() {
    lock (this._gate)
      Monitor.PulseAll(this._gate);
  }

  private bool _HeldByCurrentThread() => _held is { } held && held.Contains(this);

  /// <summary>As <see cref="LockRecursionPolicy.NoRecursion"/>: asking again for a lock this thread holds is a bug, and says so.</summary>
  private void _RefuseRecursion() {
    if (this._HeldByCurrentThread())
      throw new LockRecursionException("This thread already holds this file's lock; the lock is not recursive.");
  }

  private void _Release() {
    if (_held == null || !_held.Remove(this))
      throw new SynchronizationLockException("The lock is being released by a thread that does not hold it.");
  }

}
