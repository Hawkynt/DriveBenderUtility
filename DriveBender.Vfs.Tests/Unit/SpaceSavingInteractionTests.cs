using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The space optimizer against the recycle bin and the snapshot store (docs/SpaceSavings.md,
/// docs/Snapshots.md). What is kept there is the only copy of what it holds: the optimizer must never
/// touch it, and a kept file that shares its data with a live one (a hard link the optimizer made
/// before the file was binned or set aside) must never be reached by a change to that live file.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SpaceSavingInteractionTests {

  private static readonly Guid _pool = Guid.Parse("5a7e5a7e-0000-0000-0000-000000000004");

  private const BackendCaps _SAVING = BackendCaps.RandomRead | BackendCaps.RandomWrite | BackendCaps.AtomicRename | BackendCaps.DurableFlush
                                      | BackendCaps.List | BackendCaps.Delete | BackendCaps.Timestamps
                                      | BackendCaps.HardLinks | BackendCaps.BlockClone | BackendCaps.Sparse;

  private static readonly byte[] _TWIN = [.. Enumerable.Range(0, 96 * 1024).Select(i => (byte)(i * 7 + 1))];

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24) { Caps = _SAVING & ~BackendCaps.BlockClone }; // links, so linked names are visible
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24) { Caps = _SAVING };
  }

  private PoolFileSystem _Engine(string config = """{ "duplication": 1, "trash": { "enabled": true } }""") {
    var cache = new CacheInstance("ss" + Guid.NewGuid().ToString("N"), new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache, ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static byte[] _ReadSnapshotFile(PoolFileSystem fs, Guid id, string path) {
    using var stream = fs.OpenSnapshotFile(id, path);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static void _Poke(PoolFileSystem fs, string path) {
    var handle = fs.Open(path, AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(handle, [0xEE, 0xEE], 10, WriteMode.Normal);
    fs.Close(handle);
  }

  /// <summary>a.bin and b.bin: two files, one set of data on v1.</summary>
  private void _Twins() {
    this._v1.Seed("a.bin", false, _TWIN);
    this._v1.TryHardLink("a.bin", false, "b.bin", false).Should().BeTrue();
    this._v1.LinkCount("a.bin", false).Should().Be(2, "the premise: the two files share their data");
  }

  [TestCase("v1", TestName = "Optimizer_GivenTheBinAndTheStoreHoldTwinsOfLiveFilesOnALinkingMember_ThenItNeverTouchesThem")]
  [TestCase("v2", TestName = "Optimizer_GivenTheBinAndTheStoreHoldTwinsOfLiveFilesOnACloningMember_ThenItNeverTouchesThem")]
  [Category("HappyPath")]
  public void Optimizer_GivenKeptFilesIdenticalToLiveOnes_ThenItNeverTouchesThem(string memberName) {
    var member = memberName == "v1" ? this._v1 : this._v2;
    member.Seed("a.bin", false, _TWIN);
    member.Seed("b.bin", false, _TWIN); // a live pair: proves the pass runs and does share live files
    member.Seed(PoolTrash.TrashPrefix + "/c.bin.1f.trashver", false, _TWIN);
    member.Seed(PoolTrash.TrashPrefix + "/zeros.bin.2f.trashver", false, new byte[3 << 20]); // a sparsify candidate
    member.Seed(PoolSnapshots.SnapshotPrefix + "/versions/d.bin.00000000000000ff.snapver", false, _TWIN);
    member.Seed(PoolSnapshots.SnapshotPrefix + "/versions/zeros.bin.00000000000001ff.snapver", false, new byte[3 << 20]);
    using var fs = this._Engine();

    var touched = new List<string>();
    member.BeforeOperation = (op, path) => {
      if (op is VolumeOp.OpenWrite or VolumeOp.AtomicReplace or VolumeOp.Truncate or VolumeOp.Delete or VolumeOp.SetTimestamps
          && (path.StartsWith(PoolTrash.TrashPrefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(PoolSnapshots.SnapshotPrefix, StringComparison.OrdinalIgnoreCase)))
        lock (touched)
          touched.Add($"{op} {path}");
    };

    var report = fs.OptimizeSpace();
    member.BeforeOperation = null;

    report.FilesDeduplicated.Should().BeGreaterThan(0, "the pass ran and shared the live twins");
    touched.Should().BeEmpty("nothing kept in the bin or the snapshot store is ever changed by the optimizer");
    member.LinkCount(PoolTrash.TrashPrefix + "/c.bin.1f.trashver", false).Should().Be(1);
    member.LinkCount(PoolSnapshots.SnapshotPrefix + "/versions/d.bin.00000000000000ff.snapver", false).Should().Be(1);
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenABinnedFileSharesItsDataWithALiveOne_ThenWritingTheLiveOneLeavesTheBinnedOneAlone() {
    this._Twins();
    using var fs = this._Engine();

    fs.Unlink("a.bin"); // renamed into the bin: still one set of data with b.bin
    _Poke(fs, "b.bin");
    fs.RestoreFromTrash("a.bin");

    this._v1.GetContent("a.bin", false).Should().Equal(_TWIN, "the bin kept what was deleted, not what its twin became");
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenASetAsideVersionSharesItsDataWithALiveFile_ThenWritingTheLiveFileLeavesTheVersionAlone() {
    this._Twins();
    using var fs = this._Engine("""{ "duplication": 1, "trash": { "enabled": false } }""");
    var taken = fs.TakeSnapshot("s");

    fs.Unlink("a.bin"); // renamed into the store: still one set of data with b.bin
    _Poke(fs, "b.bin");
    fs.SetAttributes("b.bin", new(LastWriteTimeUtc: new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

    _ReadSnapshotFile(fs, taken.Id, "a.bin").Should().Equal(_TWIN, "the version is what the snapshot saw, whatever its twin became");
  }

  [TestCase(false, TestName = "Restore_GivenTheLiveFileSharesItsDataWithAnother_ThenTheOtherKeepsItsContent")]
  [TestCase(true, TestName = "Restore_GivenTheLiveFileSharesItsDataAndANewerSnapshotPinsIt_ThenTheOtherKeepsItsContent")]
  [Category("EdgeCase")]
  public void Restore_GivenTheLiveFileIsHardLinkedToAnother_ThenOnlyTheRestoredFileChanges(bool pinnedByANewerSnapshot) {
    using var fs = this._Engine("""{ "duplication": 1, "trash": { "enabled": false } }""");
    byte[] old = [.. Enumerable.Range(0, 4096).Select(i => (byte)(255 - i % 200))];
    _Write(fs, "a.bin", old);
    var first = fs.TakeSnapshot("first");
    _Write(fs, "a.bin", _TWIN);

    var holder = new[] { this._v1, this._v2 }.Single(m => m.FileExists("a.bin", false));
    holder.TryHardLink("a.bin", false, "b.bin", false).Should().BeTrue("the premise: the live file shares its data with b.bin");
    var newer = pinnedByANewerSnapshot ? fs.TakeSnapshot("newer") : null;

    fs.RestoreFromSnapshot(first.Id, "a.bin");

    holder.GetContent("b.bin", false).Should().Equal(_TWIN, "restoring one name must never write through the link into the other");
    var handle = fs.Open("a.bin", AccessMode.Read, ShareMode.Read);
    var restored = new byte[old.Length + 1];
    fs.Read(handle, restored, 0).Should().Be(old.Length);
    fs.Close(handle);
    restored[..old.Length].Should().Equal(old, "the restored name holds the old content, wherever it was placed");
    if (newer != null)
      _ReadSnapshotFile(fs, newer.Id, "a.bin").Should().Equal(_TWIN, "and the newer snapshot still holds what was live");
  }

}
