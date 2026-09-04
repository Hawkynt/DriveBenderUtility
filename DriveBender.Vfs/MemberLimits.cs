using System.Text.Json.Serialization;

namespace DivisonM.Vfs;

/// <summary>
/// What a member is being asked to do, for the purpose of rate limiting.
///
/// The three are separated because they compete for a disk on very different terms and an operator
/// wants to trade them against each other. Reads are what an application is waiting on; writes are
/// what it is waiting on to be safe; BACKGROUND is the pool's own housekeeping — a landing-zone
/// drain, a duplication heal, a media exchange — which moves far more data than either and which
/// nobody is waiting for. Being able to hold the last one down while leaving the first two alone is
/// most of what "leave some disk for everything else" means in practice.
/// </summary>
public enum IoKind {
  Read,
  Write,

  /// <summary>The pool's own whole-file work: drain, heal, exchange, scatter, quarantine.</summary>
  Background,
}

/// <summary>
/// Everything a member may be held to, per kind of operation (§6.4).
///
/// A zero anywhere means "no limit for that", which is the default and is not the same as a limit of
/// zero. Three shapes are expressible, and the UI offers exactly those:
///
/// <list type="bullet">
///   <item>NONE — every field zero.</item>
///   <item>SIMPLE — one byte rate for everything, including the pool's own background copies. That
///     is what <see cref="MaxThroughput"/> means, and it is the shape the manifest has always had.</item>
///   <item>ADVANCED — a separate rate for reads, writes and background work.</item>
/// </list>
///
/// The timeouts bound how long the LIMITER may delay an operation, and never fail one. A limit is a
/// target and a timeout is the promise that honouring it costs no more than this, so a limit set too
/// low can slow a pool down but can never wedge it: past the timeout the operation proceeds having
/// paid what it could. That deliberately does not cancel device I/O already in flight, which the
/// engine cannot do for a synchronous handle — it bounds the queueing, not the disk.
/// </summary>
public sealed record MemberLimits {

  /// <summary>Operations per second, whatever their kind; 0 for unlimited. What a seek-bound disk runs out of.</summary>
  [JsonPropertyName("maxIops")] public int MaxIops { get; init; }

  /// <summary>Bytes per second for ALL operations — the simple shape; 0 for unlimited.</summary>
  [JsonPropertyName("maxThroughput")]
  [JsonConverter(typeof(ByteSizeJsonConverter))]
  public long MaxThroughput { get; init; }

  /// <summary>Bytes per second for reads; 0 falls back to <see cref="MaxThroughput"/>.</summary>
  [JsonPropertyName("readThroughput")]
  [JsonConverter(typeof(ByteSizeJsonConverter))]
  public long ReadThroughput { get; init; }

  /// <summary>Bytes per second for writes; 0 falls back to <see cref="MaxThroughput"/>.</summary>
  [JsonPropertyName("writeThroughput")]
  [JsonConverter(typeof(ByteSizeJsonConverter))]
  public long WriteThroughput { get; init; }

  /// <summary>Bytes per second for the pool's own drain/heal/exchange; 0 falls back to <see cref="MaxThroughput"/>.</summary>
  [JsonPropertyName("backgroundThroughput")]
  [JsonConverter(typeof(ByteSizeJsonConverter))]
  public long BackgroundThroughput { get; init; }

  /// <summary>Milliseconds the limiter may delay ANY operation; 0 for no cap of its own.</summary>
  [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; init; }

  /// <summary>Milliseconds the limiter may delay a read; 0 falls back to <see cref="TimeoutMs"/>.</summary>
  [JsonPropertyName("readTimeoutMs")] public int ReadTimeoutMs { get; init; }

  /// <summary>Milliseconds the limiter may delay a write; 0 falls back to <see cref="TimeoutMs"/>.</summary>
  [JsonPropertyName("writeTimeoutMs")] public int WriteTimeoutMs { get; init; }

  /// <summary>Milliseconds the limiter may delay background work; 0 falls back to <see cref="TimeoutMs"/>.</summary>
  [JsonPropertyName("backgroundTimeoutMs")] public int BackgroundTimeoutMs { get; init; }

  public static readonly MemberLimits None = new();

  /// <summary>True when this member is held to anything at all; the common case is that it is not.</summary>
  [JsonIgnore]
  public bool Any => this.MaxIops > 0 || this.MaxThroughput > 0
                     || this.ReadThroughput > 0 || this.WriteThroughput > 0 || this.BackgroundThroughput > 0;

  /// <summary>The byte rate that applies to one kind of operation, after the fallbacks.</summary>
  public long ThroughputFor(IoKind kind) {
    var specific = kind switch {
      IoKind.Read => this.ReadThroughput,
      IoKind.Write => this.WriteThroughput,
      _ => this.BackgroundThroughput,
    };

    return specific > 0 ? specific : this.MaxThroughput;
  }

  /// <summary>How long the limiter may delay one kind of operation, or null when it may delay it indefinitely.</summary>
  public TimeSpan? DelayCapFor(IoKind kind) {
    var specific = kind switch {
      IoKind.Read => this.ReadTimeoutMs,
      IoKind.Write => this.WriteTimeoutMs,
      _ => this.BackgroundTimeoutMs,
    };

    var effective = specific > 0 ? specific : this.TimeoutMs;
    return effective > 0 ? TimeSpan.FromMilliseconds(effective) : null;
  }

  /// <summary>The shape the manifest has always carried: one rate for everything.</summary>
  public static MemberLimits Simple(int maxIops, long maxThroughput)
    => new() { MaxIops = maxIops, MaxThroughput = maxThroughput };

}
