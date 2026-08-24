namespace DivisonM.Vfs.Engine;

/// <summary>
/// One step of a long pool operation — a scrub, a duplication restore, a health pass.
///
/// These run for as long as the DATA takes, minutes to hours, and until now the only thing the UI
/// could show was the worker's most recent line of text. A line of text cannot be turned into a bar,
/// cannot say whether the run is a tenth or nine tenths through, and cannot distinguish "still
/// working" from "stuck on one enormous file". <see cref="Completed"/> out of <see cref="Total"/>
/// can.
/// </summary>
/// <param name="Completed">Items finished so far.</param>
/// <param name="Total">Items in the whole pass, or 0 when the operation genuinely cannot know yet —
/// reported honestly rather than guessed, because a total that moves is worse than no total.</param>
/// <param name="Item">What is being worked on right now; a pool-relative path, or a short phase name.</param>
public readonly record struct OperationStep(long Completed, long Total, string Item) {

  /// <summary>A step with no countable items behind it — a phase announcement.</summary>
  public static OperationStep Phase(string what) => new(0, 0, what);

  public double? Fraction => this.Total > 0 ? Math.Clamp((double)this.Completed / this.Total, 0, 1) : null;

  public override string ToString()
    => this.Total > 0
      ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{this.Completed:N0} of {this.Total:N0} — {this.Item}")
      : this.Item;
}

/// <summary>
/// How a long operation reports and is stopped. Both are optional so the engine's own callers —
/// the mount's background jobs, the tests — need not care, and both are threaded through the same
/// parameter so a caller that wants one is reminded the other exists.
/// </summary>
public sealed record OperationContext(CancellationToken Cancellation = default, Action<OperationStep>? Report = null) {

  public static readonly OperationContext None = new();

  public void Step(long completed, long total, string item) => this.Report?.Invoke(new(completed, total, item));

  public void Phase(string what) => this.Report?.Invoke(OperationStep.Phase(what));

  /// <summary>
  /// Throws <see cref="OperationCanceledException"/> when the caller has asked to stop.
  ///
  /// Called BETWEEN items, never inside one: a scrub that abandoned a file half-copied would
  /// leave exactly the torn state the operation exists to repair. Cancelling a pool operation
  /// means "stop starting new work", not "drop what is in your hands".
  /// </summary>
  public void ThrowIfStopping() => this.Cancellation.ThrowIfCancellationRequested();
}
