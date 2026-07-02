using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Administrative media operations: scatter-and-remove, replace, restore (whole-file, SAFE-DUP/SAFE-PHYS).</summary>
[TestFixture]
[Category("Unit")]
public class MediaLifecycleTests {

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private FakeVolumeIO _v3 = null!;
  private Journal _journal = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    this._v3 = new(Guid.NewGuid(), "v3", "PHYS-3", capacity: 1L << 20);
    this._journal = new(new MemberJournalStore([this._v1, this._v2, this._v3]));
  }

  private MediaLifecycle _Lifecycle(int duplication, params FakeVolumeIO[] members)
    => new(members, this._journal, duplication);

  private static byte[] _Bytes(params byte[] b) => b;

  [Test]
  [Category("HappyPath")]
  public void ScatterAndRemove_GivenSingleCopyFiles_WhenRemoved_ThenDataMovedToOtherMembers() {
    this._v1.Seed("docs/a.txt", false, _Bytes(1, 2, 3));
    this._v1.Seed("docs/b.txt", false, _Bytes(4, 5));

    var report = this._Lifecycle(1, this._v1, this._v2, this._v3).ScatterAndRemove(this._v1.MemberId);

    report.FilesMoved.Should().Be(2);
    this._v1.FilePaths.Where(p => !PoolPaths.IsHiddenName(p.Split('/')[^1]) && !p.Contains(".drivebenderutility")).Should().BeEmpty("the leaving member holds no pool data");
    var survivors = new[] { this._v2, this._v3 };
    survivors.Sum(v => v.GetContent("docs/a.txt", false) != null ? 1 : 0).Should().Be(1, "a.txt relocated to a survivor");
    survivors.Single(v => v.GetContent("docs/a.txt", false) != null).GetContent("docs/a.txt", false).Should().Equal(new byte[] { 1, 2, 3 });
  }

  [Test]
  [Category("HappyPath")]
  public void ScatterAndRemove_GivenDuplicatedFile_WhenRemoved_ThenSurvivingCopyKeptNoNeedlessMove() {
    this._v1.Seed("dup.bin", false, _Bytes(7, 7));
    this._v2.Seed("dup.bin", true, _Bytes(7, 7)); // already a copy on another domain

    var report = this._Lifecycle(2, this._v1, this._v2, this._v3).ScatterAndRemove(this._v1.MemberId);

    report.FilesMoved.Should().Be(0, "a copy already survives on v2 — no relocation needed");
    this._v1.FileExists("dup.bin", false).Should().BeFalse();
    this._v2.GetContent("dup.bin", true).Should().Equal(new byte[] { 7, 7 });
  }

  [Test]
  [Category("Exception")]
  public void ScatterAndRemove_GivenNoOtherMember_WhenRemoved_ThenRefused() {
    this._v1.Seed("f.bin", false, _Bytes(1));
    this._v2.IsOnline = false;
    this._v3.IsOnline = false;

    var act = () => this._Lifecycle(1, this._v1, this._v2, this._v3).ScatterAndRemove(this._v1.MemberId);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
  }

  [Test]
  [Category("HappyPath")]
  public void Replace_GivenOldMember_WhenReplaced_ThenAllFilesMigratedToNewAndOldCleared() {
    this._v1.Seed("docs/x.txt", false, _Bytes(1, 1));
    this._v1.Seed("docs/y.txt", true, _Bytes(2, 2));
    var replacement = new FakeVolumeIO(Guid.NewGuid(), "new", "PHYS-NEW", capacity: 1L << 20);

    var report = this._Lifecycle(1, this._v1, this._v2).Replace(this._v1.MemberId, replacement);

    report.FilesMoved.Should().Be(2);
    this._v1.FilePaths.Where(p => !p.Contains(".drivebenderutility")).Should().BeEmpty("the old member holds no pool data after migration");
    replacement.GetContent("docs/x.txt", false).Should().Equal(new byte[] { 1, 1 });
    replacement.GetContent("docs/y.txt", true).Should().Equal(new byte[] { 2, 2 }, "shadow role is preserved across the swap");
  }

  [Test]
  [Category("HappyPath")]
  public void RestorePool_GivenMissingShadows_WhenRestored_ThenDuplicationReestablished() {
    this._v1.Seed("f.bin", false, _Bytes(9, 9, 9)); // only a primary, D wants 2

    var report = this._Lifecycle(2, this._v1, this._v2, this._v3).RestorePool();

    report.CopiesCreated.Should().Be(1);
    var shadowHolders = new[] { this._v2, this._v3 }.Count(v => v.GetContent("f.bin", true) != null);
    shadowHolders.Should().Be(1, "a shadow was created on an independent domain (SAFE-DUP/SAFE-PHYS)");
  }

  [Test]
  [Category("HappyPath")]
  public void RestorePool_GivenPrimaryMissingButShadowPresent_WhenRestored_ThenShadowPromoted() {
    this._v2.Seed("orphan.bin", true, _Bytes(5, 5)); // shadow only, no primary anywhere

    var report = this._Lifecycle(1, this._v1, this._v2, this._v3).RestorePool();

    report.CopiesCreated.Should().BeGreaterThan(0);
    this._v2.FileExists("orphan.bin", false).Should().BeTrue("the surviving shadow was promoted to primary (FixMissingPrimaries)");
    this._v2.FileExists("orphan.bin", true).Should().BeFalse();
  }

  [Test]
  [Category("EdgeCase")]
  public void RestorePool_GivenAlreadyHealthy_WhenRestored_ThenNoOp() {
    this._v1.Seed("f.bin", false, _Bytes(1));
    this._v2.Seed("f.bin", true, _Bytes(1));

    var report = this._Lifecycle(2, this._v1, this._v2, this._v3).RestorePool();

    report.AnythingDone.Should().BeFalse("a pool already at its duplication level needs no work");
  }

  [Test]
  [Category("EdgeCase")]
  public void RestorePool_GivenNotEnoughDomains_WhenRestored_ThenBestEffortNoCoLocation() {
    this._v1.Seed("f.bin", false, _Bytes(1));

    // duplication 3 but only v1+v2 online → at most 2 domains; never co-locate (SAFE-PHYS)
    this._v3.IsOnline = false;
    var report = this._Lifecycle(3, this._v1, this._v2, this._v3).RestorePool();

    report.CopiesCreated.Should().Be(1, "one shadow on v2; the third copy is left owed, not co-located");
    var copies = new[] { this._v1, this._v2 }.Count(v => v.GetContent("f.bin", false) != null || v.GetContent("f.bin", true) != null);
    copies.Should().Be(2);
  }

  [Test]
  [Category("HappyPath")]
  public void ScatterAndRemove_GivenInterruptionSafety_WhenJournalInspected_ThenMovesAreJournalled() {
    this._v1.Seed("f.bin", false, _Bytes(1, 2, 3));

    this._Lifecycle(1, this._v1, this._v2).ScatterAndRemove(this._v1.MemberId);

    this._journal.ReadIncomplete().Should().BeEmpty("every relocation intent was completed (resumable, SAFE-WAL)");
  }

}
