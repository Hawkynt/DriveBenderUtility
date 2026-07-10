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
  public void PageKey_GivenDifferentCasing_WhenCompared_ThenEqual() {
    var pool = Guid.NewGuid();
    new PageKey(pool, "Docs/File.bin", 3).Should().Be(new PageKey(pool, "docs/file.bin", 3));
    new PageKey(pool, "Docs/File.bin", 3).GetHashCode().Should().Be(new PageKey(pool, "docs/file.bin", 3).GetHashCode());
  }

  [Test]
  [Category("EdgeCase")]
  public void MetadataKey_GivenDifferentCasing_WhenCompared_ThenEqual() {
    var pool = Guid.NewGuid();
    new MetadataKey(pool, "A/B.txt", MetadataKind.Stat).Should().Be(new MetadataKey(pool, "a/b.txt", MetadataKind.Stat));
    new MetadataKey(pool, "A/B.txt", MetadataKind.Stat).Should().NotBe(new MetadataKey(pool, "a/b.txt", MetadataKind.Placement));
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
