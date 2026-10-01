using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// A member's <c>reserveBytes</c> is room the pool must never consume on that member's volume — for
/// the host filesystem, for another tenant sharing the disk. Placement honoured it for new files, but
/// a write that GREW a file went straight through it, and a reserve changed on a mounted pool was not
/// applied until the next mount. A growing write or truncate that would reach into a reserve is
/// refused with NoSpace before any byte lands; shrinking and rewriting within the length are not.
/// </summary>
[TestFixture]
[Category("Unit")]
public class MemberReserveTests {

  private static readonly Guid _pool = Guid.Parse("5e5e0e00-0000-0000-0000-0000000000a7");
  private const long _CAPACITY = 1L << 20; // 1 MiB volumes, so a reserve is easy to reach
  private static readonly byte[] _SMALL = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", _CAPACITY);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", _CAPACITY);
  }

  private PoolFileSystem _Engine(long reserve, int duplication = 2) {
    var cache = new CacheInstance("rs" + Guid.NewGuid().ToString("N"),
      new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1, ReserveBytes: reserve), new(this._v2, ReserveBytes: reserve)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": {{duplication}}, "readAhead": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    try {
      fs.Write(handle, content, 0, WriteMode.Normal);
    } finally {
      fs.Close(handle);
    }
  }

  private static byte[] _Read(PoolFileSystem fs, string path) {
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var buffer = new byte[fs.GetAttributes(path).Length];
      var read = 0;
      while (read < buffer.Length && fs.Read(handle, buffer.AsSpan(read), read) is var got and > 0)
        read += got;

      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenItFitsBesideTheReserve_ThenItIsStored() {
    using var fs = this._Engine(reserve: _CAPACITY / 2);
    _Write(fs, "fits.bin", _SMALL);
    _Read(fs, "fits.bin").Should().Equal(_SMALL);
  }

  [Test]
  [Category("Exception")]
  public void Append_GivenItWouldReachIntoTheReserve_ThenItIsRefusedAndNothingLands() {
    using var fs = this._Engine(reserve: _CAPACITY - 64 * 1024); // 64 KiB the pool may use
    _Write(fs, "log.bin", _SMALL);
    var handle = fs.Open("log.bin", AccessMode.ReadWrite, ShareMode.Read);

    var grow = () => fs.Write(handle, new byte[128 * 1024], _SMALL.Length, WriteMode.Normal);

    grow.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
    fs.Close(handle);
    _Read(fs, "log.bin").Should().Equal(_SMALL, "a refused write leaves the file exactly as it was");
  }

  [Test]
  [Category("Exception")]
  public void SetLength_GivenGrowingWouldReachIntoTheReserve_ThenItIsRefused() {
    using var fs = this._Engine(reserve: _CAPACITY - 64 * 1024);
    _Write(fs, "sparse.bin", _SMALL);
    var handle = fs.Open("sparse.bin", AccessMode.ReadWrite, ShareMode.Read);

    var grow = () => fs.SetLength(handle, 512 * 1024);

    grow.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
    fs.Close(handle);
    fs.GetAttributes("sparse.bin").Length.Should().Be(_SMALL.Length);
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenItStaysWithinTheFileOrShrinksIt_ThenTheReserveIsNoObstacle() {
    // rewriting bytes a file already has, and making it smaller, take no new room
    using var fs = this._Engine(reserve: _CAPACITY - 4 * 1024);
    _Write(fs, "edit.bin", _SMALL);
    var handle = fs.Open("edit.bin", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(handle, [42], 10, WriteMode.Normal);
    fs.SetLength(handle, 500);
    fs.Close(handle);

    var back = _Read(fs, "edit.bin");
    back.Should().HaveCount(500);
    back[10].Should().Be(42);
  }

  [Test]
  [Category("Exception")]
  public void Overwrite_GivenTheNewContentWouldReachIntoTheReserve_ThenTheOldFileSurvives() {
    using var fs = this._Engine(reserve: _CAPACITY - 64 * 1024);
    _Write(fs, "doc.bin", _SMALL);

    var overwrite = () => _Write(fs, "doc.bin", new byte[256 * 1024]);

    overwrite.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
    _Read(fs, "doc.bin").Should().Equal(_SMALL);
  }

  [Test]
  [Category("EdgeCase")]
  public void Reserve_GivenItIsRaisedOnAMountedPool_ThenItAppliesAtOnce() {
    using var fs = this._Engine(reserve: 0);
    _Write(fs, "a.bin", _SMALL);
    var before = fs.StatFs().BytesFree;

    fs.UpdateMemberReserves(new Dictionary<Guid, long> { [this._v1.MemberId] = _CAPACITY, [this._v2.MemberId] = _CAPACITY });

    fs.StatFs().BytesFree.Should().Be(0, "the whole of both volumes is reserved now");
    before.Should().BeGreaterThan(0);
    var handle = fs.Open("a.bin", AccessMode.ReadWrite, ShareMode.Read);
    var grow = () => fs.Write(handle, new byte[4096], _SMALL.Length, WriteMode.Normal);
    grow.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
    fs.Close(handle);
    var create = () => _Write(fs, "b.bin", _SMALL);
    create.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace, "nor does a new file find a place");
  }

  [Test]
  [Category("EdgeCase")]
  public void Reserve_GivenItIsLoweredOnAMountedPool_ThenTheRoomIsUsableAgain() {
    using var fs = this._Engine(reserve: _CAPACITY);
    fs.UpdateMemberReserves(new Dictionary<Guid, long> { [this._v1.MemberId] = 0, [this._v2.MemberId] = 0 });

    _Write(fs, "after.bin", _SMALL);
    _Read(fs, "after.bin").Should().Equal(_SMALL);
  }

}
