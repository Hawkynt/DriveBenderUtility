using System.Diagnostics;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Multi-reader / multi-writer stress (TST-CONCURRENCY). Where <see cref="ConcurrencyFuzzTests"/>
/// gives each file a single owning writer, these tests deliberately point MANY readers and MANY
/// writers at the SAME paths while the background scheduler runs, because that is the shape in
/// which the remaining races live: a reader interleaving with a flush, a rename crossing a write,
/// a delete racing a read.
///
/// Three properties are asserted, and all three are absolutes:
/// <list type="bullet">
///   <item>NO RACE — every completed read observes one whole version of a file, never a mixture
///   of two, never a length that went backwards past an acknowledged write.</item>
///   <item>NO DEADLOCK — every worker finishes inside its watchdog. On a timeout the failure
///   names each stuck worker and the operation it was last inside, so a hang is diagnosable
///   from CI output alone rather than needing a debugger attached to a shared runner.</item>
///   <item>BOUNDED RESOURCES — allocation and managed heap stay proportional to the configured
///   cache budget and the bytes actually moved, not to the number of operations.</item>
/// </list>
///
/// Every bound below is a generous order-of-magnitude assertion: these run on shared CI runners,
/// so they must catch a real regression without tripping on a noisy neighbour.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Stress")]
public class ConcurrencyStressTests {

  // duplication 2 acked from ONE copy: the second copy stays owed in the write buffer, so the
  // background OwedSyncJob genuinely races the foreground path instead of finding nothing to do
  private const string _CONFIG = """
    {
      "duplication": 2,
      "write": { "policy": "write-back", "minCopiesBeforeAck": 1 },
      "io": { "mirrorReadSplitThreshold": "1024" },
      "readAhead": { "enabled": true, "minWindow": "4096", "maxWindow": "32768" }
    }
    """;

  private const int _CACHE_BYTES = 8 * 1024 * 1024;

  private static readonly Guid _pool = Guid.Parse("57e50000-0000-0000-0000-000000000057");

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 28);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 28);
    this._fs = new(_pool, [new(this._v1), new(this._v2)],
      new("stress" + Guid.NewGuid().ToString("N"),
        new() { Size = _CACHE_BYTES.ToString(), BlockSize = "512", MetadataEntries = 4000, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, _CONFIG));
    this._fs.Mount(new(@"X:\"));
  }

  [TearDown]
  public void TearDown() => this._fs.Dispose();

  #region self-describing content

  // byte[i] = file*31 + version*7 + i (mod 256). 7 is invertible mod 256, so the version is
  // RECOVERABLE from the first byte — a reader can name the version it got without knowing which
  // writer produced it, and a buffer mixing two versions cannot decode consistently.
  private const int _INVERSE_OF_SEVEN = 183; // 7 * 183 == 1281 == 5*256 + 1

  private static byte[] _Content(int file, int version, int length) {
    var content = new byte[length];
    for (var i = 0; i < length; ++i)
      content[i] = (byte)((file * 31 + version * 7 + i) & 0xFF);

    return content;
  }

  private static int _DecodeVersion(int file, byte[] content)
    => content.Length == 0 ? 0 : ((content[0] - file * 31) * _INVERSE_OF_SEVEN) & 0xFF;

  /// <summary>
  /// Null when the buffer is one whole, undamaged version of <paramref name="file"/>; otherwise a
  /// description of the first byte that belongs to a different version (a torn read).
  /// </summary>
  private static string? _DescribeTearing(int file, byte[] content, int expectedLength) {
    if (content.Length == 0)
      return null; // the pre-write empty file is a legal observation

    if (content.Length != expectedLength)
      return $"length {content.Length} is neither 0 nor the full {expectedLength}";

    var version = _DecodeVersion(file, content);
    var whole = _Content(file, version, content.Length);
    for (var i = 0; i < content.Length; ++i)
      if (content[i] != whole[i])
        return $"byte {i} is {content[i]:X2}, but version {version} (decoded from byte 0) requires {whole[i]:X2} — the read mixes two versions";

    return null;
  }

  #endregion

  #region worker harness with deadlock watchdog

  /// <summary>What each worker is currently inside, so a hang can be named rather than guessed at.</summary>
  private sealed class Breadcrumbs {
    private readonly string[] _where;

    public Breadcrumbs(int workers) {
      this._where = new string[workers];
      Array.Fill(this._where, "not started");
    }

    public void At(int worker, string what) => Volatile.Write(ref this._where[worker], what);

    public string Of(int worker) => Volatile.Read(ref this._where[worker]);
  }

  /// <summary>
  /// Runs <paramref name="workers"/> threads against a hard-pumped background scheduler and joins
  /// them under a watchdog. A worker that does not finish is reported WITH its last breadcrumb —
  /// the whole point of the exercise is that a deadlock in CI must be actionable.
  /// </summary>
  private void _RunWorkers(int workerCount, TimeSpan watchdog, Action<int, Breadcrumbs> body) {
    var breadcrumbs = new Breadcrumbs(workerCount);
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    var scheduler = this._fs.CreateScheduler();
    using var stop = new ManualResetEventSlim(false);
    Exception? pumpFailure = null;

    var pump = new Thread(() => {
      try {
        while (!stop.IsSet) {
          scheduler.Pump();
          Thread.Sleep(1); // a real host pumps on a timer; a spin would measure the test, not the engine
        }
      } catch (Exception e) {
        pumpFailure = e;
      }
    }) { IsBackground = true, Name = "bg-pump" };

    // every worker waits here until ALL of them exist: started one by one, the early threads
    // finish their whole run before the last one is created, and readers then only ever observe
    // the settled final state — the race the test exists to exercise never happens
    using var start = new ManualResetEventSlim(false);
    var ready = 0;

    var threads = Enumerable.Range(0, workerCount).Select(worker => new Thread(() => {
      Interlocked.Increment(ref ready);
      start.Wait();
      try {
        body(worker, breadcrumbs);
        breadcrumbs.At(worker, "finished");
      } catch (Exception e) {
        breadcrumbs.At(worker, $"threw {e.GetType().Name}");
        failures.Add($"worker {worker} threw {e.GetType().Name}: {e.Message}");
      }
    }) { IsBackground = true, Name = $"stress-{worker}" }).ToArray();

    pump.Start();
    foreach (var thread in threads)
      thread.Start();

    var spin = new SpinWait();
    while (Volatile.Read(ref ready) < workerCount)
      spin.SpinOnce();

    start.Set();

    var stuck = new List<string>();
    var deadline = DateTime.UtcNow + watchdog;
    for (var worker = 0; worker < threads.Length; ++worker) {
      var remaining = deadline - DateTime.UtcNow;
      if (remaining < TimeSpan.Zero || !threads[worker].Join(remaining))
        stuck.Add($"worker {worker} is stuck at '{breadcrumbs.Of(worker)}'");
    }

    stop.Set();
    pump.Join(TimeSpan.FromSeconds(10));

    stuck.Should().BeEmpty("no operation may deadlock — " + string.Join("; ", stuck));
    pumpFailure.Should().BeNull("the background scheduler must never throw an unhandled exception");
    failures.Should().BeEmpty();

    scheduler.Quiesce();
  }

  private byte[] _ReadAll(string path) {
    var handle = this._fs.Open(path, AccessMode.Read, ShareMode.Read | ShareMode.Write);
    try {
      var length = this._fs.GetAttributes(path).Length;
      var buffer = new byte[length];
      var read = 0;
      while (read < length) {
        var got = this._fs.Read(handle, buffer.AsSpan(read), read);
        if (got <= 0)
          break;

        read += got;
      }

      return read == length ? buffer : buffer.AsSpan(0, read).ToArray();
    } finally {
      this._fs.Close(handle);
    }
  }

  private void _WriteVersion(string path, int file, int version, int length) {
    var handle = this._fs.Open(path, AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
    try {
      this._fs.Write(handle, _Content(file, version, length), 0, WriteMode.Normal);
    } finally {
      this._fs.Close(handle);
    }
  }

  #endregion

  [Test]
  [Category("Exception")]
  public void TearDetector_GivenABufferSplicedFromTwoVersions_ThenItIsReported() {
    // the suite's whole value rests on this detector, so it is tested directly: a stress test
    // whose oracle cannot see a fault is a stress test that always passes
    const int file = 3;
    const int length = 512;
    var clean = _Content(file, 5, length);
    _DescribeTearing(file, clean, length).Should().BeNull("an untouched version must verify");

    var torn = _Content(file, 5, length);
    var other = _Content(file, 6, length);
    Array.Copy(other, length / 2, torn, length / 2, length / 2); // second half from the next version

    _DescribeTearing(file, torn, length).Should().NotBeNull("a buffer mixing two versions must be reported as torn");
    _DescribeTearing(file, clean.AsSpan(0, length / 2).ToArray(), length).Should().NotBeNull("a short read must be reported");
    _DescribeTearing(file, [], length).Should().BeNull("the empty pre-write file is a legal observation");
  }

  [Test]
  [Category("EdgeCase")]
  public void Stress_GivenManyReadersOnFilesManyWritersAreRewriting_ThenNoReadIsEverTorn() {
    const int files = 4;
    const int writersPerFile = 2;
    const int readers = 6;
    const int rounds = 30;
    const int length = 4096;

    var paths = Enumerable.Range(0, files).Select(f => $"shared{f}.bin").ToArray();
    foreach (var path in paths)
      this._fs.Close(this._fs.Create(path, NodeKind.File, CreateFlags.None));

    var tears = new System.Collections.Concurrent.ConcurrentBag<string>();
    var observedVersions = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
    var readsChecked = 0;
    var nonEmptyReads = 0;
    var writerCount = files * writersPerFile;

    // readers run for as long as any writer is still going, so the observations straddle the
    // mutations rather than trailing them
    var writersActive = writerCount;

    this._RunWorkers(writerCount + readers, TimeSpan.FromMinutes(2), (worker, breadcrumbs) => {
      if (worker < writerCount) {
        // several writers hammer ONE file: any given read must still land on a whole version
        var file = worker % files;
        var lane = worker / files;
        try {
          for (var round = 0; round < rounds; ++round) {
            var version = 1 + lane * rounds + round; // every writer owns a disjoint version range
            breadcrumbs.At(worker, $"write {paths[file]} v{version}");
            this._WriteVersion(paths[file], file, version, length);
          }
        } finally {
          Interlocked.Decrement(ref writersActive);
        }

        return;
      }

      // per-reader memory: within ONE thread the observations are ordered, and no writer ever
      // truncates, so a file that this reader has already seen with content can never legally be
      // seen empty again. That is the monotonicity the GetAttributes stat/overlay race broke —
      // treating every empty read as "legal, it may predate the first write" would hide it.
      var seenWithContent = new bool[files];

      for (var round = 0; round < rounds * files * 8 && (round < files || Volatile.Read(ref writersActive) > 0); ++round) {
        var file = round % files;
        breadcrumbs.At(worker, $"read {paths[file]}");
        var got = this._ReadAll(paths[file]);
        Interlocked.Increment(ref readsChecked);
        if (got.Length > 0) {
          Interlocked.Increment(ref nonEmptyReads);
          observedVersions.TryAdd(_DecodeVersion(file, got), 0);
          seenWithContent[file] = true;
        } else if (seenWithContent[file])
          tears.Add($"'{paths[file]}': read back EMPTY after this reader had already observed {length} bytes — an acknowledged write was rolled back or hidden");

        if (_DescribeTearing(file, got, length) is { } torn)
          tears.Add($"'{paths[file]}': {torn}");
      }
    });

    tears.Should().BeEmpty("a completed read must observe exactly one version of a file");

    // the stress is only meaningful if the readers genuinely raced the writers: if every read
    // came back empty, or every read saw the same version, this test would pass vacuously
    readsChecked.Should().BeGreaterThan(readers, "the readers must actually have run");
    nonEmptyReads.Should().BeGreaterThan(0, "the readers must have observed written content, not only the empty pre-write file");
    observedVersions.Should().HaveCountGreaterThan(1,
      "the readers must have observed the file CHANGING under them — otherwise nothing was raced and this proves nothing");

    // every copy converges once the background work settles — no copy is left behind
    foreach (var (index, path) in paths.Index()) {
      var settled = this._ReadAll(path);
      _DescribeTearing(index, settled, length).Should().BeNull($"'{path}' must settle on a whole version");
      foreach (var (volume, name) in new[] { (this._v1, "v1"), (this._v2, "v2") })
      foreach (var shadow in new[] { false, true })
        if (volume.GetContent(path, shadow) is { } copy)
          copy.Should().Equal(settled, $"copy {name}{(shadow ? "/shadow" : "/primary")} of '{path}' must match the settled content");
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Stress_GivenReadersRacingRenamesDeletesAndWrites_ThenNothingDeadlocksOrCorrupts() {
    const int churn = 40;
    const int readers = 4;
    const int length = 2048;

    // one stable file per lane plus a rename target, so renames cross reads of the same states
    for (var lane = 0; lane < 2; ++lane)
      this._fs.Close(this._fs.Create($"lane{lane}.bin", NodeKind.File, CreateFlags.None));

    var problems = new System.Collections.Concurrent.ConcurrentBag<string>();

    this._RunWorkers(2 + 2 + readers, TimeSpan.FromMinutes(2), (worker, breadcrumbs) => {
      switch (worker) {
        case 0 or 1: {
          // writers keep both lanes moving
          var lane = worker;
          for (var round = 1; round <= churn; ++round) {
            breadcrumbs.At(worker, $"write lane{lane}.bin v{round}");
            try {
              this._WriteVersion($"lane{lane}.bin", lane, round, length);
            } catch (PoolFsException) {
              // the file may be mid-rename — a refusal is legal, a hang or a corruption is not
            }
          }

          return;
        }

        case 2: {
          // OPPOSING renames: two names swapped back and forth is the classic lock-ordering trap
          for (var round = 0; round < churn; ++round) {
            breadcrumbs.At(worker, "rename lane0.bin -> swap.tmp");
            try {
              this._fs.Rename("lane0.bin", "swap.tmp", RenameFlags.ReplaceExisting);
              breadcrumbs.At(worker, "rename swap.tmp -> lane0.bin");
              this._fs.Rename("swap.tmp", "lane0.bin", RenameFlags.ReplaceExisting);
            } catch (PoolFsException) {
              // losing the race to the other direction is legal
            }
          }

          return;
        }

        case 3: {
          // create/delete churn under the readers' feet
          for (var round = 0; round < churn; ++round) {
            breadcrumbs.At(worker, $"create+unlink churn{round}.bin");
            try {
              this._fs.Close(this._fs.Create($"churn{round}.bin", NodeKind.File, CreateFlags.None));
              this._WriteVersion($"churn{round}.bin", 9, 1, length);
              this._fs.Unlink($"churn{round}.bin");
            } catch (PoolFsException) {
              // a concurrent op may have got there first
            }
          }

          return;
        }

        default: {
          for (var round = 0; round < churn * 2; ++round) {
            var lane = round % 2;
            breadcrumbs.At(worker, $"read lane{lane}.bin");
            try {
              var got = this._ReadAll($"lane{lane}.bin");
              if (_DescribeTearing(lane, got, length) is { } torn)
                problems.Add($"'lane{lane}.bin': {torn}");
            } catch (PoolFsException) {
              // the path may be mid-rename — legal
            }

            breadcrumbs.At(worker, "list root");
            try {
              this._fs.ReadDirectory("");
            } catch (PoolFsException) {
              // legal under churn
            }
          }

          return;
        }
      }
    });

    problems.Should().BeEmpty("a read that completes must observe a whole version even while renames and deletes churn");
  }

  [Test]
  [Category("EdgeCase")]
  public void Stress_GivenConcurrentReadersAndWriters_ThenMemoryStaysProportionalToTheCacheBudget() {
    const int files = 4;
    const int rounds = 25;
    const int length = 8192;

    var paths = Enumerable.Range(0, files).Select(f => $"mem{f}.bin").ToArray();
    foreach (var path in paths)
      this._fs.Close(this._fs.Create(path, NodeKind.File, CreateFlags.None));

    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

    this._RunWorkers(files * 2, TimeSpan.FromMinutes(2), (worker, breadcrumbs) => {
      var file = worker % files;
      var writer = worker < files;
      for (var round = 1; round <= rounds; ++round)
        if (writer) {
          breadcrumbs.At(worker, $"write {paths[file]} v{round}");
          this._WriteVersion(paths[file], file, round, length);
        } else {
          breadcrumbs.At(worker, $"read {paths[file]}");
          this._ReadAll(paths[file]);
        }
    });

    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    var heapGrowth = GC.GetTotalMemory(forceFullCollection: true) - heapBefore;

    // RETAINED memory is the hard property: caches and buffers are bounded by configuration, so
    // the heap must not grow with the number of operations. The page cache plus the write
    // reservation come out of one budget; twice that is ample headroom for test scaffolding.
    heapGrowth.Should().BeLessThan(2L * _CACHE_BYTES,
      $"the managed heap grew by {heapGrowth / 1024} KiB after {files * rounds} writes and {files * rounds} reads — caches must stay inside their budget");

    // ALLOCATION RATE is the soft property: the engine copies each written payload, caches blocks
    // and journals intents, so allocation scales with bytes moved. It must not scale with the
    // SQUARE of the work, which is what an accidental per-block re-materialisation looks like.
    var bytesMoved = (long)files * rounds * length * 2;
    allocated.Should().BeLessThan(bytesMoved * 64,
      $"{allocated / 1024} KiB allocated to move {bytesMoved / 1024} KiB — the hot path is re-materialising data it should be reusing");
  }

  [Test]
  [Category("HappyPath")]
  public void Stress_GivenAnIdleMountedPool_ThenTheBackgroundPumpDoesNoWorkAndBarelyAllocates() {
    // an idle pool must cost essentially nothing: the daemon pumps this on a timer for the whole
    // lifetime of a mount, so any per-tick allocation or busywork burns a user's CPU all day
    this._fs.Close(this._fs.Create("idle.bin", NodeKind.File, CreateFlags.None));
    var scheduler = this._fs.CreateScheduler();
    scheduler.Quiesce(); // settle every job that mount-time heal queued

    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    var before = GC.GetTotalAllocatedBytes(precise: true);

    const int ticks = 200;
    var worked = 0;
    for (var tick = 0; tick < ticks; ++tick)
      worked += scheduler.Pump();

    var allocatedPerTick = (GC.GetTotalAllocatedBytes(precise: true) - before) / ticks;

    worked.Should().Be(0, "an idle pool must give the background scheduler nothing to do");
    allocatedPerTick.Should().BeLessThan(16 * 1024,
      $"an idle pump tick allocated {allocatedPerTick} bytes — a mounted pool pumps this forever");
  }

  [Test]
  [Category("HappyPath")]
  public void Stress_GivenSustainedConcurrentIo_ThenCpuTimeStaysProportionalToTheWorkDone() {
    const int files = 4;
    const int rounds = 20;
    const int length = 4096;

    var paths = Enumerable.Range(0, files).Select(f => $"cpu{f}.bin").ToArray();
    foreach (var path in paths)
      this._fs.Close(this._fs.Create(path, NodeKind.File, CreateFlags.None));

    var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var wallBefore = Stopwatch.GetTimestamp();

    this._RunWorkers(files * 2, TimeSpan.FromMinutes(2), (worker, breadcrumbs) => {
      var file = worker % files;
      var writer = worker < files;
      for (var round = 1; round <= rounds; ++round)
        if (writer) {
          breadcrumbs.At(worker, $"write {paths[file]} v{round}");
          this._WriteVersion(paths[file], file, round, length);
        } else {
          breadcrumbs.At(worker, $"read {paths[file]}");
          this._ReadAll(paths[file]);
        }
    });

    var cpu = process.TotalProcessorTime - cpuBefore;
    var wall = Stopwatch.GetElapsedTime(wallBefore);

    // CPU per unit of wall-clock is the spin detector: a lock-free retry loop, a spinning pump or
    // a busy-wait shows up as many core-seconds burned per second elapsed. Genuine parallel I/O
    // over in-memory volumes stays well inside the core count.
    var coresBusy = cpu.TotalSeconds / Math.Max(0.001, wall.TotalSeconds);
    coresBusy.Should().BeLessThan(Math.Max(2.0, Environment.ProcessorCount * 0.75),
      $"the engine burned {coresBusy:F1} cores of CPU per second of wall-clock — something is spinning rather than waiting");
  }

}
