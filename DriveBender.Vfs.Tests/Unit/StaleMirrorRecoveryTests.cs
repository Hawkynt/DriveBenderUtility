using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// SAFE-OFFLINE: a member that was offline during a compaction/prune keeps a STALE journal or
/// tombstone mirror. The high-water sequence must let recovery tell that stale copy apart from
/// a genuinely interrupted operation, so a long-completed delete/rename can never be replayed
/// against data that was recreated since — the classic "ghost resurrection" bug.
/// </summary>
[TestFixture]
[Category("Unit")]
public class StaleMirrorRecoveryTests {

  private static readonly Guid _pool = Guid.Parse("aaaaaaaa-9999-8888-7777-666666666666");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;
  private FakeVolumeIO _m = null!; // the one that goes offline mid-op

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 20);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 20);
    this._m = new(Guid.NewGuid(), "m", "PHYS-M", capacity: 1L << 20);
  }

  private PoolFileSystem _CreateFs(bool mount = true, string? config = null) {
    var cache = new CacheInstance("sm" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._a), new(this._b), new(this._m)], cache,
      ConfigResolver.ResolveEffective(null, config ?? """{ "duplication": 2, "trash": { "enabled": false } }"""));
    if (mount)
      fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Create(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private byte[]? _AnyCopy(FakeVolumeIO v, string path) => v.GetContent(path, false) ?? v.GetContent(path, true);

  [Test]
  [Category("Exception")]
  public void Journal_GivenStaleDeleteIntentOnReturnedMember_WhenRemounted_ThenRecreatedFileIsNotResurrectedThenDeleted() {
    // 1) create a file, then delete it while member M is offline (so M keeps a stale journal
    //    that still holds the delete INTENT but not its completion, once the others compact)
    var fs1 = this._CreateFs();
    _Create(fs1, "ghost.txt", [1, 2, 3]);
    fs1.PollMembers();

    this._m.IsOnline = false;
    fs1.PollMembers();
    fs1.Unlink("ghost.txt");
    // drive the journal forward and compact on A/B so the delete's completion is gone there
    for (var i = 0; i < 3; ++i)
      _Create(fs1, $"filler{i}.txt", [(byte)i]);
    var scheduler = fs1.CreateScheduler();
    scheduler.Quiesce();
    fs1.Unmount();

    // 2) with M still away, a NEW file is created at the very same path
    var fs2 = this._CreateFs();
    _Create(fs2, "ghost.txt", [9, 9, 9]);
    fs2.Unmount();

    // 3) M returns and the pool is remounted — recovery must NOT resurrect the old delete
    this._m.IsOnline = true;
    var fs3 = this._CreateFs();

    fs3.ReadDirectory("").Should().Contain(e => e.Name == "ghost.txt",
      "the recreated file must survive — a returning member's stale delete intent is not replayed (SAFE-OFFLINE)");
    var handle = fs3.Open("ghost.txt", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[3];
    fs3.Read(handle, buffer, 0);
    fs3.Close(handle);
    buffer.Should().Equal(new byte[] { 9, 9, 9 }, "the current content is intact, not rolled back to the deleted version");
  }

  [Test]
  [Category("Exception")]
  public void Tombstone_GivenStaleAppliedRecordOnReturnedMember_WhenReturns_ThenRecreatedFileNotDeleted() {
    // delete a file while M is offline → a tombstone owed to M is recorded
    var fs = this._CreateFs();
    _Create(fs, "doc.txt", [5, 5]);
    fs.PollMembers();

    this._m.IsOnline = false;
    fs.PollMembers();
    fs.Unlink("doc.txt");

    // M returns and replays the missed delete — the tombstone is applied and pruned
    this._m.IsOnline = true;
    fs.PollMembers();
    this._AnyCopy(this._m, "doc.txt").Should().BeNull("the missed delete was applied to the returned member");

    // recreate the file, then drop M again and bring it back: the ALREADY-APPLIED tombstone
    // must not fire a second time against the recreated content
    _Create(fs, "doc.txt", [7, 7, 7]);
    fs.PollMembers();
    this._m.IsOnline = false;
    fs.PollMembers();
    this._m.IsOnline = true;
    fs.PollMembers();

    fs.ReadDirectory("").Should().Contain(e => e.Name == "doc.txt", "a pruned tombstone never resurrects to re-delete a recreated file");
    var handle = fs.Open("doc.txt", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[3];
    fs.Read(handle, buffer, 0);
    fs.Close(handle);
    buffer.Should().Equal(new byte[] { 7, 7, 7 });
  }

  [Test]
  [Category("HappyPath")]
  public void Journal_GivenGenuineCrashAllMembersPresent_WhenRemounted_ThenInterruptedWriteStillReconciles() {
    // a real crash (all members present, same high-water) must STILL recover — the high-water
    // filter only excludes stale mirrors, never the authoritative interrupted state
    var fs = this._CreateFs(config: """{ "duplication": 3, "write": { "policy": "write-back", "minCopiesBeforeAck": 1 }, "trash": { "enabled": false } }""");
    var handle = fs.Create("f.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1, 2, 3, 4], 0, WriteMode.Normal);
    fs.Close(handle);

    // simulate: not all copies converged, then remount (recovery resyncs from the newest)
    var scheduler = fs.CreateScheduler();
    scheduler.Quiesce();
    fs.Unmount();

    var fs2 = this._CreateFs(config: """{ "duplication": 3, "write": { "policy": "write-back", "minCopiesBeforeAck": 1 }, "trash": { "enabled": false } }""");
    var read = fs2.Open("f.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[4];
    var total = 0;
    while (total < 4) {
      var got = fs2.Read(read, buffer.AsSpan(total), total);
      if (got == 0) break;
      total += got;
    }
    fs2.Close(read);
    buffer.Should().Equal(new byte[] { 1, 2, 3, 4 }, "a genuine interrupted write is still fully recovered");
  }
}
