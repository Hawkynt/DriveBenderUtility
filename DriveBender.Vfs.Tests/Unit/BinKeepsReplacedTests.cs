using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// With the recycle bin on, a version that gets REPLACED goes to the bin, as a deleted one does:
/// saving over the wrong file and renaming over the wrong file are mistakes the bin is there for. It
/// costs no copy where the disk can hard-link (the bin entry is a second name for the old data, and
/// the replacement then takes the live name by an atomic rename), so there is no moment in which the
/// name or the old version is missing.
/// </summary>
[TestFixture]
[Category("Unit")]
public class BinKeepsReplacedTests {

  private static readonly Guid _pool = Guid.Parse("b1b1b1b1-0000-0000-0000-00000000000e");
  private const string _BIN = """{ "duplication": 2, "trash": { "enabled": true }, "readAhead": { "enabled": false } }""";
  private const string _NO_BIN = """{ "duplication": 2, "readAhead": { "enabled": false } }""";
  private const BackendCaps _LINKING = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.AtomicRename | BackendCaps.DurableFlush
                                       | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps | BackendCaps.HardLinks;

  private static readonly byte[] _OLD = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 5 + 2))];
  private static readonly byte[] _NEW = [.. Enumerable.Range(0, 2000).Select(i => (byte)(i * 11 + 7))];

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24);
  }

  private void _Linking() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24) { Caps = _LINKING };
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24) { Caps = _LINKING };
  }

  private PoolFileSystem _Engine(string config = _BIN) {
    var cache = new CacheInstance("bk" + Guid.NewGuid().ToString("N"),
      new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache, ConfigResolver.ResolveEffective(null, config));
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

  /// <summary>What the bin gives back for a path, restored under a scratch name so the live file stays put.</summary>
  private static byte[] _FromBin(PoolFileSystem fs, string path) {
    fs.Rename(path, path + ".live", RenameFlags.None);
    fs.RestoreFromTrash(path);
    var binned = _Read(fs, path);
    fs.Rename(path + ".live", path, RenameFlags.ReplaceExisting);
    return binned;
  }

  [Test]
  [Category("HappyPath")]
  public void Overwrite_GivenTheBinIsOn_ThenThePreviousVersionIsInTheBin() {
    using var fs = this._Engine();
    _Write(fs, "report.doc", _OLD);

    _Write(fs, "report.doc", _NEW);

    _Read(fs, "report.doc").Should().Equal(_NEW);
    fs.Trash.List().Should().ContainSingle(e => e.OriginalPath == "report.doc");
    fs.RestoreFromTrash("report.doc"); // a swap: the saved version goes to the bin in its place
    _Read(fs, "report.doc").Should().Equal(_OLD, "the version saved over is restorable");
    fs.RestoreFromTrash("report.doc");
    _Read(fs, "report.doc").Should().Equal(_NEW);
  }

  [Test]
  [Category("HappyPath")]
  public void RenameOver_GivenTheBinIsOn_ThenTheReplacedFileIsInTheBin() {
    using var fs = this._Engine();
    _Write(fs, "report.doc", _OLD);
    _Write(fs, "draft.doc", _NEW);

    fs.Rename("draft.doc", "report.doc", RenameFlags.ReplaceExisting);

    _Read(fs, "report.doc").Should().Equal(_NEW);
    fs.Trash.List().Should().ContainSingle(e => e.OriginalPath == "report.doc");
    _FromBin(fs, "report.doc").Should().Equal(_OLD, "the file renamed over is restorable");
  }

  [TestCase(false, TestName = "Overwrite_GivenTheBinIsOff_ThenNothingIsKept")]
  [TestCase(true, TestName = "RenameOver_GivenTheBinIsOff_ThenNothingIsKept")]
  [Category("EdgeCase")]
  public void Replace_GivenTheBinIsOff_ThenNothingIsKept(bool byRename) {
    using var fs = this._Engine(_NO_BIN);
    _Write(fs, "report.doc", _OLD);
    if (byRename) {
      _Write(fs, "draft.doc", _NEW);
      fs.Rename("draft.doc", "report.doc", RenameFlags.ReplaceExisting);
    } else
      _Write(fs, "report.doc", _NEW);

    _Read(fs, "report.doc").Should().Equal(_NEW);
    fs.Trash.List().Should().BeEmpty();
    new[] { this._v1, this._v2 }.SelectMany(v => v.FilePaths).Should().NotContain(p => p.Contains("trash"));
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenItFailedAndTheOldFileStaysLive_ThenTheBinGetsNothing() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: _OLD.Length + 64 * 1024);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: _OLD.Length + 64 * 1024);
    using var fs = this._Engine();
    _Write(fs, "report.doc", _OLD);

    var overwrite = () => _Write(fs, "report.doc", new byte[256 * 1024]);

    overwrite.Should().Throw<PoolFsException>();
    _Read(fs, "report.doc").Should().Equal(_OLD);
    fs.Trash.List().Should().BeEmpty("nothing was replaced, so nothing is kept");
  }

  [Test]
  [Category("EdgeCase")]
  public void Overwrite_GivenTheDiskCanHardLink_ThenTheBinEntryCostsNoCopyAndStillHoldsTheOldVersion() {
    // the same overwrite, binned, with and without hard links: the difference in bytes written is the
    // copy the link saves, and it must be at least the old version on each kept copy
    long Written(bool linking) {
      if (linking)
        this._Linking();
      else
        this.SetUp();

      using var fs = this._Engine();
      _Write(fs, "report.doc", _OLD);
      var before = this._v1.BytesWritten + this._v2.BytesWritten;
      _Write(fs, "report.doc", _NEW);
      var spent = this._v1.BytesWritten + this._v2.BytesWritten - before;

      _Read(fs, "report.doc").Should().Equal(_NEW);
      _FromBin(fs, "report.doc").Should().Equal(_OLD, "linked or copied, the bin holds the old version");
      return spent;
    }

    var copied = Written(linking: false);
    var linked = Written(linking: true);

    // less a few bytes: the two runs' sidecars carry timestamps whose JSON length differs by a digit or two
    (copied - linked).Should().BeGreaterThanOrEqualTo(_OLD.Length - 64, $"the link saves the copy ({copied:N0} bytes written copying, {linked:N0} linking)");
  }

  [TestCase(false, true, TestName = "Overwrite_GivenTheBinIsOnAndThePowerIsCutAtEveryStep_ThenNoVersionIsLost")]
  [TestCase(true, true, TestName = "RenameOver_GivenTheBinIsOnAndThePowerIsCutAtEveryStep_ThenNoVersionIsLost")]
  [TestCase(true, false, TestName = "RenameOver_GivenThePowerIsCutAtEveryStep_ThenTheRenamedContentIsNeverLost")]
  [Category("EdgeCase")]
  public void Replace_GivenThePowerIsCutAtEveryStep_ThenNoVersionIsLost(bool byRename, bool bin) {
    var config = bin ? _BIN : _NO_BIN;
    for (var step = 1; ; ++step) {
      this.SetUp();
      using (var seeding = this._Engine(config)) {
        _Write(seeding, "report.doc", _OLD);
        if (byRename)
          _Write(seeding, "draft.doc", _NEW);
      }

      var fired = 0;
      var remaining = step;
      void Cut(VolumeOp op, string path) {
        if (Volatile.Read(ref fired) == 1 || Interlocked.Decrement(ref remaining) == 0) {
          Interlocked.Exchange(ref fired, 1);
          throw new InvalidOperationException("power loss");
        }
      }

      var fs = this._Engine(config);
      this._v1.BeforeOperation = Cut;
      this._v2.BeforeOperation = Cut;
      try {
        if (byRename)
          fs.Rename("draft.doc", "report.doc", RenameFlags.ReplaceExisting);
        else
          _Write(fs, "report.doc", _NEW);
      } catch (Exception) {
        // the machine went down part-way
      }

      try {
        fs.Dispose();
      } catch (Exception) {
      }

      this._v1.BeforeOperation = null;
      this._v2.BeforeOperation = null;
      if (fired == 0) {
        step.Should().BeGreaterThan(4);
        break;
      }

      this._v1.SimulateCrash();
      this._v2.SimulateCrash();
      using var recovered = this._Engine(config);
      var names = recovered.ReadDirectory("").Select(e => e.Name).ToArray();
      var live = names.Contains("report.doc") ? _Read(recovered, "report.doc") : null;
      var source = names.Contains("draft.doc") ? _Read(recovered, "draft.doc") : null;
      var context = $"power cut at step {step} (found: {string.Join(", ", names)})";

      live.Should().NotBeNull($"{context}: the name holds a version, the old or the new one");
      (live!.SequenceEqual(_OLD) || live.SequenceEqual(_NEW)).Should().BeTrue($"{context}: one whole version");
      if (byRename)
        (live.SequenceEqual(_NEW) || (source != null && source.SequenceEqual(_NEW))).Should().BeTrue(
          $"{context}: the content being renamed is under one of its two names, never gone");
      if (bin && live.SequenceEqual(_NEW) && source == null)
        _FromBin(recovered, "report.doc").Should().Equal(_OLD, $"{context}: replaced, so the old version is in the bin");
    }
  }

}
