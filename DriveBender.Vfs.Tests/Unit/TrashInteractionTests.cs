using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The recycle bin against the parts of the engine that came after it: write-back copies still owed,
/// deferred publishes, members that must not receive a restored file, and a restore that would land
/// on a name already in use (§6.14, FR-TRASH).
/// </summary>
[TestFixture]
[Category("Unit")]
public class TrashInteractionTests {

  private static readonly Guid _pool = Guid.Parse("7a7a7a7a-0000-0000-0000-000000000002");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;
  private FakeVolumeIO _c = null!;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 22);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 22);
    this._c = new(Guid.NewGuid(), "c", "PHYS-C", capacity: 1L << 22);
  }

  private PoolFileSystem _Mounted(string config, params EngineMember[] members) {
    var cache = new CacheInstance("ti" + Guid.NewGuid().ToString("N"),
      new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, members.Length > 0 ? members : [new(this._a), new EngineMember(this._b)], cache,
      ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static byte[] _ReadLive(PoolFileSystem fs, string path) {
    var length = (int)fs.GetAttributes(path).Length;
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var buffer = new byte[length];
      var read = fs.Read(handle, buffer, 0);
      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  private static bool _HoldsLive(FakeVolumeIO member, string path) => member.FileExists(path, false) || member.FileExists(path, true);

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenAnAcknowledgedWriteIsStillOwedToTheCopyKeptInTheBin_ThenTheRestoredFileHoldsIt() {
    // Only one copy goes to the bin (dropDuplicatesInTrash), and it was simply the first one — which
    // can be the copy still owed an acknowledged write. The owed bytes were then discarded with the
    // delete, so restoring brought back a file the pool had never held.
    using var fs = this._Mounted("""{ "duplication": 3, "trash": { "enabled": true } }""", new EngineMember(this._a), new EngineMember(this._b), new EngineMember(this._c));
    _Write(fs, "ledger.db", new byte[48]);
    var handle = fs.Open("ledger.db", AccessMode.ReadWrite, ShareMode.Read);
    fs.Write(handle, [9, 9], 16, WriteMode.Normal); // block 1: acknowledged on the two shadows, owed to the primary
    fs.Close(handle);
    fs.WriteBuffer.IsDirty("ledger.db").Should().BeTrue("the premise: a copy is still owed the acknowledged write");

    fs.Unlink("ledger.db");
    fs.RestoreFromTrash("ledger.db");

    var expected = new byte[48];
    expected[16] = expected[17] = 9;
    _ReadLive(fs, "ledger.db").Should().Equal(expected, "the bin keeps what the pool acknowledged, not what one copy happened to hold");
  }

  [Test]
  [Category("EdgeCase")]
  public void Delete_GivenAClosedFileWhosePublishIsStillPending_ThenItGoesToTheBin() {
    // A closed file under the performance policy is published in the background. Deleting it in
    // between took the "never finished writing" branch — drop the temps, it never existed — and the
    // bin never saw a file its writer had closed.
    using var fs = this._Mounted("""{ "duplication": 1, "trash": { "enabled": true }, "write": { "policy": "performance" } }""");
    _Write(fs, "saved.doc", [3, 1, 4]);

    fs.Unlink("saved.doc");

    fs.Trash.List().Should().ContainSingle(e => e.OriginalPath == "saved.doc");
    fs.RestoreFromTrash("saved.doc");
    _ReadLive(fs, "saved.doc").Should().Equal(new byte[] { 3, 1, 4 });
  }

  [Test]
  [Category("HappyPath")]
  public void Delete_GivenAFileStillOpenForItsFirstWrite_ThenItNeverExistedAndTheBinStaysEmpty() {
    // the other side of that line, unchanged: a file its writer never finished is not a deletion to undo
    using var fs = this._Mounted("""{ "duplication": 1, "trash": { "enabled": true }, "write": { "policy": "performance" } }""");
    var handle = fs.Create("partial.tmp", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1], 0, WriteMode.Normal);

    fs.Unlink("partial.tmp");
    fs.Close(handle);

    fs.Trash.List().Should().BeEmpty();
  }

  [Test]
  [Category("Exception")]
  public void Restore_GivenTheNameHasBeenUsedAgain_ThenItIsRefusedAndBothFilesSurvive() {
    // Restoring renamed the binned file over whatever held the name now: on the same disk it replaced
    // the new file outright, on another disk it made a second primary beside it. Either way a file
    // was lost or the pool was left disagreeing with itself.
    using var fs = this._Mounted("""{ "duplication": 1, "trash": { "enabled": true } }""");
    _Write(fs, "notes.txt", [1]);
    fs.Unlink("notes.txt");
    _Write(fs, "notes.txt", [2, 2]);

    var restore = () => fs.RestoreFromTrash("notes.txt");

    restore.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
    _ReadLive(fs, "notes.txt").Should().Equal(new byte[] { 2, 2 }, "the file that holds the name now is untouched");
    new[] { this._a, this._b }.Count(m => m.FileExists("notes.txt", false)).Should().Be(1, "and it is still the only primary");
    fs.Trash.List().Should().ContainSingle(e => e.OriginalPath == "notes.txt", "and the binned one is still restorable");
  }

  [Test]
  [Category("Exception")]
  public void Restore_GivenTheNameIsBeingWrittenAgain_ThenItIsRefused() {
    using var fs = this._Mounted("""{ "duplication": 1, "trash": { "enabled": true }, "write": { "policy": "performance" } }""");
    _Write(fs, "notes.txt", [1]);
    fs.Unlink("notes.txt");
    var writer = fs.Create("notes.txt", NodeKind.File, CreateFlags.None);
    fs.Write(writer, [5], 0, WriteMode.Normal);

    var restore = () => fs.RestoreFromTrash("notes.txt");

    restore.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
    fs.Close(writer);
    while (fs.PublishOneDeferredStripe()) {
    }

    _ReadLive(fs, "notes.txt").Should().Equal(new byte[] { 5 });
  }

  [TestCase(MemberRole.Idle, TestName = "Restore_GivenTheBinnedFileIsOnAnIdleMember_ThenItIsRestoredOntoAMemberThatTakesFiles")]
  [TestCase(MemberRole.ReadOnly, TestName = "Restore_GivenTheBinnedFileIsOnAReadOnlyMember_ThenItIsRestoredOntoAMemberThatTakesFiles")]
  [Category("EdgeCase")]
  public void Restore_GivenTheBinnedFileIsOnAMemberThatTakesNoFiles_ThenItIsRestoredElsewhere(MemberRole role) {
    // The bin keeps a file on the disk it was deleted from, and restoring renamed it back into place
    // THERE. A disk that has since become idle (being retired) or read-only takes no new files, and a
    // restore is a new file as far as that disk is concerned.
    using (var fs = this._Mounted("""{ "duplication": 1, "trash": { "enabled": true } }""", new EngineMember(this._a), new EngineMember(this._b))) {
      _Write(fs, "doc.txt", [8, 8, 8]);
      fs.Unlink("doc.txt");
    }

    var holder = new[] { this._a, this._b }.Single(m => m.FilePaths.Any(p => p.EndsWith(".trashver", StringComparison.OrdinalIgnoreCase)));
    var other = holder == this._a ? this._b : this._a;

    using var reopened = this._Mounted("""{ "duplication": 1, "trash": { "enabled": true } }""",
      new EngineMember(holder, role), new EngineMember(other));
    reopened.RestoreFromTrash("doc.txt");

    _HoldsLive(holder, "doc.txt").Should().BeFalse($"a {role} member does not receive a restored file");
    other.GetContent("doc.txt", false).Should().Equal(new byte[] { 8, 8, 8 });
    reopened.Trash.List().Should().BeEmpty("the binned copy is gone once the restore has landed");
    holder.FilePaths.Should().NotContain(p => p.Contains("/trash/", StringComparison.OrdinalIgnoreCase), "nothing of it is left behind in the bin");
  }

  [TestCase(PoolTrash.TrashPrefix + "/docs/a.txt.1f.trashver", ".trashinfo",
    TestName = "Retire_GivenABinEntry_WhenItsDiskIsRemoved_ThenTheFileAndItsSidecarLandOnTheSameMember")]
  [TestCase(PoolSnapshots.SnapshotPrefix + "/versions/docs/a.txt.00000000000000ff.snapver", PoolSnapshots.InfoSuffix,
    TestName = "Retire_GivenASnapshotVersion_WhenItsDiskIsRemoved_ThenTheVersionAndItsSidecarLandOnTheSameMember")]
  [Category("EdgeCase")]
  public void Retire_GivenAKeptFileWithItsSidecar_ThenBothMoveTogether(string keptPath, string sidecarSuffix) {
    // Each file used to go wherever had the most room at that moment, and moving the first changes
    // which member that is: here the file goes to the roomier member, which is then the less roomy
    // one when its sidecar is placed. A version whose sidecar is on another disk is invisible to the
    // store, and the snapshot reports it lost.
    var leaving = new FakeVolumeIO(Guid.NewGuid(), "leaving", "PHYS-L", capacity: 1L << 22);
    var roomy = new FakeVolumeIO(Guid.NewGuid(), "roomy", "PHYS-R", capacity: 100_500);
    var other = new FakeVolumeIO(Guid.NewGuid(), "other", "PHYS-O", capacity: 100_000);
    leaving.Seed(keptPath, false, new byte[2000]);
    leaving.Seed(keptPath + sidecarSuffix, false, "{}"u8.ToArray());

    IVolumeIO[] members = [leaving, roomy, other];
    new MediaLifecycle(members, new(new MemberJournalStore(members)), 1).ScatterAndRemove(leaving.MemberId);

    var holder = new[] { roomy, other }.Single(m => m.FileExists(keptPath, false));
    holder.FileExists(keptPath + sidecarSuffix, false).Should().BeTrue("the sidecar is on the disk that holds the file it describes");
    new[] { roomy, other }.Where(m => m != holder).Should().OnlyContain(m => !m.FileExists(keptPath + sidecarSuffix, false),
      "and nowhere else");
    leaving.FileExists(keptPath, false).Should().BeFalse();
    leaving.FileExists(keptPath + sidecarSuffix, false).Should().BeFalse();
  }

  [Test]
  [Category("EdgeCase")]
  public void Retire_GivenASidecarWhoseFileIsGone_ThenItStillMovesOff() {
    var leaving = new FakeVolumeIO(Guid.NewGuid(), "leaving", "PHYS-L", capacity: 1L << 22);
    var staying = new FakeVolumeIO(Guid.NewGuid(), "staying", "PHYS-S", capacity: 1L << 22);
    const string orphan = PoolTrash.TrashPrefix + "/b.txt.2a.trashver.trashinfo";
    leaving.Seed(orphan, false, "{}"u8.ToArray());

    IVolumeIO[] members = [leaving, staying];
    new MediaLifecycle(members, new(new MemberJournalStore(members)), 1).ScatterAndRemove(leaving.MemberId);

    staying.FileExists(orphan, false).Should().BeTrue("a lone sidecar is moved as it is, rather than left behind");
  }

  [Test]
  [Category("EdgeCase")]
  public void Restore_GivenTheBinnedFileIsOnAnIdleMember_WhenThePowerIsCutAtEveryStep_ThenTheFileIsNeverLost() {
    const string config = """{ "duplication": 1, "trash": { "enabled": true } }""";
    byte[] content = [.. Enumerable.Range(0, 3000).Select(i => (byte)i)];
    for (var step = 1; ; ++step) {
      this.SetUp();
      using (var fs = this._Mounted(config, new EngineMember(this._a), new EngineMember(this._b))) {
        _Write(fs, "doc.txt", content);
        fs.Unlink("doc.txt");
      }

      var holder = new[] { this._a, this._b }.Single(m => m.FilePaths.Any(p => p.EndsWith(".trashver", StringComparison.OrdinalIgnoreCase)));
      var other = holder == this._a ? this._b : this._a;

      var fired = 0;
      var remaining = step;
      void Cut(VolumeOp op, string path) {
        if (Volatile.Read(ref fired) == 1 || Interlocked.Decrement(ref remaining) == 0) {
          Interlocked.Exchange(ref fired, 1);
          throw new InvalidOperationException("power loss");
        }
      }

      var restoring = this._Mounted(config, new EngineMember(holder, MemberRole.Idle), new EngineMember(other));
      holder.BeforeOperation = Cut;
      other.BeforeOperation = Cut;
      try {
        restoring.RestoreFromTrash("doc.txt");
      } catch (Exception) {
        // the machine went down part-way
      }

      try {
        restoring.Dispose();
      } catch (Exception) {
      }

      holder.BeforeOperation = null;
      other.BeforeOperation = null;
      if (fired == 0) {
        step.Should().BeGreaterThan(4, "moving the entry and restoring it are several steps, each one cut");
        break;
      }

      holder.SimulateCrash();
      other.SimulateCrash();
      using var recovered = this._Mounted(config, new EngineMember(holder, MemberRole.Idle), new EngineMember(other));
      if (!other.FileExists("doc.txt", false)) {
        recovered.Trash.List().Should().Contain(e => e.OriginalPath == "doc.txt", $"power cut at step {step}: not restored, so still in the bin");
        recovered.RestoreFromTrash("doc.txt");
      }

      _ReadLive(recovered, "doc.txt").Should().Equal(content, $"power cut at step {step}: the file is whole");
      _HoldsLive(holder, "doc.txt").Should().BeFalse($"power cut at step {step}: never restored onto the idle member");
    }
  }

  [Test]
  [Category("Exception")]
  public void Restore_GivenOnlyAMemberThatTakesNoFilesRemains_ThenItIsRefusedAndTheBinKeepsTheFile() {
    using (var fs = this._Mounted("""{ "duplication": 1, "trash": { "enabled": true } }""", new EngineMember(this._a))) {
      _Write(fs, "doc.txt", [8]);
      fs.Unlink("doc.txt");
    }

    using var reopened = this._Mounted("""{ "duplication": 1, "trash": { "enabled": true } }""", new EngineMember(this._a, MemberRole.Idle), new EngineMember(this._b, MemberRole.ReadOnly));
    var restore = () => reopened.RestoreFromTrash("doc.txt");

    restore.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
    reopened.Trash.List().Should().ContainSingle("a restore that has nowhere to go leaves the file where it was");
  }

}
