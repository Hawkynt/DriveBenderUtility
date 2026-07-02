using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Metrics and the sampled activity feed (OPS-METRICS, OPS-EVENTS, NFR-UI-LIVE).</summary>
[TestFixture]
[Category("Unit")]
public class ObservabilityTests {

  private DateTime _now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

  [Test]
  [Category("HappyPath")]
  public void Publish_GivenSubscriber_WhenEventsFlow_ThenDeliveredWithPayload() {
    var feed = new ActivityFeed(clock: () => this._now);
    var received = new List<ActivityEvent>();
    feed.EventPublished += received.Add;

    feed.Publish(ActivityKind.Drain, "movies/film.mkv", 1024, "ssd", "hdd1", "landing-zone drain");

    received.Should().ContainSingle();
    received[0].Kind.Should().Be(ActivityKind.Drain);
    received[0].FromMember.Should().Be("ssd");
    received[0].ToMember.Should().Be("hdd1");
    received[0].Bytes.Should().Be(1024);
  }

  [Test]
  [Category("EdgeCase")]
  public void Publish_GivenEventFlood_WhenRateLimited_ThenSamplesDropRatherThanBlock() {
    var feed = new ActivityFeed(maxEventsPerSecond: 10, clock: () => this._now);
    for (var i = 0; i < 100; ++i)
      feed.Publish(ActivityKind.Read, $"f{i}.bin", 1);

    feed.DroppedSamples.Should().Be(90, "the feed is server-side rate-limited (OPS-EVENTS)");
    feed.History.Count.Should().Be(10);

    this._now += TimeSpan.FromSeconds(1);
    feed.Publish(ActivityKind.Read, "later.bin", 1);
    feed.History.Should().Contain(e => e.Path == "later.bin", "a new window admits samples again");
  }

  [Test]
  [Category("EdgeCase")]
  public void History_GivenRingCapacity_WhenExceeded_ThenOldestEventsRollOff() {
    var feed = new ActivityFeed(ringCapacity: 3, maxEventsPerSecond: 1000, clock: () => this._now);
    for (var i = 0; i < 5; ++i)
      feed.Publish(ActivityKind.Write, $"f{i}", 1);

    feed.History.Select(e => e.Path).Should().Equal("f2", "f3", "f4");
  }

  [Test]
  [Category("HappyPath")]
  public void GetMetrics_GivenEngineTraffic_WhenSnapshotted_ThenCountersReflectActivity() {
    var volume1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    var volume2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    var cache = new CacheInstance("o" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 100, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(Guid.NewGuid(), [new(volume1), new(volume2)], cache, ConfigResolver.ResolveEffective(null, """{ "duplication": 2 }"""));
    fs.Mount(new(@"X:\"));

    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1, 2, 3, 4], 0, WriteMode.Normal);
    var buffer = new byte[4];
    fs.Read(handle, buffer, 0);
    fs.Read(handle, buffer, 0); // second read hits the page cache
    fs.Close(handle);

    var metrics = fs.GetMetrics();
    metrics.WrittenBytes.Should().Be(4);
    metrics.ReadBytes.Should().Be(8);
    metrics.CacheHits.Should().BeGreaterThan(0);
    metrics.CacheHitRate.Should().BeGreaterThan(0);
    fs.Activity.History.Should().Contain(e => e.Kind == ActivityKind.Write && e.Path == "f.bin");
  }

}
