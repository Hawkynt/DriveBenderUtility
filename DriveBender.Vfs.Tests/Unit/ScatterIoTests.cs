using System.Diagnostics;
using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The storages behind a tier have to work AT THE SAME TIME, and a storage that has stopped
/// working must stop being asked first.
///
/// Both are properties of overlap and ordering, neither of which is visible in the bytes that
/// come back — a strictly serial engine returns exactly the same content as a fan-out one, and
/// an engine that retries a dead disk once per block returns the right answer too, just far too
/// late. So these drive the engine through a member that COUNTS what happens to it.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ScatterIoTests {

  private static readonly Guid _pool = Guid.Parse("5ca77e40-0000-4000-8000-00000000d10a");

  private const int _BLOCK = 64;
  private const int _BLOCKS = 48;
  private const int _SIZE = _BLOCK * _BLOCKS;

  /// <summary>Long enough that a serial loop cannot fake an overlap, short enough not to slow the suite.</summary>
  private static readonly TimeSpan _READ_DELAY = TimeSpan.FromMilliseconds(4);

  private static byte[] _Content(int size) {
    var content = new byte[size];
    for (var i = 0; i < size; ++i)
      content[i] = (byte)(i % 251);

    return content;
  }

  private static CacheInstance _Cache() => new(
    "scatter" + Guid.NewGuid().ToString("N"),
    new() { Size = "1048576", BlockSize = _BLOCK.ToString(), MetadataEntries = 200, MetadataTtl = "5m" });

  /// <summary>
  /// A mounted pool over probe-wrapped members holding <paramref name="path"/> already, with
  /// read-ahead OFF so what the test measures is the read it issued and not a background window.
  /// </summary>
  private static (PoolFileSystem fs, ProbeVolumeIO[] probes, FakeVolumeIO[] fakes) _Mount(
    int members, string path, byte[] content, string? io = null, int duplication = 1, TimeSpan? delay = null,
    string? readAhead = null) {
    var fakes = new FakeVolumeIO[members];
    var probes = new ProbeVolumeIO[members];
    for (var index = 0; index < members; ++index) {
      fakes[index] = new(Guid.NewGuid(), $"v{index}", $"PHYS-{index}", capacity: 1L << 30);
      probes[index] = new(fakes[index], delay ?? _READ_DELAY);
    }

    // every member holds a copy, so a duplicated pool has somewhere to read from on each of them
    var shadow = false;
    foreach (var fake in fakes) {
      fake.Seed(path, shadow, content);
      shadow = duplication > 1; // the first is the primary, the rest are its shadow copies
    }

    var json = $$"""
      {
        "duplication": {{duplication}},
        "readAhead": {{readAhead ?? """{ "enabled": false }"""}}{{(io == null ? "" : ", \"io\": " + io)}}
      }
      """;
    var fs = new PoolFileSystem(_pool, [.. probes.Select(p => new EngineMember(p))], _Cache(), ConfigResolver.ResolveEffective(null, json));
    fs.Mount(new(@"X:\"));
    foreach (var probe in probes)
      probe.ResetCounters(); // the mount's own scan is not what is being measured

    return (fs, probes, fakes);
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenItSpansManyBlocks_ThenTheStorageIsAskedForSeveralAtOnce() {
    var content = _Content(_SIZE);
    var (fs, probes, _) = _Mount(1, "big.bin", content);
    using var _ = fs;

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[_SIZE];
    fs.Read(handle, buffer, 0).Should().Be(_SIZE);

    buffer.Should().Equal(content, "overlapping the block loads must not change a single byte");
    probes[0].PeakConcurrentReads.Should().BeGreaterThan(1,
      "a read spanning {0} blocks left the storage at queue depth one — that is the single easiest "
      + "way to leave most of a device's throughput unused", _BLOCKS);
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenTwoStoragesHoldTheFile_ThenBothServeItAtOnce() {
    var content = _Content(_SIZE);
    // above the mirror-split threshold, so the read is allowed to spread across the copies
    var (fs, probes, _) = _Mount(2, "big.bin", content, io: """{ "mirrorReadSplitThreshold": "1" }""", duplication: 2);
    using var _ = fs;

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[_SIZE];
    fs.Read(handle, buffer, 0).Should().Be(_SIZE);

    buffer.Should().Equal(content);
    probes.Sum(p => p.ReadAttempts).Should().BeGreaterThan(0);
    probes.Count(p => p.ReadAttempts > 0).Should().Be(2,
      "both storages hold the file, so both should be carrying part of the read — that is what "
      + "makes two disks in a tier add up rather than take turns");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenQueueDepthOne_ThenTheStorageIsNeverAskedForTwoAtOnce() {
    var content = _Content(_SIZE);
    var (fs, probes, _) = _Mount(1, "big.bin", content, io: """{ "queueDepthPerVolume": { "default": 1 } }""");
    using var _ = fs;

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[_SIZE];
    fs.Read(handle, buffer, 0).Should().Be(_SIZE);

    buffer.Should().Equal(content, "a capped queue still has to return every byte");
    probes[0].PeakConcurrentReads.Should().Be(1,
      "io.queueDepthPerVolume is what a user sets when a device is hurt rather than helped by a "
      + "deep queue — a spindle, a throttled share. It has to actually bind");
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenQueueDepthTwo_ThenTheFanOutStopsAtTwo() {
    var content = _Content(_SIZE);
    var (fs, probes, _) = _Mount(1, "big.bin", content, io: """{ "queueDepthPerVolume": { "default": 2 } }""");
    using var _ = fs;

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[_SIZE];
    fs.Read(handle, buffer, 0);

    probes[0].PeakConcurrentReads.Should().Be(2, "the configured depth is a cap AND a target");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenACopyThatHasStartedFailing_ThenItIsNotRetriedForEveryBlock() {
    var content = _Content(_SIZE);
    var (fs, probes, fakes) = _Mount(2, "big.bin", content,
      io: """{ "queueDepthPerVolume": { "default": 2 } }""", duplication: 2);
    using var _ = fs;

    fakes[0].AlwaysFail(VolumeOp.OpenRead); // the disk is dying: every read against it errors

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[_SIZE];
    fs.Read(handle, buffer, 0).Should().Be(_SIZE);

    buffer.Should().Equal(content, "the surviving copy serves the whole read");
    probes[0].ReadAttempts.Should().BeLessThan(_BLOCKS / 2,
      "a failing storage was asked {0} times for a {1}-block read. On a real dying disk each of "
      + "those is a driver timeout measured in seconds, so retrying it first for every block turns "
      + "one bad member into a pool-wide stall while a healthy copy sits beside it",
      probes[0].ReadAttempts, _BLOCKS);
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenEveryCopyFails_ThenItStillReportsTheError() {
    var content = _Content(_SIZE);
    var (fs, _, fakes) = _Mount(2, "big.bin", content, duplication: 2);
    using var __ = fs;

    foreach (var fake in fakes)
      fake.AlwaysFail(VolumeOp.OpenRead);

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var act = () => fs.Read(handle, new byte[_SIZE], 0);

    act.Should().Throw<PoolFsException>(
      "routing around a failure is not the same as swallowing one — with nowhere left to route, "
      + "the caller must be told rather than handed short or stale bytes");
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenASingleBlock_ThenNothingIsFannedOut() {
    var content = _Content(_SIZE);
    var (fs, probes, _) = _Mount(1, "big.bin", content, delay: TimeSpan.Zero);
    using var _ = fs;

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    fs.Read(handle, new byte[_BLOCK], 0).Should().Be(_BLOCK);

    probes[0].ReadAttempts.Should().Be(1,
      "a read inside one block must cost exactly one; fanning out is for requests that span blocks, "
      + "and paying for a parallel loop to schedule a single call is pure overhead");
  }

  /// <summary>Waits until the member stops being asked for anything — read-ahead runs on background threads.</summary>
  private static int _SettledReadAttempts(ProbeVolumeIO probe) {
    var previous = -1;
    for (var poll = 0; poll < 100; ++poll) {
      var now = probe.ReadAttempts;
      if (now == previous)
        return now;

      previous = now;
      Thread.Sleep(50);
    }

    return probe.ReadAttempts;
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenManyThreadsWantTheSameUncachedBlock_ThenTheStorageIsAskedOnce() {
    const int readers = 8;
    var content = _Content(_SIZE);
    // a slow open, so every reader is genuinely inside the same miss at the same moment
    var (fs, probes, _) = _Mount(1, "big.bin", content, delay: TimeSpan.FromMilliseconds(60));
    using var _ = fs;

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var start = new ManualResetEventSlim(false);
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    var threads = Enumerable.Range(0, readers).Select(_ => new Thread(() => {
      try {
        start.Wait(TimeSpan.FromSeconds(30));
        var got = new byte[_BLOCK];
        fs.Read(handle, got, 0);
        if (!got.AsSpan().SequenceEqual(content.AsSpan(0, _BLOCK)))
          failures.Add("a shared block came back with the wrong bytes");
      } catch (Exception e) {
        failures.Add($"{e.GetType().Name}: {e.Message}");
      }
    }) { IsBackground = true }).ToArray();

    foreach (var thread in threads)
      thread.Start();

    Thread.Sleep(50); // everyone parked on the gate
    start.Set();
    foreach (var thread in threads)
      thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("a reader must not hang");

    failures.Should().BeEmpty();
    probes[0].ReadAttempts.Should().Be(1,
      "{0} readers all wanted the same uncached block at the same moment. One of them fetches it and "
      + "the rest wait for that fetch; asking the storage {1} times for one block is the overhead a "
      + "cache exists to remove, and it is exactly what a reader outrunning its own read-ahead does",
      readers, probes[0].ReadAttempts);
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenReadAheadIsRunning_ThenEveryBlockLeavesTheStorageExactlyOnce() {
    var content = _Content(_SIZE);
    var (fs, probes, _) = _Mount(1, "big.bin", content,
      // small windows so the read-ahead runs MANY chains over the file rather than one covering it
      readAhead: """{ "enabled": true, "minWindow": "128", "maxWindow": "512", "adaptive": true }""",
      delay: TimeSpan.FromMilliseconds(2));
    using var _ = fs;

    var handle = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var got = new byte[_SIZE];
    for (var offset = 0; offset < _SIZE; offset += _BLOCK)
      fs.Read(handle, got.AsSpan(offset, _BLOCK), offset).Should().Be(_BLOCK);

    got.Should().Equal(content, "read-ahead must not change what the file reads as");

    _SettledReadAttempts(probes[0]).Should().Be(_BLOCKS,
      "a {0}-block file read from end to end must cost {0} trips to the storage and no more. Anything "
      + "above that is the read-ahead and the reader — or two read-ahead chains — fetching the same "
      + "block twice, which is the read-ahead paying for itself twice over",
      _BLOCKS);
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenAWriteLandsBetweenReads_ThenTheNewBytesAreServed() {
    var content = _Content(_SIZE);
    var (fs, _, _) = _Mount(1, "big.bin", content,
      readAhead: """{ "enabled": true, "minWindow": "128", "maxWindow": "512", "adaptive": true }""",
      delay: TimeSpan.FromMilliseconds(2));
    using var __ = fs;

    // walk far enough in to have read-ahead chains in flight over the blocks ahead
    var reader = fs.Open("big.bin", AccessMode.Read, ShareMode.Read);
    var scratch = new byte[_BLOCK];
    for (var offset = 0; offset < _SIZE / 2; offset += _BLOCK)
      fs.Read(reader, scratch, offset);

    // …then overwrite a block the read-ahead is plausibly holding, and read it straight back
    var replacement = new byte[_BLOCK];
    Array.Fill(replacement, (byte)0xC3);
    var target = _SIZE - _BLOCK * 4;
    var writer = fs.Open("big.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
    fs.Write(writer, replacement, target, WriteMode.Normal);

    var after = new byte[_BLOCK];
    fs.Read(reader, after, target);

    after.Should().Equal(replacement,
      "sharing an in-flight block load between callers must never hand back bytes a completed write "
      + "has already replaced — a stale read is the one failure this optimisation could introduce");
  }

}

/// <summary>
/// The whole-file transfer that drain, heal and media moves all run on. It reads from one
/// storage and writes to another, so whether it OVERLAPS them decides whether a tiered pool
/// moves data at the speed of the slower device or at the sum of two half-idle ones.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DoubleBufferedCopyTests {

  /// <summary>
  /// Watches the two devices for the thing that actually matters: was a READ under way while a
  /// WRITE was happening?
  ///
  /// Timing this instead — asserting the transfer beats some fraction of its serial cost — looks
  /// equivalent and is not. The bound has to come from somewhere, and deriving it from the sleep
  /// the test asked for assumes the host delivers that sleep: Windows' default timer granularity
  /// is about 15.6 ms, so every "10 ms" is really 15.6, the whole run inflates, and a perfectly
  /// overlapped copy lands the wrong side of a threshold computed from the nominal figure. That is
  /// a statement about the runner's clock, not about the copy. Observing the overlap needs no
  /// clock and no threshold.
  /// </summary>
  private sealed class DeviceProbe {
    private int _readsInFlight;
    public volatile bool SawReadDuringWrite;

    public void ReadStarted() => Interlocked.Increment(ref this._readsInFlight);
    public void ReadFinished() => Interlocked.Decrement(ref this._readsInFlight);

    /// <summary>Spends <paramref name="duration"/> in a write, sampling whether the other device is busy.</summary>
    public void WriteTakingTime(TimeSpan duration) {
      for (var slice = 0; slice < 20; ++slice) {
        if (Volatile.Read(ref this._readsInFlight) > 0)
          this.SawReadDuringWrite = true;

        Thread.Sleep(duration / 20);
      }
    }
  }

  private sealed class SlowSource(Stream inner, TimeSpan perRead, DeviceProbe probe) : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) {
      probe.ReadStarted();
      try {
        Thread.Sleep(perRead);
        return inner.Read(buffer, offset, count);
      } finally {
        probe.ReadFinished();
      }
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

  private sealed class SlowDestination(Stream inner, TimeSpan perWrite, DeviceProbe probe) : Stream {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Write(byte[] buffer, int offset, int count) {
      probe.WriteTakingTime(perWrite);
      inner.Write(buffer, offset, count);
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

  private static byte[] _Content(int size) {
    var content = new byte[size];
    new Random(4711).NextBytes(content);
    return content;
  }

  [Test]
  [Category("HappyPath")]
  public void CopyCounted_GivenAMultiChunkSource_ThenEveryByteArrivesInOrder() {
    var content = _Content(9000);
    using var source = new MemoryStream(content, writable: false);
    using var destination = new MemoryStream();

    var copied = WholeFilePublisher.CopyCounted(source, destination, bufferSize: 1024);

    copied.Should().Be(content.LongLength);
    destination.ToArray().Should().Equal(content,
      "double-buffering swaps two arrays around per chunk, and getting that swap wrong corrupts "
      + "the copy in a way only a byte-for-byte comparison catches");
  }

  [Test]
  [Category("EdgeCase")]
  public void CopyCounted_GivenSizesAroundTheChunkBoundary_ThenEachRoundTripsExactly() {
    foreach (var size in new[] { 0, 1, 1023, 1024, 1025, 2048, 2049 }) {
      var content = _Content(size);
      using var source = new MemoryStream(content, writable: false);
      using var destination = new MemoryStream();

      WholeFilePublisher.CopyCounted(source, destination, bufferSize: 1024).Should().Be(size);
      destination.ToArray().Should().Equal(content, "a {0}-byte copy must be exact", size);
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void CopyCounted_GivenADribblingSource_ThenTheShortReadsAreNotMistakenForTheEnd() {
    var content = _Content(5000);
    // a stream that hands back a few bytes at a time — a network source, and the classic way a
    // copy loop silently truncates by treating a short read as EOF
    using var source = new DribblingStream(content, 7);
    using var destination = new MemoryStream();

    WholeFilePublisher.CopyCounted(source, destination, bufferSize: 1024).Should().Be(content.Length,
      "one Read may return fewer bytes than asked for at any point; a copy that stops there loses "
      + "the rest of the file and reports success");
    destination.ToArray().Should().Equal(content);
  }

  private sealed class DribblingStream(byte[] content, int perRead) : Stream {
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => this._position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) {
      var take = Math.Min(Math.Min(perRead, count), content.Length - this._position);
      Array.Copy(content, this._position, buffer, offset, take);
      this._position += take;
      return take;
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

  [Test]
  [Category("HappyPath")]
  public void CopyCounted_GivenSlowSourceAndSlowDestination_ThenTheyOverlap() {
    const int chunk = 1024;
    const int chunks = 8;
    var content = _Content(chunk * chunks);
    var probe = new DeviceProbe();

    // the source is deliberately the slower of the two, so a read is genuinely still running while
    // a write is in progress rather than the two merely brushing past one another
    using var source = new SlowSource(new MemoryStream(content, writable: false), TimeSpan.FromMilliseconds(40), probe);
    using var destination = new SlowDestination(new MemoryStream(), TimeSpan.FromMilliseconds(20), probe);

    WholeFilePublisher.CopyCounted(source, destination, bufferSize: chunk).Should().Be(content.LongLength);

    probe.SawReadDuringWrite.Should().BeTrue(
      "a drain reads from one storage and writes to another. With a single buffer the two take "
      + "turns and each sits idle for the other's half of every chunk, so a read is NEVER under way "
      + "while a write is — which is exactly what this looks for, and what double-buffering changes");
  }

  [Test]
  [Category("Exception")]
  public void CopyCounted_GivenTheDestinationFails_ThenTheFailureSurfacesAndNothingIsLeftInFlight() {
    var content = _Content(8192);
    using var source = new TrackingSource(content, TimeSpan.FromMilliseconds(30));
    using var destination = new FailingStream();

    var act = () => WholeFilePublisher.CopyCounted(source, destination, bufferSize: 1024);

    act.Should().Throw<IOException>("a failed write is the caller's to hear about");

    // The read-ahead OWNS a rented buffer while it runs. If the copy returned without waiting for
    // it, that array would go back to the pool with a thread still writing into it, and the next
    // renter would get memory under active mutation — the one defect a double-buffered copy can
    // introduce that a single-buffered one cannot.
    source.Abandoned = true;
    Thread.Sleep(150);
    source.ReadAfterAbandon.Should().BeFalse("the copy must not still be reading once it has thrown");
  }

  private sealed class TrackingSource(byte[] content, TimeSpan perRead) : Stream {
    private int _position;
    public volatile bool Abandoned;
    public volatile bool ReadAfterAbandon;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => this._position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) {
      Thread.Sleep(perRead);
      if (this.Abandoned)
        this.ReadAfterAbandon = true;

      var take = Math.Min(count, content.Length - this._position);
      Array.Copy(content, this._position, buffer, offset, take);
      this._position += take;
      return take;
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

  private sealed class FailingStream : Stream {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => throw new IOException("the disk went away");
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

}

/// <summary>The queue-depth resolution itself: which key wins, and what an unconfigured pool gets.</summary>
[TestFixture]
[Category("Unit")]
public class VolumeQueueTests {

  private static FakeVolumeIO _Member(string name, string physical) => new(Guid.NewGuid(), name, physical, capacity: 1L << 30);

  [Test]
  [Category("HappyPath")]
  public void DepthFor_GivenAMemberNamedInTheConfig_ThenThatWinsOverItsRole() {
    var member = _Member("slow-usb", "PHYS-A");
    var config = ConfigResolver.ResolveEffective(null,
      """{ "io": { "queueDepthPerVolume": { "slow-usb": 1, "capacity": 9, "default": 7 } } }""");
    var queues = new VolumeQueues(config, new Dictionary<Guid, MemberRole> { [member.MemberId] = MemberRole.Capacity });

    queues.DepthFor(member).Should().Be(1, "naming one troublesome disk has to beat naming its kind");
  }

  [Test]
  [Category("HappyPath")]
  public void DepthFor_GivenOnlyARoleInTheConfig_ThenTheRoleApplies() {
    var member = _Member("ssd0", "PHYS-B");
    var config = ConfigResolver.ResolveEffective(null,
      """{ "io": { "queueDepthPerVolume": { "landing": 12, "default": 3 } } }""");
    var queues = new VolumeQueues(config, new Dictionary<Guid, MemberRole> { [member.MemberId] = MemberRole.Landing });

    queues.DepthFor(member).Should().Be(12);
  }

  [Test]
  [Category("EdgeCase")]
  public void DepthFor_GivenRotatingMedia_ThenTheDefaultIsShallow() {
    var member = _Member("archive", "PHYS-SPINDLE");
    MediaProbe.Override("PHYS-SPINDLE", MediaClass.Rotational);
    var queues = new VolumeQueues(ConfigResolver.ResolveEffective(null, null), new Dictionary<Guid, MemberRole>());

    queues.DepthFor(member).Should().Be(2,
      "§6.4: a spindle serves a deep queue by seeking between the requests, which is slower than "
      + "serving them in the order they arrived");
  }

  [Test]
  [Category("EdgeCase")]
  public void FanOutFor_GivenTwoMembersOnOneSpindle_ThenTheyCountAsOneQueue() {
    var first = _Member("part1", "PHYS-SHARED");
    var second = _Member("part2", "PHYS-SHARED");
    var separate = _Member("other", "PHYS-OTHER");
    var config = ConfigResolver.ResolveEffective(null, """{ "io": { "queueDepthPerVolume": { "default": 4 } } }""");
    var queues = new VolumeQueues(config, new Dictionary<Guid, MemberRole>());

    queues.FanOutFor([new(first, false), new(second, true)]).Should().Be(4,
      "two members carved out of one disk are one queue however the manifest lists them");
    queues.FanOutFor([new(first, false), new(separate, true)]).Should().Be(8,
      "two independent devices are what actually add up");
  }

}
