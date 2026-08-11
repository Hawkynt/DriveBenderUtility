using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Engine-level throughput and allocation budgets (TST-PERF). The pre-existing Performance
/// tier covers DriveBender.Core, not the mount engine, so cache and allocation work had no
/// before/after number to point at. These run against in-memory volumes so they measure the
/// ENGINE, not the disk — the absolute figures are machine-dependent, therefore every
/// assertion is a generous ORDER-OF-MAGNITUDE bound that only a real regression trips.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Performance")]
public class EnginePerformanceTests {

  private static readonly Guid _pool = Guid.Parse("9e9f0000-0000-0000-0000-00000000000e");

  private static PoolFileSystem _NewPool(FakeVolumeIO v1, FakeVolumeIO v2, string blockSize = "4096", string cacheSize = "8388608")
    => new(_pool, [new(v1), new(v2)],
      new("perf" + Guid.NewGuid().ToString("N"), new() { Size = cacheSize, BlockSize = blockSize, MetadataEntries = 10_000, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "readAhead": { "enabled": false } }"""));

  [Test]
  [Category("HappyPath")]
  public void Read_GivenWarmCache_WhenReadSequentially_ThenServesWithoutPerBlockAllocation() {
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "P1", capacity: 1L << 28);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "P2", capacity: 1L << 28);
    using var fs = _NewPool(v1, v2);
    fs.Mount(new(@"X:\"));

    const int size = 1 << 20; // 1 MiB over 4 KiB blocks = 256 blocks
    var content = new byte[size];
    new Random(3).NextBytes(content);
    var write = fs.Create("perf.bin", NodeKind.File, CreateFlags.None);
    fs.Write(write, content, 0, WriteMode.Normal);
    fs.Close(write);

    var buffer = new byte[size];
    var handle = fs.Open("perf.bin", AccessMode.Read, ShareMode.Read);
    _ReadFully(fs, handle, buffer); // first pass warms the page cache

    // second pass is served entirely from cache: it must not allocate a block per read
    var before = GC.GetAllocatedBytesForCurrentThread();
    _ReadFully(fs, handle, buffer);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    fs.Close(handle);

    buffer.Should().Equal(content);
    allocated.Should().BeLessThan(size,
      $"a fully cached {size}-byte read allocated {allocated} bytes — it must not re-materialise its blocks");
  }

  [Test]
  [Category("HappyPath")]
  public void InvalidatePath_GivenALargeCache_WhenOneFileMutates_ThenCostDoesNotScaleWithOccupancy() {
    // the regression this pins: invalidation used to scan EVERY cached block of the pool to find
    // the handful belonging to one path, so a write got slower as the cache filled with other
    // files' blocks. Cost must track the mutated file, not total occupancy.
    var cache = new PageCache(EvictionPolicy.Lru, 64);
    cache.SetBudget(64L * 40_100); // headroom above the 40k bulk blocks so nothing is evicted
    for (var file = 0; file < 400; ++file)
      for (var block = 0; block < 100; ++block)
        cache.Put(new(_pool, $"bulk/f{file}.bin", block), new byte[64]);

    cache.Put(new(_pool, "target.bin", 0), new byte[64]);

    var sparse = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < 2000; ++i) {
      cache.Put(new(_pool, "target.bin", 0), new byte[64]);
      cache.InvalidatePath(_pool, "target.bin");
    }

    sparse.Stop();

    // 2000 put+invalidate cycles against a 40k-block cache: a full scan would be 80M key
    // comparisons and take seconds. The indexed path is microseconds per cycle.
    sparse.ElapsedMilliseconds.Should().BeLessThan(2000,
      $"invalidating a 1-block path in a 40k-block cache took {sparse.ElapsedMilliseconds} ms over 2000 cycles — it is scanning the whole cache again");
    cache.GetStatistics(_pool).Entries.Should().Be(40_000, "the other files' blocks are untouched");
  }

  [Test]
  [Category("HappyPath")]
  public void StageWrite_GivenOwedPayloads_WhenBuffered_ThenTheBytesAreNotCopiedAgain() {
    // Measured at the write buffer rather than through the engine on purpose: FakeVolumeIO
    // clones the whole file on every durable flush to model power loss, which swamps an
    // end-to-end allocation measurement (~19 MiB for a 1 MiB write) with test-double cost.
    // This pins the thing that actually changed — the staging path takes ownership of the
    // caller's array instead of defensively cloning every written byte a second time.
    var cache = new CacheInstance("stage" + Guid.NewGuid().ToString("N"),
      new() { Size = "33554432", BlockSize = "4096", Split = new() { Mode = CacheSplitMode.SharedFixed, Read = "10%" } });
    var buffer = new WriteBufferManager(cache);

    const int chunk = 64 * 1024;
    const int chunks = 16;
    buffer.StageWrite("warm.bin", 0, new byte[chunk], 0, 1); // warm the dictionaries and the reservation path

    var payloads = new byte[chunks][];
    for (var i = 0; i < chunks; ++i)
      payloads[i] = new byte[chunk]; // allocated OUTSIDE the measurement — the caller's own array

    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < chunks; ++i)
      buffer.StageWrite("owed.bin", (long)i * chunk, payloads[i], 0, 1).Should().BeTrue();

    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    var total = (long)chunk * chunks;

    allocated.Should().BeLessThan(total / 4,
      $"staging {total} bytes of already-materialised payload allocated {allocated} — it is cloning the caller's array again");
    buffer.OverlayLength("owed.bin", 0).Should().Be(total, "every staged op is still tracked");
  }

  [Test]
  [Category("HappyPath")]
  public void ResolveForFolder_GivenFolderOverrides_WhenResolvedRepeatedly_ThenTheMergeIsNotRebuiltEveryTime() {
    // The regression this pins: resolving a folder's effective config serialises the WHOLE pool
    // config to JSON, deep-merges each matching override and deserialises the result. Write calls
    // it TWICE (once directly, once inside the ack-quorum check), Unlink once, and every
    // placement/heal decision once more — so with any "folders" override configured this ran per
    // operation. The merge depends only on WHICH globs matched, so it is memoised by match set.
    var config = ConfigResolver.ResolveEffective(null, """
      {
        "duplication": 2,
        "folders": {
          "media/**": { "duplication": 3 },
          "scratch/**": { "write": { "policy": "performance", "acceptVolatileAck": true } }
        }
      }
      """);

    ConfigResolver.ResolveForFolder(config, "media/movies").Duplication.Should().Be(3, "the override must still apply");
    ConfigResolver.ResolveForFolder(config, "plain").Duplication.Should().Be(2, "an unmatched folder keeps the pool default");

    const int resolutions = 2000;
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < resolutions; ++i) {
      ConfigResolver.ResolveForFolder(config, "media/movies/season1");
      ConfigResolver.ResolveForFolder(config, "plain/documents");
    }

    var perResolution = (GC.GetAllocatedBytesForCurrentThread() - before) / (resolutions * 2);
    perResolution.Should().BeLessThan(512,
      $"each folder resolution allocated {perResolution} bytes — the JSON merge is being rebuilt instead of reused");

    // distinct folders sharing a match set must share the memo entry, so the cache is bounded by
    // configuration complexity and not by how many folders the pool contains
    ConfigResolver.ResolveForFolder(config, "media/a").Should().BeSameAs(ConfigResolver.ResolveForFolder(config, "media/b"));
    ConfigResolver.ResolveForFolder(config, "scratch/x").Duplication.Should().Be(2);
    ConfigResolver.ResolveForFolder(config, "scratch/x").Write!.Policy.Should().Be(WritePolicy.Performance);
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenAMirrorSplitOverTwoCopies_WhenBlocksAreRouted_ThenRoutingDoesNotAllocatePerBlock() {
    // block routing runs PER BLOCK: it used to build a LINQ Range→OrderBy→ThenBy→ToArray pipeline
    // every time, so a long sequential read allocated an enumerator chain and two arrays per block
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "P1", capacity: 1L << 28);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "P2", capacity: 1L << 28);
    using var fs = new PoolFileSystem(_pool, [new(v1), new(v2)],
      new("route" + Guid.NewGuid().ToString("N"), new() { Size = "4194304", BlockSize = "512", MetadataEntries = 2000, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "readAhead": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));

    const int size = 128 * 1024;
    var content = new byte[size];
    new Random(7).NextBytes(content);
    var write = fs.Create("routed.bin", NodeKind.File, CreateFlags.None);
    fs.Write(write, content, 0, WriteMode.Normal);
    fs.Close(write);

    var buffer = new byte[size];
    var handle = fs.Open("routed.bin", AccessMode.Read, ShareMode.Read);
    _ReadFully(fs, handle, buffer); // warm the cache so the measurement is routing, not I/O

    var before = GC.GetAllocatedBytesForCurrentThread();
    _ReadFully(fs, handle, buffer);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    fs.Close(handle);

    buffer.Should().Equal(content);
    allocated.Should().BeLessThan(size,
      $"a fully cached {size}-byte mirrored read allocated {allocated} bytes — block routing is allocating per block");
  }

  [Test]
  [Category("HappyPath")]
  public void Publish_GivenTheRateLimitIsSpent_WhenTheIoPathReports_ThenTheDropCostsNoLock() {
    // every read and write publishes an activity event, and a busy pool spends the per-second
    // budget immediately — so the DROP path is the hot path, and it used to take a process-wide
    // lock just to decide the event was not wanted
    var now = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    var feed = new ActivityFeed(ringCapacity: 64, maxEventsPerSecond: 10, clock: () => now);
    for (var i = 0; i < 20; ++i)
      feed.Publish(ActivityKind.Read, "spend.bin", 4096, reason: "budget");

    feed.DroppedSamples.Should().BeGreaterThan(0, "the budget must actually be spent for this to measure the drop path");

    const int drops = 5000;
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < drops; ++i)
      feed.Publish(ActivityKind.Read, "hot.bin", 4096, reason: "user I/O");

    var perDrop = (GC.GetAllocatedBytesForCurrentThread() - before) / drops;
    perDrop.Should().Be(0, $"a dropped event allocated {perDrop} bytes — it must cost nothing but counters");
    feed.History.Should().HaveCount(10, "only the events inside the budget are retained");
  }

  private static void _ReadFully(PoolFileSystem fs, NodeHandle handle, byte[] buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var got = fs.Read(handle, buffer.AsSpan(read), read);
      if (got <= 0)
        break;

      read += got;
    }

    read.Should().Be(buffer.Length);
  }

}
