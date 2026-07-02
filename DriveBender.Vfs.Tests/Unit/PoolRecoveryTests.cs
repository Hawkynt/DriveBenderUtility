using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Crash-consistency (TST-FAULT): interrupted operations at every intent/mutate/complete
/// boundary must recover to a consistent state, idempotently (SAFE-NOLOSS, SAFE-IDEMP).
/// </summary>
[TestFixture]
[Category("Unit")]
public class PoolRecoveryTests {

  private FakeVolumeIO _volume1 = null!;
  private FakeVolumeIO _volume2 = null!;
  private Journal _journal = null!;

  [SetUp]
  public void SetUp() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1");
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2");
    this._journal = new(new MemberJournalStore([this._volume1, this._volume2]));
  }

  private PoolRecovery _Recovery() => new([this._volume1, this._volume2], this._journal);

  [Test]
  [Category("HappyPath")]
  public void Run_GivenWriteAppliedToPrimaryOnly_WhenRecovered_ThenShadowResyncedFromPrimary() {
    // crash between mutating the primary and the shadow: copies diverge
    this._volume1.Seed("f.bin", false, [9, 9, 9]);
    this._volume2.Seed("f.bin", true, [1, 1, 1]);
    this._journal.LogIntent(JournalOp.Write, "f.bin", offset: 0, length: 3);

    var report = this._Recovery().Run();

    report.Reconciled.Should().Be(1);
    this._volume2.GetContent("f.bin", true).Should().Equal(new byte[] { 9, 9, 9 }, "the primary is authoritative after an interrupted write");
  }

  [Test]
  [Category("HappyPath")]
  public void Run_GivenDeleteAppliedToPrimaryOnly_WhenRecovered_ThenSurvivingShadowRemoved() {
    // primary deleted, then crash — the shadow copy is an orphan
    this._volume2.Seed("f.bin", true, [1]);
    this._journal.LogIntent(JournalOp.Delete, "f.bin");

    this._Recovery().Run();

    this._volume2.FileExists("f.bin", true).Should().BeFalse("delete rolls forward: no orphan copies (FR-DELETE)");
  }

  [Test]
  [Category("HappyPath")]
  public void Run_GivenRenameMovedOnOneMemberOnly_WhenRecovered_ThenRenameCompletedEverywhere() {
    this._volume1.Seed("new.bin", false, [1]); // member 1 already moved
    this._volume2.Seed("old.bin", true, [1]);  // member 2's shadow still under the old name
    this._journal.LogIntent(JournalOp.Rename, "old.bin", "new.bin");

    this._Recovery().Run();

    this._volume2.FileExists("old.bin", true).Should().BeFalse();
    this._volume2.FileExists("new.bin", true).Should().BeTrue("the name flips on every member holding a copy (FR-RENAME)");
  }

  [Test]
  [Category("EdgeCase")]
  public void Run_GivenRenameIntentButNothingMoved_WhenRecovered_ThenSourceStaysAuthoritative() {
    this._volume1.Seed("old.bin", false, [1]);
    this._journal.LogIntent(JournalOp.Rename, "old.bin", "new.bin");

    this._Recovery().Run();

    this._volume1.FileExists("old.bin", false).Should().BeTrue("an intent that never took effect rolls back cleanly");
    this._volume1.FileExists("new.bin", false).Should().BeFalse();
  }

  [Test]
  [Category("HappyPath")]
  public void Run_GivenOrphanedTempFiles_WhenRecovered_ThenStagingFilesRemoved() {
    this._volume1.Seed("docs/movie.mkv." + DriveBender.DriveBenderConstants.TEMP_EXTENSION, false, [1, 2, 3]);
    this._volume1.Seed("docs/movie.mkv", false, [1]);

    var report = this._Recovery().Run();

    report.TempsRemoved.Should().Be(1);
    this._volume1.FileExists("docs/movie.mkv." + DriveBender.DriveBenderConstants.TEMP_EXTENSION, false).Should().BeFalse();
    this._volume1.GetContent("docs/movie.mkv", false).Should().Equal(new byte[] { 1 }, "the published file is untouched (SAFE-ATOMIC)");
  }

  [Test]
  [Category("HappyPath")]
  public void Run_GivenAnyCrashState_WhenRecoveredTwice_ThenSecondRunIsANoOp() {
    this._volume1.Seed("f.bin", false, [9]);
    this._volume2.Seed("f.bin", true, [1]);
    this._journal.LogIntent(JournalOp.Write, "f.bin", offset: 0, length: 1);

    this._Recovery().Run();
    var second = this._Recovery().Run();

    second.AnythingDone.Should().BeFalse("replay is idempotent (SAFE-IDEMP)");
    this._volume2.GetContent("f.bin", true).Should().Equal(new byte[] { 9 });
  }

  [Test]
  [Category("HappyPath")]
  public void Run_GivenCompletedOperations_WhenRecovered_ThenNothingTouchedAndJournalCompacted() {
    this._volume1.Seed("f.bin", false, [1]);
    var sequence = this._journal.LogIntent(JournalOp.Write, "f.bin");
    this._journal.Complete(sequence, JournalOp.Write);

    var report = this._Recovery().Run();

    report.AnythingDone.Should().BeFalse();
    this._journal.ReadAll().Should().BeEmpty("recovery checkpoints the journal");
  }

  [Test]
  [Category("EdgeCase")]
  public void Run_GivenIncompleteRemoveDir_WhenFolderStillHoldsData_ThenNotRolledForward() {
    this._volume1.Seed("dir/f.txt", false, [1]);
    this._journal.LogIntent(JournalOp.RemoveDir, "dir");

    this._Recovery().Run();

    this._volume1.FileExists("dir/f.txt", false).Should().BeTrue("recovery never destroys data to complete a namespace op");
  }

  /// <summary>End-to-end crash sweep: power loss at every boundary of a duplicated write via the real engine.</summary>
  [Test]
  [Category("HappyPath")]
  public void EndToEnd_GivenPowerLossAfterAckedWrite_WhenRemounted_ThenAckedDataPresentAndConsistent() {
    var cache = new Caching.CacheInstance("r" + Guid.NewGuid().ToString("N"), new() { Size = "65536", BlockSize = "16", MetadataEntries = 100, MetadataTtl = "1m" });
    var config = ConfigResolver.ResolveEffective(null, """{ "duplication": 2 }""");
    var fs = new PoolFileSystem(Guid.NewGuid(), [new(this._volume1), new(this._volume2)], cache, config, this._journal);
    fs.Mount(new(@"X:\"));
    var handle = fs.Create("acked.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [42, 42, 42], 0, WriteMode.Normal); // returns ⇒ acknowledged
    fs.Close(handle);
    fs.Unmount();

    // power loss: everything unflushed dies on both members
    this._volume1.SimulateCrash();
    this._volume2.SimulateCrash();

    // remount runs recovery (FR-RECOVER)
    var cache2 = new Caching.CacheInstance("r2" + Guid.NewGuid().ToString("N"), new() { Size = "65536", BlockSize = "16", MetadataEntries = 100, MetadataTtl = "1m" });
    var remounted = new PoolFileSystem(Guid.NewGuid(), [new(this._volume1), new(this._volume2)], cache2, config);
    remounted.Mount(new(@"X:\"));

    var readHandle = remounted.Open("acked.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[3];
    remounted.Read(readHandle, buffer, 0).Should().Be(3);
    buffer.Should().Equal(new byte[] { 42, 42, 42 }, "no acknowledged write is ever lost after any crash (SAFE-NOLOSS)");
    remounted.Close(readHandle);

    var holders = new[] { this._volume1, this._volume2 };
    holders.Count(v => v.FileExists("acked.bin", false)).Should().Be(1);
    holders.Count(v => v.FileExists("acked.bin", true)).Should().Be(1, "the duplication invariant survives the crash (SAFE-DUP)");
  }

}
