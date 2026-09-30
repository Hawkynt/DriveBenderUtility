using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class PageCacheTests {

  private static readonly Guid _poolA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
  private static readonly Guid _poolB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

  [Test]
  [Category("EdgeCase")]
  public void PutIfCurrent_GivenTheWholePoolWasInvalidated_ThenALateBackgroundPutIsStillRejected() {
    // A prefetch captures the epoch BEFORE it reads a block off disk and hands it back with
    // PutIfCurrent, so a write that landed meanwhile rejects the now-stale bytes. Dropping the
    // pool's cache used to REPLACE the shard, which restarted the epoch at zero — so a prefetch
    // holding the captured epoch zero was accepted afterwards, putting back exactly the
    // pre-invalidation block the epoch exists to reject.
    var cache = new PageCache(EvictionPolicy.Lru, 8);
    cache.SetBudget(4096);
    var key = new PageKey(_poolA, "prefetched.bin", 0);

    var captured = cache.EpochOf(_poolA); // what a background load would have recorded
    cache.Put(key, [1, 2, 3, 4]);
    cache.InvalidatePool(_poolA);

    cache.PutIfCurrent(key, [9, 9, 9, 9], captured);

    cache.TryGet(key, out _).Should().BeFalse("a load that began before the invalidation must not repopulate the cache after it");
  }

  [Test]
  [Category("EdgeCase")]
  public void PageCache_GivenConcurrentPoolsHammeringIt_ThenAccountingStaysExactAndNothingDeadlocks() {
    // pools hold independent locks so they never contend; the risk that buys is accounting drift
    // and lock-ordering, so both are asserted directly
    var cache = new PageCache(EvictionPolicy.Lru, 64);
    cache.SetBudget(64 * 500);

    const int workers = 8;
    const int rounds = 4000;
    var pools = new[] { _poolA, _poolB };
    var threads = Enumerable.Range(0, workers).Select(worker => new Thread(() => {
      var pool = pools[worker % pools.Length];
      for (var round = 0; round < rounds; ++round) {
        var key = new PageKey(pool, $"f{round % 50}.bin", round % 20);
        cache.Put(key, new byte[64]);
        cache.TryGet(key, out _);
        if (round % 32 == 0)
          cache.InvalidatePath(pool, $"f{round % 50}.bin");
        if (round % 512 == 0)
          cache.InvalidatePool(pool);
      }
    }) { IsBackground = true, Name = $"cache-{worker}" }).ToArray();

    foreach (var thread in threads)
      thread.Start();
    foreach (var thread in threads)
      thread.Join(TimeSpan.FromMinutes(1)).Should().BeTrue("no cache operation may deadlock");

    var perPool = pools.Sum(pool => cache.GetStatistics(pool).Bytes);
    cache.TotalBytes.Should().Be(perPool, "the global byte total must stay exactly the sum of the pools' occupancy");
    cache.TotalBytes.Should().BeLessThanOrEqualTo(cache.BudgetBytes, "eviction must keep the cache inside its budget");
    cache.TotalBytes.Should().BeGreaterThanOrEqualTo(0, "accounting must never go negative");
  }

  [Test]
  [Category("HappyPath")]
  public void TryGet_GivenPutBlock_WhenFetched_ThenHitWithSameBytes() {
    var cache = new PageCache(EvictionPolicy.Lru, 4);
    cache.SetBudget(1024);
    var key = new PageKey(_poolA, "f.bin", 0);
    cache.Put(key, [1, 2, 3, 4]);

    cache.TryGet(key, out var block).Should().BeTrue();
    block.Should().Equal(1, 2, 3, 4);
    cache.GetStatistics(_poolA).Hits.Should().Be(1);
  }

  [Test]
  [Category("EdgeCase")]
  public void Put_GivenBudgetExceeded_WhenInserting_ThenEvictsDownToBudget() {
    var cache = new PageCache(EvictionPolicy.Lru, 4);
    cache.SetBudget(8); // room for two 4-byte blocks
    cache.Put(new(_poolA, "f.bin", 0), new byte[4]);
    cache.Put(new(_poolA, "f.bin", 1), new byte[4]);
    cache.Put(new(_poolA, "f.bin", 2), new byte[4]);

    cache.TotalBytes.Should().BeLessThanOrEqualTo(8);
    cache.TryGet(new(_poolA, "f.bin", 0), out _).Should().BeFalse("the LRU block was evicted");
    cache.TryGet(new(_poolA, "f.bin", 2), out _).Should().BeTrue();
  }

  [Test]
  [Category("HappyPath")]
  public void Eviction_GivenTwoPoolsWithWeights_WhenOnePoolFloods_ThenVictimComesFromOverShareUser() {
    var cache = new PageCache(EvictionPolicy.Lru, 4);
    cache.SetBudget(64);
    cache.SetPoolWeight(_poolA, 1.0);
    cache.SetPoolWeight(_poolB, 1.0);

    // pool B holds a modest working set
    for (var i = 0; i < 4; ++i)
      cache.Put(new(_poolB, "b.bin", i), new byte[4]);

    // pool A floods far beyond its fair share
    for (var i = 0; i < 30; ++i)
      cache.Put(new(_poolA, "a.bin", i), new byte[4]);

    cache.GetStatistics(_poolB).Entries.Should().BeGreaterThan(0, "a busy pool must not fully starve another (FR-CACHE-GLOBAL)");
    cache.GetStatistics(_poolA).Bytes.Should().BeGreaterThan(cache.GetStatistics(_poolB).Bytes, "the flooding pool still gets the larger share");
  }

  [Test]
  [Category("HappyPath")]
  public void InvalidatePath_GivenCachedBlocks_WhenPathMutated_ThenAllItsBlocksDropped() {
    var cache = new PageCache(EvictionPolicy.Lru, 4);
    cache.SetBudget(1024);
    cache.Put(new(_poolA, "f.bin", 0), [1]);
    cache.Put(new(_poolA, "f.bin", 1), [2]);
    cache.Put(new(_poolA, "other.bin", 0), [3]);

    cache.InvalidatePath(_poolA, "f.bin");

    cache.TryGet(new(_poolA, "f.bin", 0), out _).Should().BeFalse("coherency requires dropping stale blocks (SAFE-COHERE)");
    cache.TryGet(new(_poolA, "f.bin", 1), out _).Should().BeFalse();
    cache.TryGet(new(_poolA, "other.bin", 0), out _).Should().BeTrue();
  }

  [Test]
  [Category("EdgeCase")]
  public void InvalidatePath_GivenBlocksAlreadyEvicted_WhenPathMutated_ThenAccountingStaysExact() {
    // the per-path index that makes invalidation O(blocks-of-the-path) must stay in exact step
    // with the block map through EVICTION too — a desync would either leak a stale block past
    // its invalidation (SAFE-COHERE) or corrupt the byte accounting the budget relies on
    var cache = new PageCache(EvictionPolicy.Lru, 4);
    cache.SetBudget(4);
    cache.Put(new(_poolA, "f.bin", 0), [1, 1]);
    cache.Put(new(_poolA, "f.bin", 1), [2, 2]);
    cache.Put(new(_poolA, "f.bin", 2), [3, 3]); // evicts block 0

    cache.TryGet(new(_poolA, "f.bin", 0), out _).Should().BeFalse("block 0 was evicted to stay within budget");
    cache.TotalBytes.Should().Be(4, "eviction must decrement the running total");

    cache.InvalidatePath(_poolA, "f.bin"); // must tolerate index entries for evicted blocks

    cache.TryGet(new(_poolA, "f.bin", 1), out _).Should().BeFalse();
    cache.TryGet(new(_poolA, "f.bin", 2), out _).Should().BeFalse();
    cache.TotalBytes.Should().Be(0, "every surviving block of the path is accounted for on the way out");
    cache.GetStatistics(_poolA).Bytes.Should().Be(0);

    // the path is reusable afterwards — a stale index entry would corrupt the next generation
    cache.Put(new(_poolA, "f.bin", 0), [9, 9]);
    cache.TryGet(new(_poolA, "f.bin", 0), out var reborn).Should().BeTrue();
    reborn.Should().Equal([9, 9]);
    cache.TotalBytes.Should().Be(2);
  }

  [Test]
  [Category("EdgeCase")]
  public void InvalidatePool_GivenCachedBlocks_WhenPoolDropped_ThenTheGlobalTotalFollows() {
    var cache = new PageCache(EvictionPolicy.Lru, 4);
    cache.SetBudget(1024);
    cache.Put(new(_poolA, "a.bin", 0), [1, 1]);
    cache.Put(new(_poolB, "b.bin", 0), [2, 2, 2]);
    cache.TotalBytes.Should().Be(5);

    cache.InvalidatePool(_poolA);

    cache.TotalBytes.Should().Be(3, "dropping a pool must remove exactly its own bytes from the shared budget");
    cache.TryGet(new(_poolB, "b.bin", 0), out _).Should().BeTrue("the other pool is untouched");
  }

  [Test]
  [Category("HappyPath")]
  public void TryGet_GivenAnotherThreadHoldsThePoolsLock_WhenACachedBlockIsFetched_ThenTheHitDoesNotWait() {
    // Measured: with the metadata lookups out of the way, twenty threads of cached reads spent most
    // of the read path queueing on the pool's cache lock — every hit took it to bump the counters
    // and the eviction order. A hit now reads a concurrent map and never waits for a writer.
    using var gate = new ManualResetEventSlim();
    var policy = new BlockingInsertPolicy<PageKey>(gate);
    var cache = new PageCache(() => policy, 4);
    cache.SetBudget(1024);
    cache.Put(new(_poolA, "cached.bin", 0), [1, 2, 3, 4]);
    policy.BlockInserts = true;

    var writer = Task.Run(() => cache.Put(new(_poolA, "slow.bin", 0), [5, 6, 7, 8])); // parks INSIDE the lock
    policy.InsertEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the writer must be holding the lock for this to test anything");
    try {
      var reader = Task.Run(() => cache.TryGet(new(_poolA, "cached.bin", 0), out var block) ? block : null);
      reader.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("a hit must not queue behind a writer holding the pool's lock");
      reader.Result.Should().Equal(1, 2, 3, 4);
    } finally {
      gate.Set();
      writer.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
    }

    cache.GetStatistics(_poolA).Hits.Should().Be(1, "a lock-free hit is still counted");
  }

  [Test]
  [Category("EdgeCase")]
  public void TryGet_GivenAPathWasInvalidated_WhenReadAfterTheInvalidationReturned_ThenTheOldBlockIsNeverServed() {
    // the coherence contract a lock-free hit must keep: once InvalidatePath has returned, no reader
    // can be handed the dropped bytes (SAFE-COHERE) — however many readers are racing it
    var cache = new PageCache(EvictionPolicy.Arc, 4);
    cache.SetBudget(1 << 20);
    var key = new PageKey(_poolA, "hot.bin", 0);
    var stale = new byte[] { 1, 1, 1, 1 };
    for (var round = 0; round < 200; ++round) {
      cache.Put(key, stale);
      using var stop = new CancellationTokenSource();
      var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() => {
        while (!stop.IsCancellationRequested)
          cache.TryGet(key, out _);
      })).ToArray();

      cache.InvalidatePath(_poolA, "hot.bin");
      var served = cache.TryGet(key, out var after);
      stop.Cancel();
      Task.WaitAll(readers);

      served.Should().BeFalse($"round {round}: an invalidated block was served after the invalidation returned");
      after.Should().NotBeSameAs(stale);
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void PageCache_GivenOnePoolHammeredByHitsPutsAndInvalidations_WhenQuiescent_ThenTheMapAndThePolicyAgree() {
    // Every change to the block map still pairs with its change to the policy under the pool's lock;
    // a hit's lock-free recency must never re-admit a key the map no longer holds. If the two drifted,
    // eviction could not reach every block and the cache would sit above its budget.
    const int block = 64;
    var cache = new PageCache(EvictionPolicy.Arc, block);
    cache.SetBudget(block * 100);
    var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
    var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() => {
      var random = new Random(worker);
      try {
        for (var i = 0; i < 20_000; ++i) {
          var file = random.Next(20);
          var key = new PageKey(_poolA, $"f{file}.bin", random.Next(20));
          switch (random.Next(10)) {
            case < 6:
              if (cache.TryGet(key, out var found) && found[0] != (byte)file)
                throw new InvalidOperationException($"{key.Path} served another file's block");
              break;
            case < 9:
              var bytes = new byte[block];
              bytes[0] = (byte)file;
              cache.Put(key, bytes);
              break;
            default:
              cache.InvalidatePath(_poolA, key.Path);
              break;
          }
        }
      } catch (Exception e) {
        failures.Enqueue(e);
      }
    })).ToArray();

    Task.WaitAll(workers, TimeSpan.FromSeconds(60)).Should().BeTrue("no deadlock");
    failures.Should().BeEmpty();
    cache.TotalBytes.Should().Be(cache.GetStatistics(_poolA).Bytes);
    cache.TotalBytes.Should().BeLessThanOrEqualTo(cache.BudgetBytes);

    for (var i = 0; i < 300; ++i)
      cache.Put(new(_poolA, "fresh.bin", i), new byte[block]);

    cache.TotalBytes.Should().BeLessThanOrEqualTo(cache.BudgetBytes, "every block the map holds is still known to the policy");
    for (var file = 0; file < 20; ++file)
      cache.InvalidatePath(_poolA, $"f{file}.bin");
    cache.InvalidatePath(_poolA, "fresh.bin");
    cache.GetStatistics(_poolA).Entries.Should().Be(0, "the per-path index reaches every block the map holds");
    cache.TotalBytes.Should().Be(0);
  }

}

/// <summary>An LRU whose inserts can be held open — a writer parked inside the owning cache's lock.</summary>
internal sealed class BlockingInsertPolicy<TKey>(ManualResetEventSlim gate) : ICacheEvictionPolicy<TKey> where TKey : class {
  private readonly ICacheEvictionPolicy<TKey> _inner = EvictionPolicyFactory.Create<TKey>(EvictionPolicy.Lru);
  public volatile bool BlockInserts;
  public readonly ManualResetEventSlim InsertEntered = new();

  public void OnAccess(TKey key) => this._inner.OnAccess(key);

  public void OnInsert(TKey key) {
    if (this.BlockInserts) {
      this.InsertEntered.Set();
      gate.Wait();
    }

    this._inner.OnInsert(key);
  }

  public TKey? SelectVictim() => this._inner.SelectVictim();
  public void Remove(TKey key) => this._inner.Remove(key);
  public int Count => this._inner.Count;
}

[TestFixture]
[Category("Unit")]
public class MetadataCacheTests {

  private static readonly Guid _pool = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
  private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

  private MetadataCache _Create(int maxEntries = 100, int ttlSeconds = 30)
    => new(EvictionPolicy.Lru, maxEntries, TimeSpan.FromSeconds(ttlSeconds), () => this._now);

  [Test]
  [Category("HappyPath")]
  public void TryGet_GivenFreshEntry_WhenFetched_ThenHit() {
    var cache = this._Create();
    cache.Put(new(_pool, "a/b", MetadataKind.Stat), new FileMeta(42, this._now, this._now, FileAttributes.Normal));

    cache.TryGet<FileMeta>(new(_pool, "a/b", MetadataKind.Stat), out var meta).Should().BeTrue();
    meta.Length.Should().Be(42);
  }

  [Test]
  [Category("EdgeCase")]
  public void TryGet_GivenExpiredEntry_WhenFetched_ThenMiss() {
    var cache = this._Create(ttlSeconds: 30);
    cache.Put(new(_pool, "a/b", MetadataKind.Stat), new FileMeta(42, this._now, this._now, FileAttributes.Normal));

    this._now += TimeSpan.FromSeconds(31);

    cache.TryGet<FileMeta>(new(_pool, "a/b", MetadataKind.Stat), out _).Should().BeFalse("TTL expired");
  }

  [Test]
  [Category("HappyPath")]
  public void InvalidatePath_GivenMutation_WhenInvalidated_ThenPathAndParentListingDropped() {
    var cache = this._Create();
    cache.Put(new(_pool, "docs/f.txt", MetadataKind.Stat), new FileMeta(1, this._now, this._now, FileAttributes.Normal));
    cache.Put(new(_pool, "docs", MetadataKind.DirectoryListing), new List<string> { "f.txt" });
    cache.Put(new(_pool, "docs", MetadataKind.Stat), new FileMeta(0, this._now, this._now, FileAttributes.Directory));

    cache.InvalidatePath(_pool, "docs/f.txt");

    cache.TryGet<FileMeta>(new(_pool, "docs/f.txt", MetadataKind.Stat), out _).Should().BeFalse();
    cache.TryGet<List<string>>(new(_pool, "docs", MetadataKind.DirectoryListing), out _).Should().BeFalse("the parent's listing is stale after a child mutation");
    cache.TryGet<FileMeta>(new(_pool, "docs", MetadataKind.Stat), out _).Should().BeTrue("the parent's own stat is unaffected");
  }

  [Test]
  [Category("EdgeCase")]
  public void Put_GivenMaxEntriesExceeded_WhenInserting_ThenEvicts() {
    var cache = this._Create(maxEntries: 3);
    for (var i = 0; i < 5; ++i)
      cache.Put(new(_pool, $"p{i}", MetadataKind.Stat), i);

    cache.Count.Should().BeLessThanOrEqualTo(3);
  }

  [Test]
  [Category("HappyPath")]
  public void TryGet_GivenAnLruCacheUsedFromOneThread_WhenAnEntryIsReadBeforeTheCacheOverflows_ThenTheReadEntrySurvives() {
    // a hit's recency is recorded only when the lock is free — which, with a single caller, is always
    var cache = this._Create(maxEntries: 2);
    cache.Put(new(_pool, "old", MetadataKind.Stat), 1);
    cache.Put(new(_pool, "newer", MetadataKind.Stat), 2);

    cache.TryGet<int>(new(_pool, "old", MetadataKind.Stat), out _).Should().BeTrue();
    cache.Put(new(_pool, "newest", MetadataKind.Stat), 3);

    cache.TryGet<int>(new(_pool, "old", MetadataKind.Stat), out _).Should().BeTrue("it was used last, so LRU keeps it");
    cache.TryGet<int>(new(_pool, "newer", MetadataKind.Stat), out _).Should().BeFalse("the least recently used entry is the one evicted");
  }

  [Test]
  [Category("EdgeCase")]
  public void TryGet_GivenAnEntryOfAnotherType_WhenFetched_ThenMissAndTheEntryIsDropped() {
    var cache = this._Create();
    cache.Put(new(_pool, "a", MetadataKind.Stat), "not a FileMeta");

    cache.TryGet<FileMeta>(new(_pool, "a", MetadataKind.Stat), out _).Should().BeFalse();
    cache.Count.Should().Be(0, "an entry that cannot answer for its key is removed, as an expired one is");
  }

  [Test]
  [Category("HappyPath")]
  public void TryGet_GivenAnotherThreadHoldsTheCacheLock_WhenACachedEntryIsFetched_ThenTheHitDoesNotWait() {
    // Measured: every read looks up the placement AND the stat here, and under twenty threads of
    // cached reads 80% of the read path was queueing on this one lock. A hit reads a concurrent map
    // and records its recency only when the lock is free, so it never waits for a writer.
    using var gate = new ManualResetEventSlim();
    var policy = new BlockingInsertPolicy<MetadataKey>(gate);
    var cache = new MetadataCache(policy, 100, TimeSpan.FromMinutes(1), () => this._now);
    cache.Put(new(_pool, "cached", MetadataKind.Stat), 7);
    policy.BlockInserts = true;

    var writer = Task.Run(() => cache.Put(new(_pool, "slow", MetadataKind.Stat), 8)); // parks INSIDE the lock
    policy.InsertEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the writer must be holding the lock for this to test anything");
    try {
      var reader = Task.Run(() => cache.TryGet<int>(new(_pool, "cached", MetadataKind.Stat), out var value) ? value : -1);
      reader.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("a hit must not queue behind a writer holding the lock");
      reader.Result.Should().Be(7);
    } finally {
      gate.Set();
      writer.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
    }

    cache.TryGet<int>(new(_pool, "slow", MetadataKind.Stat), out var slow).Should().BeTrue();
    slow.Should().Be(8);
  }

  [Test]
  [Category("EdgeCase")]
  public void Cache_GivenConcurrentHitsPutsInvalidationsAndEvictions_WhenQuiescent_ThenTheBoundStillHolds() {
    // The map and the policy must stay in exact step whatever interleaving the lock-free hit sees:
    // a key the policy forgot can never be chosen as a victim, so the cache would outgrow its bound.
    const int maxEntries = 64;
    var cache = new MetadataCache(EvictionPolicy.Slru, maxEntries, TimeSpan.FromMinutes(1));
    var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
    var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() => {
      var random = new Random(worker);
      try {
        for (var i = 0; i < 20_000; ++i) {
          var key = new MetadataKey(_pool, $"p{random.Next(200)}", MetadataKind.Stat);
          switch (random.Next(10)) {
            case < 6:
              if (cache.TryGet<int>(key, out var value) && value != int.Parse(key.Path[1..]))
                throw new InvalidOperationException($"{key.Path} answered {value}");
              break;
            case < 9:
              cache.Put(key, int.Parse(key.Path[1..]));
              break;
            default:
              cache.InvalidatePath(_pool, key.Path);
              break;
          }
        }
      } catch (Exception e) {
        failures.Enqueue(e);
      }
    })).ToArray();

    Task.WaitAll(workers, TimeSpan.FromSeconds(60)).Should().BeTrue("no deadlock");
    failures.Should().BeEmpty();
    cache.Count.Should().BeLessThanOrEqualTo(maxEntries);

    for (var i = 0; i < maxEntries * 2; ++i)
      cache.Put(new(_pool, $"fresh{i}", MetadataKind.Stat), i);

    cache.Count.Should().Be(maxEntries, "every entry the map holds is still known to the policy, so eviction reaches all of them");
  }

}

[TestFixture]
[Category("Unit")]
public class CacheInstanceTests {

  [Test]
  [Category("HappyPath")]
  public void SharedAuto_GivenWriteBurst_WhenReserving_ThenBudgetShiftsTowardWritesButReadNeverStarves() {
    var instance = new CacheInstance("t", new() { Size = "1000", Split = new() { Mode = CacheSplitMode.SharedAuto } });
    var initialRead = instance.ReadCacheMax;

    instance.TryReserveWrite(700).Should().BeTrue("a write burst may grow the write side under shared-auto");
    instance.ReadCacheMax.Should().BeLessThan(initialRead);
    instance.ReadCacheMax.Should().BeGreaterThanOrEqualTo(100, "the read side may never be starved to zero (§6.5A)");

    instance.TryReserveWrite(10_000).Should().BeFalse("the safety bound caps the write side");
  }

  [Test]
  [Category("HappyPath")]
  public void SharedAuto_GivenWriteDrain_WhenReleased_ThenReadBudgetGrowsBack() {
    var instance = new CacheInstance("t", new() { Size = "1000", Split = new() { Mode = CacheSplitMode.SharedAuto } });
    instance.TryReserveWrite(700);
    var squeezed = instance.ReadCacheMax;

    instance.ReleaseWrite(700);

    instance.ReadCacheMax.Should().BeGreaterThan(squeezed, "a read-heavy phase reclaims the RAM");
  }

  [Test]
  [Category("HappyPath")]
  public void SharedFixed_GivenWritePressure_WhenReserving_ThenBoundaryDoesNotMove() {
    var instance = new CacheInstance("t", new() {
      Size = "1000",
      Split = new() { Mode = CacheSplitMode.SharedFixed, Read = "70%", Write = "30%" },
    });

    instance.ReadCacheMax.Should().Be(700);
    instance.WriteBufferMax.Should().Be(300);
    instance.TryReserveWrite(300).Should().BeTrue();
    instance.TryReserveWrite(1).Should().BeFalse("shared-fixed holds the boundary regardless of load");
    instance.ReadCacheMax.Should().Be(700);
  }

  [Test]
  [Category("HappyPath")]
  public void Separate_GivenWriteFlood_WhenReserving_ThenReadCapUntouched() {
    var instance = new CacheInstance("t", new() {
      Split = new() { Mode = CacheSplitMode.Separate, ReadCacheMax = "512", WriteBufferMax = "256" },
    });

    instance.TryReserveWrite(256).Should().BeTrue();
    instance.TryReserveWrite(1).Should().BeFalse();
    instance.ReadCacheMax.Should().Be(512, "a write flood can't shrink the read cache (FR-CACHE-SPLIT separate)");
    instance.SizeBytes.Should().Be(768);
  }

  [Test]
  [Category("Exception")]
  public void ReserveRelease_GivenBackpressureAtCap_WhenReserving_ThenCallerToldToBlock() {
    var instance = new CacheInstance("t", new() {
      Split = new() { Mode = CacheSplitMode.Separate, ReadCacheMax = "64", WriteBufferMax = "64" },
    });

    instance.TryReserveWrite(64).Should().BeTrue();
    instance.TryReserveWrite(1).Should().BeFalse("at the hard cap the writer must block or degrade, never grow (FR-BACKP)");
    instance.ReleaseWrite(32);
    instance.TryReserveWrite(32).Should().BeTrue();
  }

}

[TestFixture]
[Category("Unit")]
public class CacheHostTests {

  [Test]
  [Category("Exception")]
  public void CreateInstance_GivenCeilingWouldBeExceeded_WhenCreating_ThenRefused() {
    var host = new CacheHost(maxTotalBytes: 1000);
    host.CreateInstance("global", new() { Size = "800" });

    var act = () => host.CreateInstance("dedicated", new() { Size = "300" });
    act.Should().Throw<ConfigValidationException>().WithMessage("*over-commit*", "the host RAM ceiling is never over-committed (SAFE-RAM-BUDGET)");
  }

  [Test]
  [Category("HappyPath")]
  public void AttachPool_GivenDedicatedConfig_WhenAttached_ThenPrivateInstanceCreatedWithinCeiling() {
    var host = new CacheHost(maxTotalBytes: 1000);
    host.CreateInstance("global", new() { Size = "500" });
    var poolId = Guid.NewGuid();

    var instance = host.AttachPool(poolId, new() { Dedicated = new() { Size = "400" } });

    instance.Name.Should().Contain(poolId.ToString("D"));
    host.TotalCommittedBytes.Should().Be(900);
    host.GetPoolCache(poolId).Should().BeSameAs(instance);
  }

  [Test]
  [Category("HappyPath")]
  public void AttachPool_GivenNoConfig_WhenAttached_ThenSharedGlobalInstanceUsed() {
    var host = new CacheHost(maxTotalBytes: 1000);
    var global = host.CreateInstance("global", new() { Size = "500" });
    var poolId = Guid.NewGuid();

    host.AttachPool(poolId, null).Should().BeSameAs(global);
  }

}
