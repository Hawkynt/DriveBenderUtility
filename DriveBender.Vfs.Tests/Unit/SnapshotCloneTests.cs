using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Keeping a version by CLONE (docs/Snapshots.md): an in-place modification of a pinned file needs
/// the old content kept while the live file stays where it is. That was always a full copy; on a
/// member that can clone blocks it is now a clone — verified, and a copy whenever the clone cannot be
/// made or does not match.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SnapshotCloneTests {

  private static readonly Guid _pool = Guid.Parse("c10ec10e-0000-0000-0000-000000000003");

  private const BackendCaps _CLONING = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.AtomicRename | BackendCaps.DurableFlush
                                       | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps | BackendCaps.BlockClone;

  private const int _SIZE = 1 << 20;

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;

  private void _Members(bool clone) {
    var caps = clone ? _CLONING : _CLONING & ~BackendCaps.BlockClone;
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 24) { Caps = caps };
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 24) { Caps = caps };
  }

  [SetUp]
  public void SetUp() => this._Members(clone: true);

  private PoolFileSystem _Mounted(string config = """{ "duplication": 1, "trash": { "enabled": false } }""") {
    var cache = new CacheInstance("cl" + Guid.NewGuid().ToString("N"), new() { Size = "4194304", BlockSize = "4096", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._a), new(this._b)], cache, ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static byte[] _Content(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 13 + 5))];

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    if (content.Length > 0)
      fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static byte[] _ReadSnapshotFile(PoolFileSystem fs, Guid id, string path) {
    using var stream = fs.OpenSnapshotFile(id, path);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private long _Written => this._a.BytesWritten + this._b.BytesWritten;

  [Test]
  [Category("HappyPath")]
  public void OpenForWriting_GivenAPinnedFileOnAMemberThatCanClone_ThenNothingIsCopiedAndTheVersionHoldsTheOldContent() {
    using var fs = this._Mounted();
    var original = _Content(_SIZE);
    _Write(fs, "vm.disk", original);
    var taken = fs.TakeSnapshot("s");

    var before = this._Written;
    var handle = fs.Open("vm.disk", AccessMode.ReadWrite, ShareMode.Read);
    var cost = this._Written - before;
    fs.Write(handle, [0xFF, 0xFF], 1000, WriteMode.Normal);
    fs.Close(handle);

    cost.Should().BeLessThan(16 * 1024, "a clone copies no data — what is written is the journal and a sidecar");
    _ReadSnapshotFile(fs, taken.Id, "vm.disk").Should().Equal(original, "and the clone is independent of the live file written afterwards");
  }

  [Test]
  [Category("HappyPath")]
  public void OpenForWriting_GivenAMemberThatCannotClone_ThenTheVersionIsCopied() {
    // the fall-back, and the proof the measurement above can tell the two apart
    this._Members(clone: false);
    using var fs = this._Mounted();
    var original = _Content(_SIZE);
    _Write(fs, "vm.disk", original);
    var taken = fs.TakeSnapshot("s");

    var before = this._Written;
    fs.Close(fs.Open("vm.disk", AccessMode.ReadWrite, ShareMode.Read));

    (this._Written - before).Should().BeGreaterThanOrEqualTo(_SIZE, "without cloning the whole file is copied");
    _ReadSnapshotFile(fs, taken.Id, "vm.disk").Should().Equal(original);
  }

  [TestCase(0, TestName = "OpenForWriting_GivenACloneThatDoesNotMatch_WhenTheFileIsEmpty_ThenTheVersionIsStillExact")]
  [TestCase(1, TestName = "OpenForWriting_GivenACloneThatDoesNotMatch_WhenTheFileIsOneByte_ThenItIsCopiedInstead")]
  [TestCase(WholeFilePublisher.CloneVerifyBytes, TestName = "OpenForWriting_GivenACloneThatDoesNotMatch_WhenTheFileIsExactlyTheSample_ThenItIsCopiedInstead")]
  [TestCase(WholeFilePublisher.CloneVerifyBytes + 1, TestName = "OpenForWriting_GivenACloneThatDoesNotMatch_WhenTheFileIsOneBytePastTheSample_ThenItIsCopiedInstead")]
  [TestCase(_SIZE + 3, TestName = "OpenForWriting_GivenACloneThatDoesNotMatch_WhenTheFileIsLarge_ThenItIsCopiedInstead")]
  [Category("Exception")]
  public void OpenForWriting_GivenACloneThatDoesNotMatchItsSource_ThenItIsDiscardedAndTheVersionIsCopied(int length) {
    // a filesystem that answers "cloned" and shares the wrong block must cost time, never content
    this._a.CorruptClones = true;
    this._b.CorruptClones = true;
    using var fs = this._Mounted();
    var original = _Content(length);
    _Write(fs, "f.bin", original);
    var taken = fs.TakeSnapshot("s");

    fs.Close(fs.Open("f.bin", AccessMode.ReadWrite, ShareMode.Read));

    _ReadSnapshotFile(fs, taken.Id, "f.bin").Should().Equal(original);
    foreach (var member in new[] { this._a, this._b })
      member.FilePaths.Should().NotContain(p => p.EndsWith(".TEMP.$DRIVEBENDER", StringComparison.OrdinalIgnoreCase), "the rejected clone is not left behind");
  }

  [Test]
  [Category("HappyPath")]
  public void OpenForWriting_GivenAClone_ThenTheVersionKeepsTheLiveFilesTimes() {
    using var fs = this._Mounted();
    _Write(fs, "f.bin", _Content(4096));
    var stamped = new DateTime(2020, 2, 2, 2, 2, 2, DateTimeKind.Utc);
    fs.SetAttributes("f.bin", new(LastWriteTimeUtc: stamped));
    var taken = fs.TakeSnapshot("s");

    fs.Close(fs.Open("f.bin", AccessMode.ReadWrite, ShareMode.Read));

    var version = fs.Snapshots.Resolve(taken.Id, "f.bin");
    version.Should().NotBeNull();
    version!.Value.member.Stat(version.Value.versionPath, false)!.Value.LastWriteTimeUtc.Should().Be(stamped,
      "a version is the file as it was, times included — a clone is born with the time it was made");
  }

  [Test]
  [Category("HappyPath")]
  public void Delete_GivenTheBinKeepsEveryCopyAndTheShadowCanBeCloned_ThenNothingIsCopied() {
    // the bin moves a primary by rename; a SHADOW crosses namespaces and was always copied
    using var fs = this._Mounted("""{ "duplication": 2, "trash": { "enabled": true, "dropDuplicatesInTrash": false } }""");
    var original = _Content(_SIZE);
    _Write(fs, "big.iso", original);

    var before = this._Written;
    fs.Unlink("big.iso");

    (this._Written - before).Should().BeLessThan(16 * 1024, "the shadow went into the bin as a clone");
    fs.RestoreFromTrash("big.iso");
    var handle = fs.Open("big.iso", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[_SIZE];
    fs.Read(handle, buffer, 0);
    fs.Close(handle);
    buffer.Should().Equal(original);
  }

  [Test]
  [Category("EdgeCase")]
  public void OpenForWriting_GivenAClone_WhenThePowerIsCutAtEveryStep_ThenTheSnapshotAlwaysReadsTheOldContent() {
    var original = _Content(64 * 1024 + 7);
    for (var step = 1; ; ++step) {
      this.SetUp();
      Guid snapshot;
      using (var setup = this._Mounted()) {
        _Write(setup, "db.bin", original);
        snapshot = setup.TakeSnapshot("s").Id;
      }

      var fired = 0;
      var remaining = step;
      void Cut(VolumeOp op, string path) {
        if (Volatile.Read(ref fired) == 1 || Interlocked.Decrement(ref remaining) == 0) {
          Interlocked.Exchange(ref fired, 1);
          throw new InvalidOperationException("power loss");
        }
      }

      var fs = this._Mounted();
      this._a.BeforeOperation = Cut;
      this._b.BeforeOperation = Cut;
      try {
        var handle = fs.Open("db.bin", AccessMode.ReadWrite, ShareMode.Read);
        fs.Write(handle, [0xEE], 5, WriteMode.Normal);
        fs.Close(handle);
      } catch (Exception) {
        // the machine went down part-way
      }

      try {
        fs.Dispose();
      } catch (Exception) {
      }

      this._a.BeforeOperation = null;
      this._b.BeforeOperation = null;
      if (fired == 0) {
        step.Should().BeGreaterThan(4, "the clone, its check, its stamp, its flush and its rename are each a step that was cut");
        break;
      }

      this._a.SimulateCrash();
      this._b.SimulateCrash();
      using var recovered = this._Mounted();
      _ReadSnapshotFile(recovered, snapshot, "db.bin").Should().Equal(original, $"power cut at step {step}: the snapshot's content survives");
      foreach (var member in new[] { this._a, this._b })
        member.FilePaths.Should().NotContain(p => p.EndsWith(".TEMP.$DRIVEBENDER", StringComparison.OrdinalIgnoreCase),
          $"power cut at step {step}: recovery sweeps a half-made clone");
    }
  }

}
