using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// What the recycle bin takes, beyond "is it on". A file rewritten all the time (a log, a database)
/// would fill the bin with near-identical versions, a huge file would fill it in one go, and a disk
/// running out of room needs that room more than it needs an undo:
/// <list type="bullet">
/// <item><c>trash.replacedInterval</c>: a REPLACED version is kept only when the bin has none of that
/// file from within the interval; deletes, being deliberate, are always kept.</item>
/// <item><c>trash.maxFileSize</c>: larger files skip the bin.</item>
/// <item><c>trash.minFreeSpace</c>: below it, nothing new goes to the bin at all.</item>
/// </list>
/// </summary>
[TestFixture]
[Category("Unit")]
public class BinPolicyTests {

  private static readonly Guid _pool = Guid.Parse("b1b1b1b1-0000-0000-0000-0000000000f0");

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private DateTime _now;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24);
    this._now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
  }

  private PoolFileSystem _Engine(string trash) {
    var cache = new CacheInstance("bp" + Guid.NewGuid().ToString("N"),
      new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 2, "trash": { "enabled": true {{trash}} } }"""), clock: () => this._now);
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

  /// <summary>How many versions of a path the bin holds, across members (the listing shows the newest only).</summary>
  private int _Binned(string path)
    => new[] { this._v1, this._v2 }.Sum(v => v.FilePaths.Count(p => p.StartsWith($"{PoolTrash.TrashPrefix}/{path}.", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".trashver", StringComparison.OrdinalIgnoreCase)));

  #region replacedInterval

  [Test]
  [Category("HappyPath")]
  public void Replaced_GivenAFileIsSavedOverAndOverWithinTheInterval_ThenTheBinKeepsOneVersionPerInterval() {
    using var fs = this._Engine(""", "replacedInterval": "15m" """);
    _Write(fs, "app.log", [0]);
    for (var save = 1; save <= 10; ++save) {
      this._now = this._now.AddSeconds(30);
      _Write(fs, "app.log", [(byte)save]);
    }

    this._Binned("app.log").Should().Be(1, "ten saves within five minutes are one version in the bin");

    this._now = this._now.AddMinutes(15);
    _Write(fs, "app.log", [99]);
    this._Binned("app.log").Should().Be(2, "once the interval has passed, the next replaced version is kept");
  }

  [TestCase("0", TestName = "Replaced_GivenTheIntervalIsZero_ThenEveryVersionIsKept")]
  [TestCase("off", TestName = "Replaced_GivenTheIntervalIsOff_ThenEveryVersionIsKept")]
  [Category("EdgeCase")]
  public void Replaced_GivenNoInterval_ThenEveryVersionIsKept(string interval) {
    using var fs = this._Engine($$""", "replacedInterval": "{{interval}}" """);
    _Write(fs, "doc.txt", [0]);
    for (var save = 1; save <= 3; ++save)
      _Write(fs, "doc.txt", [(byte)save]);

    this._Binned("doc.txt").Should().Be(3);
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenAReplacedVersionWasKeptMomentsAgo_ThenTheDeleteIsKeptAnyway() {
    // a delete is deliberate, and the one thing the bin must never refuse for being too frequent
    using var fs = this._Engine(""", "replacedInterval": "1h" """);
    _Write(fs, "notes.txt", [1]);
    _Write(fs, "notes.txt", [2]);

    fs.Unlink("notes.txt");

    this._Binned("notes.txt").Should().Be(2, "the replaced version and the deleted one");
  }

  [Test]
  [Category("EdgeCase")]
  public void Replaced_GivenTheIntervalIsCountedAcrossARemount_ThenTheBinIsAskedNotForgotten() {
    using (var fs = this._Engine(""", "replacedInterval": "15m" """)) {
      _Write(fs, "db.sqlite", [1]);
      _Write(fs, "db.sqlite", [2]);
    }

    this._now = this._now.AddMinutes(5);
    using (var again = this._Engine(""", "replacedInterval": "15m" """))
      _Write(again, "db.sqlite", [3]);

    this._Binned("db.sqlite").Should().Be(1, "the version kept before the remount still counts");
  }

  [Test]
  [Category("EdgeCase")]
  public void Replaced_GivenOtherFilesInTheSameFolder_ThenEachIsCountedOnItsOwn() {
    using var fs = this._Engine(""", "replacedInterval": "15m" """);
    _Write(fs, "a.log", [1]);
    _Write(fs, "a.log.old", [1]);
    _Write(fs, "a.log", [2]);

    _Write(fs, "a.log.old", [2]);

    this._Binned("a.log.old").Should().Be(1, "a.log being kept moments ago says nothing about a.log.old");
  }

  #endregion

  #region maxFileSize

  [TestCase("4KiB", 8 * 1024, false, TestName = "Delete_GivenAFileLargerThanMaxFileSize_ThenItSkipsTheBin")]
  [TestCase("4KiB", 4 * 1024, true, TestName = "Delete_GivenAFileExactlyMaxFileSize_ThenItIsKept")]
  [TestCase("1%", 64 * 1024, true, TestName = "Delete_GivenAFileUnderAPercentageOfTheMember_ThenItIsKept")]
  [TestCase("1%", 512 * 1024, false, TestName = "Delete_GivenAFileOverAPercentageOfTheMember_ThenItSkipsTheBin")]
  [Category("EdgeCase")]
  public void Delete_GivenAMaxFileSize_ThenOnlyFilesUpToItAreKept(string maxFileSize, int length, bool kept) {
    using var fs = this._Engine($$""", "maxFileSize": "{{maxFileSize}}" """);
    _Write(fs, "big.bin", new byte[length]);

    fs.Unlink("big.bin");

    this._Binned("big.bin").Should().Be(kept ? 1 : 0);
    fs.ReadDirectory("").Should().NotContain(e => e.Name == "big.bin", "kept or not, a delete is a delete");
  }

  [Test]
  [Category("EdgeCase")]
  public void Replaced_GivenAVersionLargerThanMaxFileSize_ThenItSkipsTheBin() {
    using var fs = this._Engine(""", "maxFileSize": "4KiB", "replacedInterval": "0" """);
    _Write(fs, "video.mp4", new byte[16 * 1024]);
    _Write(fs, "video.mp4", new byte[10]);

    this._Binned("video.mp4").Should().Be(0);
  }

  #endregion

  #region minFreeSpace

  [TestCase(true, TestName = "Delete_GivenTheMemberIsBelowMinFreeSpace_ThenNothingGoesToTheBin")]
  [TestCase(false, TestName = "Replaced_GivenTheMemberIsBelowMinFreeSpace_ThenNothingGoesToTheBin")]
  [Category("EdgeCase")]
  public void Bin_GivenTheMemberIsRunningOutOfRoom_ThenItIsNotUsed(bool delete) {
    // 1 MiB volumes, half of each taken: 50% free, below the 60% asked for
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    using var fs = this._Engine(""", "minFreeSpace": "60%", "replacedInterval": "0" """);
    _Write(fs, "filler.bin", new byte[512 * 1024]);
    _Write(fs, "doc.txt", [1]);

    if (delete)
      fs.Unlink("doc.txt");
    else
      _Write(fs, "doc.txt", [2]);

    this._Binned("doc.txt").Should().Be(0, "the room is needed more than an undo is");
  }

  [Test]
  [Category("HappyPath")]
  public void Bin_GivenTheMemberHasRoomAboveMinFreeSpace_ThenItIsUsed() {
    using var fs = this._Engine(""", "minFreeSpace": "10%" """);
    _Write(fs, "doc.txt", [1]);
    fs.Unlink("doc.txt");
    this._Binned("doc.txt").Should().Be(1);
  }

  #endregion

  [TestCase("replacedInterval", "soon")]
  [TestCase("maxFileSize", "big")]
  [TestCase("minFreeSpace", "-5%")]
  [Category("Exception")]
  public void Config_GivenAnInvalidBinSetting_ThenItIsRefusedWithItsName(string key, string value) {
    var config = ConfigResolver.ResolveEffective(null, $$"""{ "trash": { "enabled": true, "{{key}}": "{{value}}" } }""");
    var validate = () => ConfigValidator.Validate(config, 1L << 34);
    validate.Should().Throw<ConfigValidationException>().Which.Message.Should().Contain(key);
  }

}
