using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class JournalTests {

  private FakeVolumeIO _member1 = null!;
  private FakeVolumeIO _member2 = null!;
  private Journal _journal = null!;

  [SetUp]
  public void SetUp() {
    this._member1 = new(Guid.NewGuid(), "m1", "PHYS-1");
    this._member2 = new(Guid.NewGuid(), "m2", "PHYS-2");
    this._journal = new(new MemberJournalStore([this._member1, this._member2]));
  }

  [Test]
  [Category("HappyPath")]
  public void LogIntent_GivenNoCompletion_WhenRead_ThenReportedIncomplete() {
    var sequence = this._journal.LogIntent(JournalOp.Write, "docs/f.txt", offset: 10, length: 4);

    var incomplete = this._journal.ReadIncomplete();

    incomplete.Should().ContainSingle();
    incomplete[0].Sequence.Should().Be(sequence);
    incomplete[0].Op.Should().Be(JournalOp.Write);
    incomplete[0].Path.Should().Be("docs/f.txt");
  }

  [Test]
  [Category("HappyPath")]
  public void Complete_GivenIntent_WhenCompleted_ThenNoLongerIncomplete() {
    var sequence = this._journal.LogIntent(JournalOp.Delete, "f.txt");
    this._journal.Complete(sequence, JournalOp.Delete);

    this._journal.ReadIncomplete().Should().BeEmpty();
  }

  [Test]
  [Category("HappyPath")]
  public void Append_GivenTwoMembers_WhenOneVanishes_ThenJournalStillReadableFromSurvivor() {
    this._journal.LogIntent(JournalOp.Rename, "a.txt", "b.txt");
    this._member1.IsOnline = false;

    var survivorJournal = new Journal(new MemberJournalStore([this._member1, this._member2]));

    survivorJournal.ReadIncomplete().Should().ContainSingle("the journal is mirrored on every member (SAFE-WAL)");
  }

  [Test]
  [Category("Exception")]
  public void LogIntent_GivenNoMemberCanPersist_WhenLogging_ThenMutationRefused() {
    this._member1.IsOnline = false;
    this._member2.IsOnline = false;

    var act = () => this._journal.LogIntent(JournalOp.Write, "f.txt");
    act.Should().Throw<PoolFsException>("an intent that cannot be persisted anywhere must block the mutation (SAFE-ORDER)");
  }

  [Test]
  [Category("EdgeCase")]
  public void ReadAll_GivenTornLastLine_WhenRead_ThenIntactRecordsSurvive() {
    this._journal.LogIntent(JournalOp.Write, "f.txt");
    // simulate a torn append after power loss
    using (var stream = this._member1.OpenWrite(MemberJournalStore.JournalPath, false, false)) {
      stream.Seek(0, SeekOrigin.End);
      var garbage = "{\"seq\": 99, \"op\""u8.ToArray();
      stream.Write(garbage, 0, garbage.Length);
      stream.Flush();
    }

    var records = new Journal(new MemberJournalStore([this._member1])).ReadAll();

    records.Should().ContainSingle("a torn final line is expected after power loss and must not poison replay");
  }

  [Test]
  [Category("HappyPath")]
  public void Checkpoint_GivenCompletedHistory_WhenCompacted_ThenJournalEmpty() {
    var sequence = this._journal.LogIntent(JournalOp.Write, "f.txt");
    this._journal.Complete(sequence, JournalOp.Write);

    this._journal.Checkpoint();

    this._journal.ReadAll().Should().BeEmpty();
    this._journal.ReadIncomplete().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void LogIntent_GivenReloadAfterCrash_WhenLoggingAgain_ThenSequenceNeverReused() {
    var first = this._journal.LogIntent(JournalOp.Write, "f.txt");

    var reloaded = new Journal(new MemberJournalStore([this._member1, this._member2]));
    var second = reloaded.LogIntent(JournalOp.Write, "g.txt");

    second.Should().BeGreaterThan(first, "sequences stay monotonic across restarts (SAFE-IDEMP)");
  }

}
