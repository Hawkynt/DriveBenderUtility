using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Ways the engine lost data it had acknowledged, or that a feature had promised to keep: an fsync
/// that did not cover a truncate, a user file swept as a temp, a save dropped when the bin could not
/// take the old version, copies left divergent by a crash between renames, a restore that destroyed
/// the live file, snapshots left unprotected while their pins were rebuilt or while one was taken,
/// and a write that went into a file the space optimizer had hard-linked to another.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DataLossAuditTests {

  private static readonly Guid _pool = Guid.Parse("5afe0e00-0000-0000-0000-0000000000f9");
  private static readonly byte[] _OLD = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 3 + 1))];
  private static readonly byte[] _NEW = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 2))];

  private const BackendCaps _LINKING = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.AtomicRename | BackendCaps.DurableFlush
                                       | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps | BackendCaps.HardLinks;

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", 1L << 24);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", 1L << 24);
  }

  private PoolFileSystem _Engine(string extra = "", int duplication = 2) {
    var cache = new CacheInstance("dla" + Guid.NewGuid().ToString("N"),
      new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache,
      TestConfig.Resolve($$"""{ "duplication": {{duplication}}, "readAhead": { "enabled": false } {{extra}} }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
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

  private static byte[] _ReadAll(Stream stream) {
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private byte[]? _CopyOn(FakeVolumeIO volume, string path) => volume.GetContent(path, false) ?? volume.GetContent(path, true);

  [Test]
  [Category("EdgeCase")]
  public void Truncate_GivenItWasFsynced_WhenThePowerFails_ThenTheNewLengthSurvives() {
    using (var seeding = this._Engine())
      _Write(seeding, "f.bin", _OLD);

    var fs = this._Engine();
    var handle = fs.Open("f.bin", AccessMode.ReadWrite, ShareMode.Read);
    fs.SetLength(handle, 100);
    fs.Flush(handle);
    fs.Close(handle);
    this._v1.SimulateCrash();
    this._v2.SimulateCrash();

    using var recovered = this._Engine();
    recovered.GetAttributes("f.bin").Length.Should().Be(100, "an fsynced truncate must survive a power cut");
    this._CopyOn(this._v1, "f.bin").Should().HaveCount(100);
    this._CopyOn(this._v2, "f.bin").Should().HaveCount(100);
  }

  [TestCase(true, TestName = "Publish_WhenThePowerFailsBetweenTheRenamesOfTwoCopies_ThenTheCopiesAgree(checksumDb)")]
  [TestCase(false, TestName = "Publish_WhenThePowerFailsBetweenTheRenamesOfTwoCopies_ThenTheCopiesAgree(noChecksumDb)")]
  [Category("EdgeCase")]
  public void Publish_WhenThePowerFailsBetweenTheRenamesOfTwoCopies_ThenTheCopiesAgree(bool checksumDb) {
    var extra = checksumDb ? "" : """, "integrity": { "checksumDb": false }""";
    using (var seeding = this._Engine(extra))
      _Write(seeding, "doc.bin", _OLD);

    var fs = this._Engine(extra);
    var renames = 0;
    var fired = 0;
    void Cut(VolumeOp op, string path) {
      if (Volatile.Read(ref fired) == 1)
        throw new InvalidOperationException("power loss");
      if (op == VolumeOp.AtomicReplace && path == "doc.bin" && Interlocked.Increment(ref renames) == 2) {
        Interlocked.Exchange(ref fired, 1);
        throw new InvalidOperationException("power loss");
      }
    }

    this._v1.BeforeOperation = Cut;
    this._v2.BeforeOperation = Cut;
    try {
      _Write(fs, "doc.bin", _NEW);
    } catch (Exception) {
      // the power is gone
    }

    try {
      fs.Dispose();
    } catch (Exception) {
      // the power is gone
    }

    this._v1.BeforeOperation = null;
    this._v2.BeforeOperation = null;
    fired.Should().Be(1, "the premise: the cut landed between the two renames");
    this._v1.SimulateCrash();
    this._v2.SimulateCrash();

    using var recovered = this._Engine(extra);
    byte[]?[] copies = [this._CopyOn(this._v1, "doc.bin"), this._CopyOn(this._v2, "doc.bin")];
    copies.Where(c => c != null).Select(c => Convert.ToHexString(c!)).Distinct().Should().HaveCount(1, "every copy agrees after recovery");
  }

  [TestCase("notes.temp.$drivebender")]
  [TestCase("FOLDER.DUPLICATE.$DRIVEBENDER")]
  [TestCase(".drivebenderutility")]
  [Category("EdgeCase")]
  public void Create_GivenANameThePoolReservesForItself_ThenItIsRefusedRatherThanSweptLater(string name) {
    using var fs = this._Engine();

    var file = () => fs.Create(name, NodeKind.File, CreateFlags.None);
    var folder = () => fs.MakeDir(name);
    var renamed = () => {
      _Write(fs, "plain.txt", _NEW);
      fs.Rename("plain.txt", name, RenameFlags.None);
    };

    file.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.InvalidArgument);
    folder.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.InvalidArgument);
    renamed.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.InvalidArgument);
    _Read(fs, "plain.txt").Should().Equal(_NEW, "a refused rename leaves the file where it was");
  }

  [Test]
  [Category("EdgeCase")]
  public void Publish_WhenKeepingTheReplacedVersionInTheBinFails_ThenTheSaveIsStillPublishedOnTheRetry() {
    const string bin = """, "trash": { "enabled": true }""";
    using (var seeding = this._Engine(bin))
      _Write(seeding, "doc.bin", _OLD);

    var fs = this._Engine(bin);
    var handle = fs.Create("doc.bin", NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, _NEW, 0, WriteMode.Normal);
    var failed = 0;
    void Fail(VolumeOp op, string path) {
      if (op == VolumeOp.OpenWrite && path.Contains("trash", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref failed, 1) == 0)
        throw new PoolFsException(PoolFsError.IoError, "transient");
    }

    this._v1.BeforeOperation = Fail;
    this._v2.BeforeOperation = Fail;
    try {
      fs.Close(handle);
    } catch (PoolFsException) {
      // reported to the application; the save itself must not be forgotten
    }

    this._v1.BeforeOperation = null;
    this._v2.BeforeOperation = null;
    failed.Should().Be(1, "the premise: the bin copy failed once");
    fs.Dispose(); // a clean unmount is the documented retry point

    using var recovered = this._Engine(bin);
    _Read(recovered, "doc.bin").Should().Equal(_NEW, "the save must be published on its retry, not swept as an abandoned temp");
  }

  [Test]
  [Category("EdgeCase")]
  public void RestoreFromTrash_GivenTheNameIsHeldByAFileTheBinWouldNotTake_ThenThatFileIsNotDestroyed() {
    using var fs = this._Engine(""", "trash": { "enabled": true, "maxFileSize": "1KiB" }""", duplication: 1);
    _Write(fs, "f", new byte[500]);
    fs.Unlink("f");
    var live = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
    _Write(fs, "f", live);

    var restore = () => fs.RestoreFromTrash("f");

    restore.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
    _Read(fs, "f").Should().Equal(live, "the live file is too large for the bin, so swapping it out would delete it for good");
    fs.Trash.NewestVersionOf("f").Should().NotBeNull("the older version is still there to restore once the name is free");
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenASingleCopyFileASnapshotNames_ThenItStillGoesToTheBinAndOutlivesTheSnapshot() {
    using var fs = this._Engine(""", "trash": { "enabled": true }""", duplication: 1);
    _Write(fs, "f.bin", _NEW);
    var snapshot = fs.TakeSnapshot("s");

    fs.Unlink("f.bin");
    fs.Trash.List().Should().Contain(e => e.OriginalPath == "f.bin", "the bin keeps deleted files, whether or not a snapshot also names them");
    _ReadAll(fs.OpenSnapshotFile(snapshot.Id, "f.bin")).Should().Equal(_NEW);

    fs.DeleteSnapshot(snapshot.Id);
    fs.RestoreFromTrash("f.bin");
    _Read(fs, "f.bin").Should().Equal(_NEW);
  }

  [Test]
  [Category("EdgeCase")]
  public void DeleteSnapshot_WhileTheOthersArePinnedAgain_ThenAWriteInBetweenIsStillPreservedForThem() {
    using var fs = this._Engine("", duplication: 1);
    _Write(fs, "f.bin", _OLD);
    var keep = fs.TakeSnapshot("keep");
    var drop = fs.TakeSnapshot("drop");

    // The pins are rebuilt after the snapshot is gone, by reading every index that is left. The write
    // must land inside THAT walk: earlier, the old pins still protect the file. So the reads the
    // delete itself makes are counted first, by repeating the walk the rebuild will do.
    var indexReads = 0;
    void CountIndexReads(VolumeOp op, string path) {
      if (op == VolumeOp.OpenRead && path.Contains("snapshots/index", StringComparison.OrdinalIgnoreCase))
        Interlocked.Increment(ref indexReads);
    }

    var wrote = 0;
    var inside = 0;
    var readsBeforeRebuild = -1;
    void WriteDuringRebuild(VolumeOp op, string path) {
      if (op != VolumeOp.OpenRead || !path.Contains("snapshots/index", StringComparison.OrdinalIgnoreCase) || Volatile.Read(ref inside) == 1)
        return;

      if (Interlocked.Increment(ref indexReads) <= readsBeforeRebuild || Interlocked.Exchange(ref wrote, 1) == 1)
        return;

      Volatile.Write(ref inside, 1);
      _Write(fs, "f.bin", _NEW);
    }

    // the delete's own reads: run the same delete on an identical pool first
    {
      var a = new FakeVolumeIO(Guid.NewGuid(), "a", "PHYS-A", 1L << 24);
      var b = new FakeVolumeIO(Guid.NewGuid(), "b", "PHYS-B", 1L << 24);
      (this._v1, this._v2, var keepV1, var keepV2) = (a, b, this._v1, this._v2);
      using (var twin = this._Engine("", duplication: 1)) {
        _Write(twin, "f.bin", _OLD);
        twin.TakeSnapshot("keep");
        var twinDrop = twin.TakeSnapshot("drop");
        a.BeforeOperation = CountIndexReads;
        b.BeforeOperation = CountIndexReads;
        twin.Snapshots.Delete(twinDrop.Id);
        a.BeforeOperation = null;
        b.BeforeOperation = null;
      }

      (this._v1, this._v2) = (keepV1, keepV2);
    }

    readsBeforeRebuild = indexReads;
    indexReads = 0;
    this._v1.BeforeOperation = WriteDuringRebuild;
    this._v2.BeforeOperation = WriteDuringRebuild;
    fs.DeleteSnapshot(drop.Id);
    this._v1.BeforeOperation = null;
    this._v2.BeforeOperation = null;

    wrote.Should().Be(1, "the premise: a write landed while the snapshot pins were rebuilt");
    _ReadAll(fs.OpenSnapshotFile(keep.Id, "f.bin")).Should().Equal(_OLD, "the surviving snapshot still holds the file as it was");
  }

  [Test]
  [Category("EdgeCase")]
  public void TakeSnapshot_WhileAVersionIsBeingPreserved_ThenTheNewSnapshotIsProtectedToo() {
    using var fs = this._Engine("", duplication: 1);
    _Write(fs, "f.bin", _OLD);
    fs.TakeSnapshot("first");

    // an in-place edit preserves by copy; the second snapshot is taken while that copy is recorded
    Task<SnapshotEntry>? second = null;
    void SnapshotDuringPreserve(VolumeOp op, string path) {
      if (op == VolumeOp.OpenWrite && path.EndsWith(PoolSnapshots.InfoSuffix, StringComparison.OrdinalIgnoreCase) && second == null) {
        second = Task.Run(() => fs.TakeSnapshot("second"));
        second.Wait(TimeSpan.FromMilliseconds(200)); // long enough to land inside the preserve, unless it is excluded
      }
    }

    this._v1.BeforeOperation = SnapshotDuringPreserve;
    this._v2.BeforeOperation = SnapshotDuringPreserve;
    var handle = fs.Open("f.bin", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(handle, [0x55, 0x55], 0, WriteMode.Normal);
    fs.Close(handle);
    this._v1.BeforeOperation = null;
    this._v2.BeforeOperation = null;

    second.Should().NotBeNull("the premise: a snapshot was taken while a version was preserved");
    second!.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
    var asSecondSawIt = _Read(fs, "f.bin");
    _Write(fs, "f.bin", _NEW); // a later edit, which must be preserved for the second snapshot

    _ReadAll(fs.OpenSnapshotFile(second.Result.Id, "f.bin")).Should().Equal(asSecondSawIt, "the second snapshot keeps the file as it was when taken");
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenTheOptimizerLinkedTheFileAfterTheApplicationClosedIt_ThenTheOtherFileIsUntouched() {
    // On Windows the kernel's close follows the application's much later, often at unmount, and the
    // file's state lives until then. Its "already separated" flag outlived the application handle, so
    // a later write went in place into data the optimizer had shared with another file.
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", 1L << 24) { Caps = _LINKING };
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", 1L << 24) { Caps = _LINKING };
    var twin = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i * 11 + 5)).ToArray(); // over the optimizer's minimum
    this._v1.Seed("a.bin", false, twin);
    this._v1.Seed("b.bin", false, twin);
    using var fs = this._Engine("", duplication: 1);

    var first = fs.Open("b.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
    fs.Write(first, twin.AsSpan(0, 16), 0, WriteMode.Normal); // same bytes: the file stays a twin
    fs.MarkApplicationClosed(first); // the kernel has not closed it yet
    var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    fs.SetAttributes("a.bin", new(CreationTimeUtc: stamp, LastWriteTimeUtc: stamp));
    fs.SetAttributes("b.bin", new(CreationTimeUtc: stamp, LastWriteTimeUtc: stamp));
    fs.OptimizeSpace();
    this._v1.LinkCount("a.bin", false).Should().Be(2, "the premise: the optimizer shared the two files' data");

    var second = fs.Open("b.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
    fs.Write(second, [0xFF, 0xFF], 0, WriteMode.Normal);
    fs.Close(second);
    fs.Close(first);

    this._v1.GetContent("a.bin", false).Should().Equal(twin, "a write to one file must never change another");
    this._v1.GetContent("b.bin", false)![0].Should().Be(0xFF);
  }

  [Test]
  [Category("EdgeCase")]
  public void Scrub_WhenAnApplicationWritesWhileACopyIsRepaired_ThenTheWriteReachesEveryCopy() {
    using var fs = this._Engine();
    _Write(fs, "doc.bin", _OLD);
    fs.RunScrub(); // the baseline

    // edited on another machine: one copy's content and time move on, so the scrub repairs the other from it
    var holder = this._v1.FileExists("doc.bin", false) ? this._v1 : this._v2;
    var other = holder == this._v1 ? this._v2 : this._v1;
    holder.CorruptSilently("doc.bin", false, content => content[100] = 0x42);
    holder.SetTimestamps("doc.bin", false, null, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    Task? write = null;
    other.BeforeOperation = (op, path) => {
      if (op != VolumeOp.AtomicReplace || path != "doc.bin" || write != null)
        return;

      write = Task.Run(() => {
        var handle = fs.Open("doc.bin", AccessMode.ReadWrite, ShareMode.Read);
        fs.Write(handle, [0xAB, 0xCD], 0, WriteMode.Normal);
        fs.Close(handle);
      });
      write.Wait(TimeSpan.FromMilliseconds(300)); // long enough to land inside the repair, unless it waits for it
    };

    fs.RunScrub();
    other.BeforeOperation = null;

    write.Should().NotBeNull("the premise: the scrub repaired a copy while the application wrote");
    write!.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
    foreach (var volume in new[] { this._v1, this._v2 }) {
      var copy = this._CopyOn(volume, "doc.bin")!;
      copy[..2].Should().Equal([0xAB, 0xCD], $"the acknowledged write must be on {volume.DisplayName}'s copy too");
      copy[100].Should().Be(0x42, "and so must the accepted edit");
    }
  }

}
