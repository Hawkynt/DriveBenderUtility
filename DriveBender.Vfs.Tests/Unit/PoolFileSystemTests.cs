using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class PoolFileSystemTests {

  private static readonly Guid _pool = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

  private FakeVolumeIO _volume1 = null!;
  private FakeVolumeIO _volume2 = null!;
  private CacheInstance _cache = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    this._cache = new("test", new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    this._fs = new(_pool, [new(this._volume1), new(this._volume2)], this._cache, ConfigResolver.ResolveEffective(null, """{ "io": { "mirrorReadSplitThreshold": "64" } }"""));
    this._fs.Mount(new(@"X:\"));
  }

  private static byte[] _Pattern(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i)
      data[i] = (byte)(i * 31 + 7);
    return data;
  }

  [Test]
  [Category("HappyPath")]
  public void GetAttributes_GivenFileOnOneVolume_WhenQueried_ThenLogicalSizeNotSumOfCopies() {
    this._volume1.Seed("docs/f.txt", false, new byte[100]);
    this._volume2.Seed("docs/f.txt", true, new byte[100]);

    this._fs.GetAttributes("docs/f.txt").Length.Should().Be(100, "size is the logical size, not the sum of copies (FR-STAT)");
  }

  [Test]
  [Category("Exception")]
  public void GetAttributes_GivenMissingPath_WhenQueried_ThenNotFound() {
    var act = () => this._fs.GetAttributes("nope.bin");
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("HappyPath")]
  public void ReadDirectory_GivenFilesSpreadAcrossVolumes_WhenListed_ThenMergedNamespace() {
    this._volume1.Seed("docs/a.txt", false, [1]);
    this._volume2.Seed("docs/b.txt", false, [2]);
    this._volume1.EnsureFolder("docs/sub", false);

    var entries = this._fs.ReadDirectory("docs");

    entries.Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "b.txt", "sub"], "the pool presents one merged tree (FR-DIR)");
  }

  [Test]
  [Category("HappyPath")]
  public void ReadDirectory_GivenSidecarsOnDisk_WhenListed_ThenHidden() {
    this._volume1.Seed("docs/real.txt", false, [1]);
    this._volume1.Seed("docs/ghost.txt", true, [1]); // lives in FOLDER.DUPLICATE.$DRIVEBENDER
    this._volume1.Seed("docs/moving.bin.TEMP.$DRIVEBENDER", false, [1]);

    var entries = this._fs.ReadDirectory("docs");

    entries.Select(e => e.Name).Should().BeEquivalentTo(["real.txt", "ghost.txt"],
      "shadow-only files surface once under their logical name; temp sidecars stay hidden (FR-HIDE)");
  }

  [Test]
  [Category("EdgeCase")]
  public void ReadDirectory_GivenShadowOnlySurvivor_WhenPrimaryHolderOffline_ThenFileStillListed() {
    this._volume1.Seed("docs/f.txt", false, [1]);
    this._volume2.Seed("docs/f.txt", true, [1]);
    this._volume1.IsOnline = false;

    var entries = this._fs.ReadDirectory("docs");

    entries.Should().ContainSingle(e => e.Name == "f.txt", "duplicated files stay readable on drive loss (SAFE-DEGRADE)");
  }

  [Test]
  [Category("Exception")]
  public void ReadDirectory_GivenMissingFolder_WhenListed_ThenNotFound() {
    var act = () => this._fs.ReadDirectory("no-such-dir");
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenFileSpanningManyBlocks_WhenReadInChunks_ThenExactBytes() {
    var content = _Pattern(100); // block size is 16 → 7 blocks
    this._volume1.Seed("big.bin", false, content);
    var handle = this._fs.Open("big.bin", AccessMode.Read, ShareMode.Read);

    var result = new byte[100];
    var buffer = new byte[7];
    var offset = 0L;
    int read;
    while ((read = this._fs.Read(handle, buffer, offset)) > 0) {
      Array.Copy(buffer, 0, result, offset, read);
      offset += read;
    }

    this._fs.Close(handle);
    offset.Should().Be(100);
    result.Should().Equal(content, "exact bytes at any offset/length (FR-READ)");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenOffsetPastEof_WhenRead_ThenZeroBytes() {
    this._volume1.Seed("f.bin", false, new byte[10]);
    var handle = this._fs.Open("f.bin", AccessMode.Read, ShareMode.Read);

    this._fs.Read(handle, new byte[4], 10).Should().Be(0, "reads past EOF return 0 (FR-READ)");
    this._fs.Read(handle, new byte[4], 100).Should().Be(0);
    this._fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenDuplicatedLargeFile_WhenMirrorSplitActive_ThenContentStillExact() {
    var content = _Pattern(256); // ≥ the 64-byte mirror threshold with 16-byte blocks
    this._volume1.Seed("mirrored.bin", false, content);
    this._volume2.Seed("mirrored.bin", true, content);
    var handle = this._fs.Open("mirrored.bin", AccessMode.Read, ShareMode.Read);

    var buffer = new byte[256];
    this._fs.Read(handle, buffer, 0).Should().Be(256);
    buffer.Should().Equal(content, "mirror block routing must be correctness-neutral (FR-MIRROR)");
    this._fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenSequentialAccess_WhenPrefetching_ThenSubsequentBlocksComeFromCache() {
    var content = _Pattern(512);
    this._volume1.Seed("stream.bin", false, content);
    var handle = this._fs.Open("stream.bin", AccessMode.Read, ShareMode.Read);

    var buffer = new byte[16];
    this._fs.Read(handle, buffer, 0);   // triggers read-ahead of the next window
    this._volume1.AlwaysFail(VolumeOp.OpenRead); // volume now refuses — only the cache can serve

    var act = () => this._fs.Read(handle, buffer, 16);
    act.Should().NotThrow("the prefetched block is served from the page cache (FR-RA)");
    buffer.Should().Equal(content.AsSpan(16, 16).ToArray());
    this._fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenPrimaryVanishedAfterOpen_WhenRead_ThenServedFromShadowCopy() {
    var content = _Pattern(32);
    this._volume1.Seed("f.bin", false, content);
    this._volume2.Seed("f.bin", true, content);
    var handle = this._fs.Open("f.bin", AccessMode.Read, ShareMode.Read);

    this._volume1.IsOnline = false;
    this._fs.Placement.Invalidate("f.bin");

    var buffer = new byte[32];
    this._fs.Read(handle, buffer, 0).Should().Be(32);
    buffer.Should().Equal(content, "reads fail over to surviving copies (UC4)");
    this._fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void StatFs_GivenMembersOnDistinctVolumes_WhenQueried_ThenAggregates() {
    var stats = this._fs.StatFs();
    stats.BytesTotal.Should().Be(2L << 20);
    stats.BytesFree.Should().BeGreaterThan(0);
  }

  [Test]
  [Category("EdgeCase")]
  public void StatFs_GivenTwoMembersOnOnePhysicalVolume_WhenQueried_ThenCountedOnce() {
    var sameDisk = new FakeVolumeIO(Guid.NewGuid(), "v1b", "PHYS-1", capacity: 1L << 20);
    var fs = new PoolFileSystem(_pool, [new(this._volume1), new(sameDisk, ReserveBytes: 100)], this._cache, ConfigResolver.ResolveEffective(null, null));
    fs.Mount(new(@"Y:\"));

    var stats = fs.StatFs();

    stats.BytesTotal.Should().Be(1L << 20, "one physical volume counts once (FR-SPACE-SHARED)");
    stats.BytesFree.Should().Be((1L << 20) - 100, "reserveBytes reduce usable space");
  }

  [Test]
  [Category("Exception")]
  public void WriteOps_GivenReadHandle_WhenWriting_ThenAccessDenied() {
    this._volume1.Seed("f.bin", false, [1]);
    var handle = this._fs.Open("f.bin", AccessMode.Read, ShareMode.Read);

    ((Action)(() => this._fs.Write(handle, new byte[1], 0, WriteMode.Normal))).Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.AccessDenied);
    this._fs.Close(handle);
  }

  [Test]
  [Category("Exception")]
  public void Open_GivenReadOnlyMount_WhenOpenedForWrite_ThenAccessDenied() {
    this._volume1.Seed("f.bin", false, [1]);
    var fs = new PoolFileSystem(_pool, [new(this._volume1)], this._cache, ConfigResolver.ResolveEffective(null, null));
    fs.Mount(new(@"Z:\", ReadOnly: true));

    var act = () => fs.Open("f.bin", AccessMode.ReadWrite, ShareMode.None);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.AccessDenied);
  }

  [Test]
  [Category("Exception")]
  public void Read_GivenClosedHandle_WhenRead_ThenStaleHandle() {
    this._volume1.Seed("f.bin", false, [1]);
    var handle = this._fs.Open("f.bin", AccessMode.Read, ShareMode.Read);
    this._fs.Close(handle);

    var act = () => this._fs.Read(handle, new byte[1], 0);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.StaleHandle);
  }

  [Test]
  [Category("Exception")]
  public void Mount_GivenAllMembersOffline_WhenMounted_ThenRefused() {
    this._volume1.IsOnline = false;
    var fs = new PoolFileSystem(_pool, [new(this._volume1)], this._cache, ConfigResolver.ResolveEffective(null, null));

    var act = () => fs.Mount(new(@"Q:\"));
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Offline);
  }

  [Test]
  [Category("EdgeCase")]
  public void AnyOp_GivenUnmountedFs_WhenCalled_ThenRejected() {
    var fs = new PoolFileSystem(_pool, [new(this._volume1)], this._cache, ConfigResolver.ResolveEffective(null, null));
    var act = () => fs.GetAttributes("f");
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.InvalidArgument);
  }

}
