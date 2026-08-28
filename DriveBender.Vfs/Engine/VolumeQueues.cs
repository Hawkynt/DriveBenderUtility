using System.Collections.Concurrent;

namespace DivisonM.Vfs.Engine;

/// <summary>
/// What kind of device sits behind a physical volume, as far as the host can actually tell.
///
/// It is deliberately a THREE-valued answer. A queue depth that suits an SSD makes a spindle
/// thrash, and one that suits a spindle leaves an NVMe idle, so guessing wrong in either
/// direction costs real throughput — and pretending to know is worse than admitting we do not.
/// </summary>
public enum MediaClass {
  Unknown,
  Rotational,
  Solid,
}

/// <summary>
/// Classifies a physical volume by media, cached forever — a disk does not stop spinning
/// halfway through a mount.
///
/// Linux answers authoritatively from sysfs, which is exactly where
/// <see cref="PhysicalVolumeResolver"/> already resolved the whole-disk device name to. Every
/// other platform answers <see cref="MediaClass.Unknown"/>: the Windows equivalent is a
/// DeviceIoControl seek-penalty query, and an untested guess at it would silently mis-tune
/// every pool on that platform. Where the class is unknown the <c>hdd</c>/<c>ssd</c> keys of
/// <c>io.queueDepthPerVolume</c> simply do not bind, and a member name, a role or
/// <c>default</c> does — so the setting stays usable rather than half-working.
/// </summary>
public static class MediaProbe {

  private static readonly ConcurrentDictionary<string, MediaClass> _classified = new(StringComparer.OrdinalIgnoreCase);

  public static MediaClass Classify(string physicalVolumeId)
    => string.IsNullOrEmpty(physicalVolumeId) ? MediaClass.Unknown : _classified.GetOrAdd(physicalVolumeId, _Probe);

  /// <summary>Overrides the classification of one volume — for tests, which have no real hardware to ask.</summary>
  public static void Override(string physicalVolumeId, MediaClass media) => _classified[physicalVolumeId] = media;

  private static MediaClass _Probe(string physicalVolumeId) {
    if (!OperatingSystem.IsLinux() || !physicalVolumeId.StartsWith("/dev/", StringComparison.Ordinal))
      return MediaClass.Unknown;

    try {
      var device = physicalVolumeId["/dev/".Length..];
      var rotational = $"/sys/block/{device}/queue/rotational";
      if (!File.Exists(rotational))
        return MediaClass.Unknown;

      return File.ReadAllText(rotational).Trim() == "1" ? MediaClass.Rotational : MediaClass.Solid;
    } catch (IOException) {
      return MediaClass.Unknown;
    } catch (UnauthorizedAccessException) {
      return MediaClass.Unknown;
    }
  }

}

/// <summary>
/// Per-PHYSICAL-VOLUME I/O admission (<c>CFG.io.queueDepthPerVolume</c>, §6.4).
///
/// Two things the engine needs and had neither of:
///
/// 1. **A fan-out width.** A read that spans several blocks used to load them ONE AT A TIME
///    unless the file was duplicated AND the read was over the mirror-split threshold — so the
///    normal case ran a device at queue depth one, which is the single easiest way to leave
///    most of an SSD's throughput unused. <see cref="FanOutFor"/> says how many block loads a
///    single request may keep in flight, summed over the DISTINCT devices its copies live on:
///    that is what makes several storages in one tier add up rather than take turns.
///
/// 2. **A cap.** Fan-out without a bound is how twenty threads turn into hundreds of
///    outstanding requests at one disk. <see cref="Enter"/> is the per-device gate, and it is
///    keyed by <see cref="IVolumeIO.PhysicalVolumeId"/> rather than by member, because two
///    members carved out of one spindle ARE one queue however the manifest lists them.
///
/// The defaults are asymmetric on purpose. A spindle gets 2 (§6.4: a deep queue on rotating
/// media is seek thrash), a member whose operations park the caller for a network round trip
/// gets 4 (the blocking scheduler bounds the rest), and anything solid or unrecognised gets a
/// depth that scales with the host and is deliberately generous — a cap that binds on a fast
/// local device would cost throughput to protect against nothing.
/// </summary>
public sealed class VolumeQueues {

  /// <summary>The most blocks one request keeps in flight against a single device.</summary>
  private const int _MAX_FAN_OUT_PER_VOLUME = 16;

  /// <summary>The most blocks one request keeps in flight in total, however many devices it spans.</summary>
  private const int _MAX_FAN_OUT = 32;

  private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConcurrentDictionary<Guid, int> _depths = new();
  private IReadOnlyDictionary<Guid, MemberRole> _roles;
  private PoolConfig _config;

  public VolumeQueues(PoolConfig config, IReadOnlyDictionary<Guid, MemberRole> roles) {
    this._config = config;
    this._roles = roles;
  }

  /// <summary>Re-reads the tuning after a live config reload; depths are recomputed on next use.</summary>
  public void UpdateConfig(PoolConfig config) {
    this._config = config;
    this._Reset();
  }

  /// <summary>Roles changed live (a member re-tiered), and the role is one of the depth keys.</summary>
  public void UpdateRoles(IReadOnlyDictionary<Guid, MemberRole> roles) {
    this._roles = roles;
    this._Reset();
  }

  private void _Reset() {
    this._depths.Clear();
    this._gates.Clear(); // a gate sized to the old depth would keep enforcing it forever
  }

  /// <summary>A permit on one device, released when it goes out of scope. A struct, so the hot path does not allocate.</summary>
  public readonly struct Admission(SemaphoreSlim? gate) : IDisposable {
    public void Dispose() => gate?.Release();
  }

  /// <summary>
  /// How long a request will queue behind a device's cap before giving up on the cap and going
  /// anyway. Long enough that no healthy device ever reaches it — at a depth of 2 and ten
  /// milliseconds a request, this is hundreds of queued requests — and finite because the cap is
  /// a TUNING knob and must never become a liveness hazard: a member that hangs while holding a
  /// permit would otherwise keep every later request to that device waiting on a semaphore
  /// instead of on the failover the engine already knows how to do.
  /// </summary>
  private static readonly TimeSpan _ADMISSION_TIMEOUT = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Waits for room at the device behind <paramref name="volume"/>. The permit covers ONE leaf
  /// operation (a block read, a block write) and is never held across a call back into the
  /// engine, so admission can never be part of a cycle.
  /// </summary>
  public Admission Enter(IVolumeIO volume, long bytes = 0) {
    this._Throttle(volume, bytes);
    var gate = this._GateFor(volume);
    return gate.Wait(_ADMISSION_TIMEOUT) ? new(gate) : new(null); // over-subscribe rather than stall
  }

  /// <summary>
  /// Holds a member to the rate its manifest allows, if it set one.
  ///
  /// A pool is rarely the only thing using a disk. A mechanical drive shared with something else,
  /// or a cloud endpoint with a rate limit and a bill attached, is better served slowly than
  /// saturated. Two independent buckets, because the two limits mean different things: operations
  /// per second is what a seek-bound device runs out of, bytes per second is what a link runs out
  /// of, and a member may want either or both.
  ///
  /// The wait happens BEFORE taking a concurrency permit, so a throttled member never occupies a
  /// slot while doing nothing — otherwise a slow storage would throttle the pool rather than
  /// itself. Combined with load-aware placement, a throttled member simply receives less work: its
  /// in-flight count stays high, and new files go elsewhere.
  /// </summary>
  private void _Throttle(IVolumeIO volume, long bytes) {
    if (!this._throttles.TryGetValue(volume.MemberId, out var throttle))
      return;

    var wait = throttle.Reserve(bytes);
    if (wait > TimeSpan.Zero)
      Thread.Sleep(wait);
  }

  private readonly ConcurrentDictionary<Guid, MemberThrottle> _throttles = new();

  /// <summary>Applies per-member rate limits; a member absent from the map is unlimited.</summary>
  public void SetThrottles(IEnumerable<(Guid MemberId, int MaxIops, long MaxThroughput)> limits) {
    this._throttles.Clear();
    foreach (var (memberId, maxIops, maxThroughput) in limits)
      if (maxIops > 0 || maxThroughput > 0)
        this._throttles[memberId] = new(maxIops, maxThroughput);
  }

  /// <summary>
  /// A pair of token buckets — one counting operations, one counting bytes.
  ///
  /// Each refills continuously at its configured rate and holds at most one second of credit, so a
  /// member idle for a while may burst briefly and then settles to the limit. Deliberately not a
  /// fixed window: a window lets a caller spend the whole allowance instantly and then stall for
  /// the remainder, which reads to a user as a stutter rather than a limit.
  /// </summary>
  private sealed class MemberThrottle(int maxIops, long maxThroughput) {
    private readonly Lock _lock = new();
    private double _operations = maxIops;
    private double _bytes = maxThroughput;
    private long _lastTicks = Environment.TickCount64;

    public TimeSpan Reserve(long bytes) {
      lock (this._lock) {
        var now = Environment.TickCount64;
        var elapsed = Math.Max(0, now - this._lastTicks) / 1000.0;
        this._lastTicks = now;

        var wait = 0.0;
        if (maxIops > 0) {
          this._operations = Math.Min(maxIops, this._operations + elapsed * maxIops) - 1;
          if (this._operations < 0)
            wait = Math.Max(wait, -this._operations / maxIops);
        }

        if (maxThroughput > 0 && bytes > 0) {
          this._bytes = Math.Min(maxThroughput, this._bytes + elapsed * maxThroughput) - bytes;
          if (this._bytes < 0)
            wait = Math.Max(wait, -this._bytes / (double)maxThroughput);
        }

        return TimeSpan.FromSeconds(wait);
      }
    }
  }

  /// <summary>How many block loads a request over <paramref name="copies"/> may keep in flight at once.</summary>
  public int FanOutFor(IReadOnlyList<PhysicalCopy> copies) {
    if (copies.Count == 0)
      return 1;
    if (copies.Count == 1)
      return this.FanOutFor(copies[0].Volume); // the ordinary unduplicated file — no set, no sum

    var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var total = 0;
    for (var index = 0; index < copies.Count; ++index) {
      var volume = copies[index].Volume;
      if (!counted.Add(volume.PhysicalVolumeId))
        continue; // two members on one spindle are one queue, not two

      total += this.FanOutFor(volume);
    }

    return Math.Clamp(total, 1, _MAX_FAN_OUT);
  }

  /// <summary>
  /// How many block loads one request may keep in flight against a SINGLE device — what a read
  /// that is not being split across copies gets, since fanning out wider than the one storage
  /// that will serve it only parks threads on that storage's own cap.
  /// </summary>
  public int FanOutFor(IVolumeIO volume) => Math.Clamp(this.DepthFor(volume), 1, _MAX_FAN_OUT_PER_VOLUME);

  /// <summary>The configured or defaulted queue depth of one member's device.</summary>
  public int DepthFor(IVolumeIO volume) => this._depths.GetOrAdd(volume.MemberId, _ => this._ResolveDepth(volume));

  private SemaphoreSlim _GateFor(IVolumeIO volume)
    => this._gates.GetOrAdd(volume.PhysicalVolumeId, _ => new(this.DepthFor(volume), int.MaxValue));

  /// <summary>
  /// Keys are tried most specific first: this member by name, then its role, then its media
  /// class, then <c>default</c>. Naming a member wins over naming its kind, which is what a
  /// user tuning one troublesome disk expects.
  /// </summary>
  private int _ResolveDepth(IVolumeIO volume) {
    if (this._config.Io?.QueueDepthPerVolume is { } configured)
      foreach (var key in this._KeysFor(volume))
        if (key != null && configured.TryGetValue(key, out var depth) && depth > 0)
          return depth;

    return _DefaultDepthFor(volume);
  }

  private IEnumerable<string?> _KeysFor(IVolumeIO volume) {
    yield return volume.DisplayName;
    yield return this._roles.TryGetValue(volume.MemberId, out var role) ? _RoleKey(role) : _RoleKey(MemberRole.Capacity);
    yield return MediaProbe.Classify(volume.PhysicalVolumeId) switch {
      MediaClass.Rotational => "hdd",
      MediaClass.Solid => "ssd",
      _ => null,
    };
    yield return "default";
  }

  private static string _RoleKey(MemberRole role) => role switch {
    MemberRole.Landing => "landing",
    MemberRole.ReadOnly => "readonly",
    _ => "capacity",
  };

  private static int _DefaultDepthFor(IVolumeIO volume) {
    if (volume.BlocksCallingThread)
      return 4; // each request parks a thread for a round trip; BlockingIoScheduler bounds the rest

    return MediaProbe.Classify(volume.PhysicalVolumeId) == MediaClass.Rotational
      ? 2 // §6.4: a spindle serves a deep queue by seeking, which is slower than serving it in order
      : Math.Clamp(Environment.ProcessorCount * 4, 32, 256);
  }

}
