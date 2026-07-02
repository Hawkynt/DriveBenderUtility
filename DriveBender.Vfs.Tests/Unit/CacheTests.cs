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
