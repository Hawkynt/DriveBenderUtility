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
