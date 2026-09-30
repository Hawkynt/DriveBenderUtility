using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Write-buffer coherence and cache-key consistency: the buffer never mutates the shared page
/// array in place, a full-coverage write supersedes obsolete buffered bytes so they cannot
/// flush back over newer data, and cache keys match the engine's case-insensitive path model.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WriteBufferCoherenceTests {

  private CacheInstance _cache = null!;
  private WriteBufferManager _buffer = null!;

  [SetUp]
  public void SetUp() {
    this._cache = new("wb" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 100, MetadataTtl = "5m" });
    this._buffer = new(this._cache);
  }

  [Test]
  [Category("EdgeCase")]
  public void OverlayBlock_GivenDirtyFile_WhenApplied_ThenInputArrayIsNotMutated() {
    this._buffer.StageWrite("f.bin", 2, [9, 9], 0, 1);
    var cached = new byte[] { 1, 2, 3, 4, 5, 6 };
    var snapshot = (byte[])cached.Clone();

    var result = this._buffer.OverlayBlock("f.bin", 0, 16, cached);

    cached.Should().Equal(snapshot, "the shared page-cache array must never be mutated in place");
    result.Should().Equal(new byte[] { 1, 2, 9, 9, 5, 6 }, "the overlay is applied to a copy");
  }

  [Test]
  [Category("HappyPath")]
  public void Supersede_GivenLaterFullCoverageWrite_WhenApplied_ThenObsoleteBufferedBytesDropped() {
    this._buffer.StageWrite("f.bin", 0, [1, 1, 1, 1], 0, 1); // owed
    this._buffer.IsDirty("f.bin").Should().BeTrue();

    // a later write made offsets 0..4 durable on every copy → the buffered op is obsolete
    this._buffer.Supersede("f.bin", 0, 4);

    this._buffer.IsDirty("f.bin").Should().BeFalse("the fully-superseded op is gone, so it can never flush over the newer data");
  }

  [Test]
  [Category("EdgeCase")]
  public void Supersede_GivenPartialOverlap_WhenApplied_ThenOnlyOverlapDroppedAndRestSurvives() {
    this._buffer.StageWrite("f.bin", 0, [1, 2, 3, 4, 5, 6], 0, 1); // owed bytes at 0..6

    // only offsets 2..4 became durable elsewhere
    this._buffer.Supersede("f.bin", 2, 2);

    // the prefix [0,2) and suffix [4,6) are still legitimately owed
    var block = this._buffer.OverlayBlock("f.bin", 0, 16, new byte[6]);
    block[0].Should().Be(1);
    block[1].Should().Be(2);
    block[2].Should().Be(0, "the superseded middle is no longer owed");
    block[3].Should().Be(0);
    block[4].Should().Be(5);
    block[5].Should().Be(6);
  }

  [Test]
  [Category("EdgeCase")]
  public void PageKey_GivenDifferentCasing_WhenCompared_ThenItFollowsThePlatform() {
    var pool = Guid.NewGuid();
    var mixed = new PageKey(pool, "Docs/File.bin", 3);
    var lowered = new PageKey(pool, "docs/file.bin", 3);

    if (OperatingSystem.IsWindows()) {
      // one file under two spellings: a block cached under either must be found — and invalidated —
      // under the other, or a mutation through one casing leaves a stale block behind the other
      mixed.Should().Be(lowered);
      mixed.GetHashCode().Should().Be(lowered.GetHashCode());
      return;
    }

    // POSIX: two names are two files. Sharing a cache entry between them serves one file's blocks
    // for the other's reads, which is silent corruption rather than a stale-cache annoyance.
    mixed.Should().NotBe(lowered, "on POSIX these address different files");
  }

  [Test]
  [Category("EdgeCase")]
  public void MetadataKey_GivenDifferentCasing_WhenCompared_ThenItFollowsThePlatform() {
    var pool = Guid.NewGuid();
    var mixed = new MetadataKey(pool, "A/B.txt", MetadataKind.Stat);
    var lowered = new MetadataKey(pool, "a/b.txt", MetadataKind.Stat);

    if (OperatingSystem.IsWindows())
      mixed.Should().Be(lowered, "Windows resolves both spellings to one file");
    else
      mixed.Should().NotBe(lowered, "POSIX resolves them to two files, and a stat for one is not a stat for the other");

    // the KIND is part of the identity on every platform — a cached stat is not a cached placement
    mixed.Should().NotBe(new MetadataKey(pool, "A/B.txt", MetadataKind.Placement));
  }

  [Test]
  [Category("EdgeCase")]
  public void Journal_GivenLocalPlusWholeFileRemote_WhenAppended_ThenOnlyTheDurableLocalMemberHoldsIt() {
    // a whole-file remote (no DurableFlush) must NOT carry the journal — appending would
    // re-upload the whole growing file per intent and throttle the pool to WAN speed
    var local = new TestSupport.FakeVolumeIO(Guid.NewGuid(), "local", "PHYS-L", capacity: 1L << 20);
    var remote = new TestSupport.FakeVolumeIO(Guid.NewGuid(), "remote", "UNC-R", capacity: 1L << 20) {
      Caps = BackendCaps.RandomRead | BackendCaps.List | BackendCaps.Delete, // FTP-ish: no DurableFlush, no RandomWrite
    };
    var journal = new Journal(new MemberJournalStore([local, remote]));

    journal.LogIntent(JournalOp.Write, "f.bin", offset: 0, length: 4);

    local.GetContent(MemberJournalStore.JournalPath, false).Should().NotBeNull("the durable local member holds the WAL");
    remote.GetContent(MemberJournalStore.JournalPath, false).Should().BeNull("the whole-file remote is never journalled to");
  }
}

/// <summary>
/// The write buffer answers "is anything owed on this path?" without its lock, because every read
/// asks it. What must survive that: a reader of a CLEAN path gets the disk block back untouched, a
/// reader of a DIRTY path always gets the overlay, and the map never tears under concurrent
/// staging, draining and renaming of other paths.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WriteBufferConcurrencyTests {

  [Test]
  [Category("EdgeCase")]
  public void Overlay_GivenOtherPathsStagedDrainedAndRenamedConcurrently_ThenEachPathReadsItsOwnTruth() {
    var cache = new CacheInstance("wbc" + Guid.NewGuid().ToString("N"), new() { Size = "16777216", BlockSize = "16" });
    var buffer = new WriteBufferManager(cache);
    buffer.StageWrite("dirty.bin", 0, [7, 7, 7, 7], 0, 1).Should().BeTrue();
    var disk = new byte[] { 1, 2, 3, 4 };
    var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var stop = new CancellationTokenSource();

    var churn = Enumerable.Range(0, 4).Select(worker => Task.Run(() => {
      for (var i = 0; !stop.IsCancellationRequested; ++i) {
        var path = $"churn{worker}-{i % 16}.bin";
        buffer.StageWrite(path, 0, [1], 0, 1);
        if (i % 3 == 0)
          buffer.RenamePath(path, path + ".moved");
        buffer.Drain(path);
        buffer.Drain(path + ".moved");
      }
    })).ToArray();

    var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() => {
      for (var i = 0; i < 50_000; ++i) {
        if (!ReferenceEquals(buffer.OverlayBlock("clean.bin", 0, 16, disk), disk))
          failures.Enqueue("a clean path's disk block was not handed back as it is");
        if (buffer.IsDirty("clean.bin") || buffer.OverlayLength("clean.bin", 4) != 4)
          failures.Enqueue("a clean path looked dirty");
        if (!buffer.IsDirty("dirty.bin") || buffer.OverlayBlock("dirty.bin", 0, 16, disk)[0] != 7 || buffer.OverlayLength("dirty.bin", 0) != 4)
          failures.Enqueue("a dirty path's owed bytes were not overlaid");
      }
    })).ToArray();

    Task.WaitAll(readers, TimeSpan.FromSeconds(60)).Should().BeTrue("no reader may block for good");
    stop.Cancel();
    Task.WaitAll(churn, TimeSpan.FromSeconds(60)).Should().BeTrue();

    failures.Should().BeEmpty();
    buffer.DirtyPaths.Should().Equal("dirty.bin");
    disk.Should().Equal(1, 2, 3, 4);
  }

}
