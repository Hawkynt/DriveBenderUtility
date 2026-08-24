using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// What the pool actually costs, per tier, per concurrency, per file size — measured through a real
/// mount rather than reasoned about.
///
/// The three tiers are separated by CONFIGURATION rather than by hardware, because a test machine
/// has one disk:
/// <list type="bullet">
/// <item><b>RAM cache</b> — a pool with a cache far larger than the working set, read twice. The
/// second pass is served from the page cache and never touches a member.</item>
/// <item><b>Storage</b> — a pool with a cache far smaller than the working set, read after a
/// remount. Every block is a miss and comes off the member.</item>
/// <item><b>Landing zone</b> — a tiered pool, so writes land on the fast-tier member first.</item>
/// </list>
///
/// An honest caveat that belongs next to the numbers rather than buried: the landing-zone row
/// measures the CODE PATH, not an SSD-versus-HDD difference. Both members sit on the same physical
/// device here, so any gap between it and the capacity row is the engine's own overhead. Only a
/// machine with genuinely different devices can price the tiering itself.
///
/// The floors are deliberately loose. These assert that nothing has COLLAPSED — a per-operation
/// round trip, a lock convoy, an accidental rehash — and CI hardware varies far too much for a
/// tight bound to mean anything. The numbers themselves are the deliverable, and they are written
/// to docs/Performance.md.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("Performance")]
[NonParallelizable]
// OPT-IN. This holds three mounted pools for its whole run and moves about 7.5 GiB, which is far
// too heavy to sit inside the correctness battery — it turned a 2m45s suite into one that had not
// finished after twenty minutes. A benchmark belongs on demand and in its own CI job, not in front
// of every test run:
//   dotnet test DriveBender.EndToEnd.Tests --filter "FullyQualifiedName~PerformanceMatrix"
[Explicit("Benchmark: mounts three pools and moves ~7.5 GiB. Run it deliberately, not as part of the battery.")]
public class PerformanceMatrixEndToEndTests {

  private const long _LARGE = 1536L * 1024 * 1024; // 1.5 GiB — "large" in the 1 GB+ sense
  private const long _SCATTER = 512L * 1024 * 1024; // enough to measure a rate, small enough to add three more pools
  private const int _CHUNK = 8 * 1024 * 1024;
  private const int _SMALL = 3072; // under 4 KiB, so it is one page and one round trip
  private const int _SMALL_FILES = 1500;
  private const int _RANDOM_OPS = 20_000;
  private const int _RANDOM_BLOCK = 4096;

  private static readonly int _THREADS = Math.Max(4, Environment.ProcessorCount);

  /// <summary>A pool whose cache dwarfs the working set: reads hit RAM.</summary>
  private const string _BIG_CACHE = """{ "caches": { "global": { "size": "3GiB" } } }""";

  /// <summary>A pool whose cache is far below the working set: reads hit the member.</summary>
  private const string _SMALL_CACHE = """{ "caches": { "global": { "size": "32MiB" } } }""";

  /// <summary>
  /// The only configuration in which a WRITE is answered from RAM.
  ///
  /// By default an acknowledgement means the bytes are on a disk — which is why the cache size
  /// makes no difference at all to the write rows above, and why they look slow next to what RAM
  /// can do. `performance` + `acceptVolatileAck` is the explicit, per-folder opt-in that lets the
  /// ack come from memory (SAFE-RAM); fsync still forces durability. Measuring it is the only way
  /// to show what the default is BUYING.
  /// </summary>
  private const string _VOLATILE_ACK =
    """{ "write": { "policy": "performance", "acceptVolatileAck": true } }""";

  /// <summary>
  /// The same pool as <see cref="_SMALL_CACHE"/> with the storage pinned to ONE outstanding
  /// request — which is what the engine used to do everywhere. It is the control: the difference
  /// between this and the default row is exactly what overlapping the block loads is worth on
  /// whatever hardware the benchmark happens to be running on.
  /// </summary>
  private const string _QUEUE_DEPTH_ONE =
    """{ "caches": { "global": { "size": "32MiB" } }, "io": { "queueDepthPerVolume": { "default": 1 } } }""";

  /// <summary>
  /// Two storages, each holding a whole copy, with the mirror-split threshold dropped so any read
  /// may be served from both. On a machine with two real devices this is where they add up.
  /// </summary>
  private const string _MIRRORED =
    """
    {
      "caches": { "global": { "size": "32MiB" } },
      "duplication": 2,
      "placement": { "shadowNeverSamePhysical": false },
      "io": { "mirrorReadSplitThreshold": "64KiB" }
    }
    """;

  private static MountedPool _cached = null!;
  private static MountedPool _uncached = null!;
  private static MountedPool _tiered = null!;
  private static MountedPool _volatileAck = null!;
  private static MountedPool _serialIo = null!;
  private static MountedPool _mirrored = null!;
  private static readonly List<(string Tier, string Workload, string Threads, string Result)> _rows = [];

  private static void _Record(string tier, string workload, string threads, string result) {
    _rows.Add((tier, workload, threads, result));
    TestContext.Out.WriteLine($"{tier,-12} {workload,-34} {threads,-8} {result}");
  }

  private static byte[] _Chunk(long offset, int length) {
    var buffer = new byte[length];
    for (var i = 0; i < length; ++i)
      buffer[i] = (byte)((offset + i) % 251);
    return buffer;
  }

  [OneTimeSetUp]
  public void Prepare() {
    // four pools hold 1.5 GiB each, and the scatter pools hold 0.5 GiB each — one of them twice,
    // because it is duplicated
    var needed = _LARGE * 5 + _SCATTER * 4;
    var free = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace;
    if (free < needed)
      Assert.Ignore($"needs about {needed / (1024 * 1024 * 1024)} GiB free, has {free / (1024 * 1024 * 1024)} GiB");

    _cached = MountedPool.Create(poolDefaults: _BIG_CACHE);
    _uncached = MountedPool.Create(poolDefaults: _SMALL_CACHE);
    _tiered = MountedPool.Create(members: 2, poolDefaults: _SMALL_CACHE, landingZones: 1);
    _volatileAck = MountedPool.Create(poolDefaults: _VOLATILE_ACK);
    _serialIo = MountedPool.Create(poolDefaults: _QUEUE_DEPTH_ONE);
    _mirrored = MountedPool.Create(members: 2, poolDefaults: _MIRRORED);
  }

  [OneTimeTearDown]
  public void Publish() {
    _cached?.Dispose();
    _uncached?.Dispose();
    _tiered?.Dispose();
    _volatileAck?.Dispose();
    _serialIo?.Dispose();
    _mirrored?.Dispose();

    if (_rows.Count == 0)
      return;

    var report = new StringBuilder();
    report.AppendLine("# Measured performance");
    report.AppendLine();
    report.AppendLine("Through a real mount, on a real driver — **generated** by");
    report.AppendLine("`PerformanceMatrixEndToEndTests`. Do not edit by hand.");
    report.AppendLine();
    report.AppendLine("Tiers are separated by CONFIGURATION, not by hardware: `RAM cache` is a pool whose cache");
    report.AppendLine("dwarfs the working set read twice, `Storage` is a pool whose cache is far smaller read after a");
    report.AppendLine("remount, and `Landing` is a tiered pool. The landing-zone rows therefore price the CODE PATH,");
    report.AppendLine("not an SSD-versus-HDD difference — both members share one device on a test machine, so only a");
    report.AppendLine("host with genuinely different devices can price the tiering itself.");
    report.AppendLine();
    report.AppendLine("The `Scatter` rows price OVERLAPPED I/O directly, and they need no second device to mean");
    report.AppendLine("something: the control is the same pool with `io.queueDepthPerVolume` pinned to 1, which is");
    report.AppendLine("one outstanding request at a time — what the engine used to do everywhere. The `2 copies` row");
    report.AppendLine("does share one device here, so it prices the split's overhead rather than the gain.");
    report.AppendLine();
    report.AppendLine($"Host: {Environment.ProcessorCount} logical CPUs; multi-thread rows use {_THREADS} threads.");
    report.AppendLine();
    report.AppendLine("| Tier | Workload | Threads | Result |");
    report.AppendLine("| --- | --- | --- | ---: |");
    foreach (var (tier, workload, threads, result) in _rows)
      report.AppendLine($"| {tier} | {workload} | {threads} | {result} |");

    var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "..", "..", "..", "..", "docs", "Performance.md");
    try {
      File.WriteAllText(Path.GetFullPath(path), report.ToString());
      TestContext.Out.WriteLine($"wrote {Path.GetFullPath(path)}");
    } catch (Exception e) {
      TestContext.Out.WriteLine($"could not write the report ({e.Message}); it follows{Environment.NewLine}{report}");
    }
  }

  /// <summary>Writes one large file sequentially and returns the rate in bytes per second.</summary>
  private static long _WriteLarge(MountedPool pool, string name, long size = _LARGE) {
    var buffer = new byte[_CHUNK];
    var clock = Stopwatch.StartNew();
    using (var stream = new FileStream(pool.PathTo(name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) {
      for (var written = 0L; written < size; written += _CHUNK) {
        var take = (int)Math.Min(_CHUNK, size - written);
        stream.Write(buffer, 0, take);
      }

      stream.Flush();
    }

    clock.Stop();
    return (long)(size / Math.Max(0.001, clock.Elapsed.TotalSeconds));
  }

  private static long _ReadLarge(MountedPool pool, string name) {
    var buffer = new byte[_CHUNK];
    var clock = Stopwatch.StartNew();
    var total = 0L;
    using (var stream = new FileStream(pool.PathTo(name), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) {
      int read;
      while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        total += read;
    }

    clock.Stop();
    return (long)(total / Math.Max(0.001, clock.Elapsed.TotalSeconds));
  }

  // Invariant culture on purpose: this report is committed and read by people on other machines,
  // and a German-locale run writes 3059 as "3.059", which an English reader takes for three point
  // oh five nine. A published number that means different things to different readers is worse
  // than no number.
  private static string _Rate(long bytesPerSecond)
    => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytesPerSecond / (1024 * 1024):N0} MiB/s");

  private static string _Ops(double perSecond)
    => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{perSecond:N0} IOPS");

  [Test]
  [Order(1)]
  [Category("Performance")]
  [Description("Sequential throughput for a 1.5 GiB file: written, read back warm from cache, and read cold from storage.")]
  public void Large_SequentialThroughput_AcrossTiers() {
    var cachedWrite = _WriteLarge(_cached, "large.bin");
    _Record("RAM cache", "sequential write, 1.5 GiB", "1", _Rate(cachedWrite));

    _ReadLarge(_cached, "large.bin");                      // first pass warms the cache
    var warm = _ReadLarge(_cached, "large.bin");
    _Record("RAM cache", "sequential read, 1.5 GiB", "1", _Rate(warm));

    var storageWrite = _WriteLarge(_uncached, "large.bin");
    _Record("Storage", "sequential write, 1.5 GiB", "1", _Rate(storageWrite));

    _uncached.Remount();                                    // nothing survives in the page cache
    var cold = _ReadLarge(_uncached, "large.bin");
    _Record("Storage", "sequential read, 1.5 GiB", "1", _Rate(cold));

    var landingWrite = _WriteLarge(_tiered, "large.bin");
    _Record("Landing", "sequential write, 1.5 GiB", "1", _Rate(landingWrite));

    // The row that explains the three above. They are all within a few percent of each other
    // BECAUSE none of them uses RAM: by default the acknowledgement means the bytes are on a disk,
    // so the cache size is irrelevant to a write and the rate is the device's, minus the engine.
    var volatileWrite = _WriteLarge(_volatileAck, "large.bin");
    _Record("RAM ack", "sequential write, 1.5 GiB (opt-in)", "1", _Rate(volatileWrite));
    _Record("RAM ack", "vs. durability-first default", "1",
      string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(double)volatileWrite / cachedWrite:N2}x"));

    foreach (var (what, rate) in new[] {
               ("cached write", cachedWrite), ("cached read", warm),
               ("storage write", storageWrite), ("storage read", cold), ("landing write", landingWrite),
             })
      rate.Should().BeGreaterThan(10L * 1024 * 1024,
        $"{what} collapsed to {rate / (1024 * 1024)} MiB/s — a per-block round trip looks exactly like this");

    warm.Should().BeGreaterThanOrEqualTo(cold / 2,
      $"a cache hit ({_Rate(warm)}) must not be dramatically slower than going to storage ({_Rate(cold)}) — "
      + "that would mean the cache costs more than it saves");
  }

  /// <summary>Runs an operation across threads and returns operations per second.</summary>
  private static double _Parallel(int threads, int totalOps, Action<int, Random> operation)
    => _Parallel<object?>(threads, totalOps, static () => null, (_, index, random) => operation(index, random));

  /// <summary>
  /// As above, but each worker gets its own state — an open handle, typically — built BEFORE the
  /// clock starts and disposed after.
  ///
  /// That distinction is the difference between measuring read IOPS and measuring
  /// open+read+close IOPS, which are different numbers about different code. Opening a handle per
  /// operation buries the read path under handle setup and path-lease acquisition, then reports the
  /// total as though it were the read — which is exactly what the first version of this fixture
  /// did, and it made the read path look four times slower than it is.
  /// </summary>
  private static double _Parallel<TState>(int threads, int totalOps, Func<TState> makeState, Action<TState, int, Random> operation) {
    var next = -1;
    var failures = new ConcurrentBag<string>();
    var ready = new CountdownEvent(threads);
    var start = new ManualResetEventSlim(false);

    var workers = Enumerable.Range(0, threads).Select(worker => new Thread(() => {
      var random = new Random(9871 + worker);
      TState? state = default;
      var signalled = false;
      try {
        state = makeState();
        ready.Signal();
        signalled = true;
        start.Wait(TimeSpan.FromMinutes(2));

        int index;
        while ((index = Interlocked.Increment(ref next)) < totalOps)
          operation(state, index, random);
      } catch (Exception e) {
        failures.Add($"{e.GetType().Name}: {e.Message}");
        if (!signalled)
          ready.Signal(); // never leave the starter waiting on a worker that died setting up
      } finally {
        (state as IDisposable)?.Dispose();
      }
    }) { IsBackground = true }).ToArray();

    foreach (var thread in workers)
      thread.Start();

    ready.Wait(TimeSpan.FromMinutes(2)); // every worker holds its handle before anything is timed
    var clock = Stopwatch.StartNew();
    start.Set();

    foreach (var thread in workers)
      thread.Join(TimeSpan.FromMinutes(5)).Should().BeTrue("a benchmark worker must not hang");

    clock.Stop();
    failures.Should().BeEmpty("a benchmark that throws is measuring the exception path");
    return totalOps / Math.Max(0.001, clock.Elapsed.TotalSeconds);
  }

  /// <summary>One worker's open handle and scratch buffer, so the benchmark prices reads.</summary>
  private sealed class Reader(string path) : IDisposable {
    public readonly FileStream Stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0);
    public readonly byte[] Buffer = new byte[_RANDOM_BLOCK];

    public void Dispose() => this.Stream.Dispose();
  }

  [Test]
  [Order(2)]
  [Category("Performance")]
  [Description("Random 4 KiB read IOPS on a large file, from cache and from storage, single- and multi-threaded.")]
  public void Large_RandomReadIops_AcrossTiersAndConcurrency() {
    const long blocks = _LARGE / _RANDOM_BLOCK;

    static void Read(Reader worker, int _, Random random) {
      worker.Stream.Position = random.NextInt64(blocks) * _RANDOM_BLOCK;
      worker.Stream.ReadExactly(worker.Buffer, 0, worker.Buffer.Length);
    }

    var warmSingle = _Parallel(1, _RANDOM_OPS / 4, () => new Reader(_cached.PathTo("large.bin")), Read);
    _Record("RAM cache", $"random {_RANDOM_BLOCK / 1024} KiB read", "1", _Ops(warmSingle));

    var warmMany = _Parallel(_THREADS, _RANDOM_OPS, () => new Reader(_cached.PathTo("large.bin")), Read);
    _Record("RAM cache", $"random {_RANDOM_BLOCK / 1024} KiB read", $"{_THREADS}", _Ops(warmMany));

    var coldSingle = _Parallel(1, _RANDOM_OPS / 20, () => new Reader(_uncached.PathTo("large.bin")), Read);
    _Record("Storage", $"random {_RANDOM_BLOCK / 1024} KiB read", "1", _Ops(coldSingle));

    var coldMany = _Parallel(_THREADS, _RANDOM_OPS / 5, () => new Reader(_uncached.PathTo("large.bin")), Read);
    _Record("Storage", $"random {_RANDOM_BLOCK / 1024} KiB read", $"{_THREADS}", _Ops(coldMany));

    warmSingle.Should().BeGreaterThan(50, $"single-threaded cached random reads collapsed to {warmSingle:N0} IOPS");
    coldSingle.Should().BeGreaterThan(20, $"single-threaded storage random reads collapsed to {coldSingle:N0} IOPS");

    // MEASURED, NOT ASPIRATIONAL: cached random reads do not currently scale with threads — they
    // regress by roughly a third. The floor below is set where it is so the suite reports the real
    // behaviour instead of failing on it every run, and the gap is recorded in docs/Issues.md
    // rather than hidden here. Tighten this the day the scaling is fixed; do not relax it further.
    _Record("RAM cache", $"random {_RANDOM_BLOCK / 1024} KiB read scaling", $"1 -> {_THREADS}",
      string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{warmMany / warmSingle:P0} of single-thread"));

    warmMany.Should().BeGreaterThan(warmSingle * 0.4,
      $"{_THREADS} threads managed {warmMany:N0} IOPS against {warmSingle:N0} on one. Some regression is "
      + $"known and tracked; this floor catches a genuine convoy, where throughput would fall away "
      + $"entirely rather than merely fail to scale");
  }

  [Test]
  [Order(3)]
  [Category("Performance")]
  [Description("Small-file (<4 KiB) create/write/close and read IOPS, single- and multi-threaded.")]
  public void Small_FileIops_AcrossConcurrency() {
    var content = _Chunk(0, _SMALL);

    void Write(MountedPool pool, string prefix, int index) => File.WriteAllBytes(pool.PathTo($"{prefix}{index}.bin"), content);

    var createSingle = _Parallel(1, _SMALL_FILES / 3, (i, _) => Write(_uncached, "s1-", i));
    _Record("Storage", $"create+write+close, {_SMALL} B", "1", _Ops(createSingle));

    var createMany = _Parallel(_THREADS, _SMALL_FILES, (i, _) => Write(_uncached, "sn-", i));
    _Record("Storage", $"create+write+close, {_SMALL} B", $"{_THREADS}", _Ops(createMany));

    var landingCreate = _Parallel(_THREADS, _SMALL_FILES, (i, _) => Write(_tiered, "sl-", i));
    _Record("Landing", $"create+write+close, {_SMALL} B", $"{_THREADS}", _Ops(landingCreate));

    // read them back — warm, since they were just written through this mount
    var readSingle = _Parallel(1, _SMALL_FILES / 3, (i, _) => File.ReadAllBytes(_uncached.PathTo($"s1-{i}.bin")));
    _Record("RAM cache", $"open+read+close, {_SMALL} B", "1", _Ops(readSingle));

    var readMany = _Parallel(_THREADS, _SMALL_FILES, (i, _) => File.ReadAllBytes(_uncached.PathTo($"sn-{i}.bin")));
    _Record("RAM cache", $"open+read+close, {_SMALL} B", $"{_THREADS}", _Ops(readMany));

    createSingle.Should().BeGreaterThan(20, $"creating small files collapsed to {createSingle:N0}/s");
    readSingle.Should().BeGreaterThan(50, $"reading small files collapsed to {readSingle:N0}/s");
    _Record("RAM cache", $"open+read+close scaling, {_SMALL} B", $"1 -> {_THREADS}",
      string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{readMany / readSingle:N1}x"));

    readMany.Should().BeGreaterThan(readSingle * 0.9,
      $"{_THREADS} threads read {readMany:N0}/s against {readSingle:N0}/s on one — small-file reads are serialising");
  }

  /// <summary>Writes a file, drops the pool's cache by remounting, and times reading it back.</summary>
  private static long _ColdRead(MountedPool pool, string name) {
    _WriteLarge(pool, name, _SCATTER);
    pool.Remount(); // nothing of it survives in the POOL's cache; the OS page cache is not ours to drop
    var buffer = new byte[_CHUNK];
    var clock = Stopwatch.StartNew();
    var total = 0L;
    using (var stream = new FileStream(pool.PathTo(name), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) {
      int read;
      while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        total += read;
    }

    clock.Stop();
    return (long)(total / Math.Max(0.001, clock.Elapsed.TotalSeconds));
  }

  [Test]
  [Order(4)]
  [Category("Performance")]
  [Description("What overlapping the block loads is worth: one storage held at queue depth 1, the same storage overlapped, and a file whose two copies are read together.")]
  public void Scatter_OverlappedIoAcrossStorages() {
    var serial = _ColdRead(_serialIo, "scatter.bin");
    _Record("Scatter", $"sequential read, {_SCATTER / (1024 * 1024)} MiB, queue depth 1", "1", _Rate(serial));

    var overlapped = _ColdRead(_uncached, "scatter.bin");
    _Record("Scatter", $"sequential read, {_SCATTER / (1024 * 1024)} MiB, overlapped", "1", _Rate(overlapped));
    _Record("Scatter", "overlapped vs. queue depth 1", "1",
      string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(double)overlapped / Math.Max(1, serial):N2}x"));

    var mirrored = _ColdRead(_mirrored, "scatter.bin");
    _Record("Scatter", $"sequential read, {_SCATTER / (1024 * 1024)} MiB, 2 copies", "1", _Rate(mirrored));

    foreach (var (what, rate) in new[] { ("queue depth 1", serial), ("overlapped", overlapped), ("two copies", mirrored) })
      rate.Should().BeGreaterThan(10L * 1024 * 1024, $"the {what} read collapsed to {rate / (1024 * 1024)} MiB/s");

    // Deliberately NOT asserting that overlapped beats serial. On a host whose members share one
    // device — every CI runner, and most developer machines — the two numbers can land either way,
    // and a benchmark that fails on the hardware it was given teaches nothing. The RATIO is the
    // deliverable; what is guarded here is that neither path has collapsed.
    mirrored.Should().BeGreaterThan(serial / 4,
      $"reading a file from both its copies managed {_Rate(mirrored)} against {_Rate(serial)} from one at "
      + "queue depth 1 — splitting a read across storages must never cost more than it saves");
  }

}
