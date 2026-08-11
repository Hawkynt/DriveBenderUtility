namespace Hawkynt.CloudStorage;

/// <summary>
/// The one place an asynchronous SDK call is turned into a synchronous one.
///
/// This library is driven by a filesystem engine whose own callers — WinFsp, Dokan and FUSE —
/// are synchronous callbacks. There is no async path available to propagate, so somewhere the
/// wait has to happen, and doing it in one audited place is better than doing it ad hoc in
/// thirteen providers.
///
/// The hazard being closed here is the classic one: <c>task.GetAwaiter().GetResult()</c> blocks
/// the current thread, and if that thread carries a single-threaded
/// <see cref="SynchronizationContext"/>, the SDK's continuation is posted BACK to it and can
/// never run — the call deadlocks outright rather than merely being slow. The engine normally
/// runs without a context, but "normally" is not a guarantee a storage library should rely on:
/// it is also reachable from the desktop shell and from any host that installs one. So a
/// captured context is escaped before blocking, and the exception is unwrapped so callers see
/// the provider's own <see cref="CloudStorageException"/> instead of an
/// <see cref="AggregateException"/> wrapping it.
/// </summary>
public static class SyncBridge {

  /// <summary>Runs an async operation to completion from a synchronous caller.</summary>
  public static T Run<T>(Func<Task<T>> operation) {
    // No context to capture: block directly. ConfigureAwait(false) is not enough on its own —
    // it governs THIS await, not the continuations inside the SDK.
    if (SynchronizationContext.Current == null)
      return _Unwrap(() => operation().GetAwaiter().GetResult());

    // A context is installed: start the work on a pool thread, which has none, so no
    // continuation can be posted back to the thread that is about to block on it
    return _Unwrap(() => Task.Run(operation).GetAwaiter().GetResult());
  }

  /// <summary>Runs an async operation with no result to completion from a synchronous caller.</summary>
  public static void Run(Func<Task> operation) {
    if (SynchronizationContext.Current == null)
      _Unwrap(() => {
        operation().GetAwaiter().GetResult();
        return true;
      });
    else
      _Unwrap(() => {
        Task.Run(operation).GetAwaiter().GetResult();
        return true;
      });
  }

  /// <summary>
  /// Surfaces the provider's own failure rather than an AggregateException wrapping it —
  /// otherwise every <c>catch (AmazonS3Exception)</c> style filter in the providers stops
  /// matching the moment the call is routed through a Task.
  /// </summary>
  private static T _Unwrap<T>(Func<T> blocking) {
    try {
      return blocking();
    } catch (AggregateException e) when (e.InnerExceptions.Count == 1) {
      System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
      throw; // unreachable — Throw() rethrows preserving the original stack
    }
  }

}
