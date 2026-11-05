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
  [Category("EdgeCase")]
  public void RestorePool_GivenSameDiskMembersAndOptIn_WhenRestored_ThenCopyCoLocatedForBitRotProtection() {
    var sameDisk = new FakeVolumeIO(Guid.NewGuid(), "same-disk", "PHYS-1", capacity: 1L << 20);
    this._v1.Seed("only.bin", false, _Bytes(3, 1, 4));

    // strict SAFE-PHYS: nothing to place on — the file stays single-copy
    this._Lifecycle(2, this._v1, sameDisk).RestorePool().CopiesCreated.Should().Be(0);
    this._Lifecycle(2, this._v1, sameDisk).CountUnderDuplicated().Should().Be(1);

    // opted in: the second copy lands on the same disk's other member (bit-rot protection)
    var allowing = new MediaLifecycle([this._v1, sameDisk], this._journal, 2, allowSamePhysical: true);
    allowing.RestorePool().CopiesCreated.Should().Be(1);
    sameDisk.GetContent("only.bin", true).Should().Equal(new byte[] { 3, 1, 4 });
    allowing.CountUnderDuplicated().Should().Be(0, "with same-disk copies allowed, two members satisfy duplication 2");
  }

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
  public void ScatterAndRemove_GivenDuplicatedFileAndADiskToSpare_WhenRemoved_ThenSurvivorKeptAndDuplicationRestored() {
    // This used to assert that NOTHING was copied: one copy survived on v2, so the leaving copy was
    // simply dropped — and the file sat at one copy with v3 free, until some later heal. Retiring a
    // disk now keeps the configured level on the disks that stay: the survivor is left where it is,
    // and one new copy goes to v3.
    this._v1.Seed("dup.bin", false, _Bytes(7, 7));
    this._v2.Seed("dup.bin", true, _Bytes(7, 7)); // already a copy on another domain

    var report = this._Lifecycle(2, this._v1, this._v2, this._v3).ScatterAndRemove(this._v1.MemberId);

    report.FilesMoved.Should().Be(1, "one copy is made on v3 to keep duplication 2 — the survivor itself is not moved");
    this._v1.FileExists("dup.bin", false).Should().BeFalse();
    this._v2.GetContent("dup.bin", true).Should().Equal(new byte[] { 7, 7 }, "the surviving copy stays where it is");
    (this._v3.GetContent("dup.bin", false) ?? this._v3.GetContent("dup.bin", true)).Should().Equal(new byte[] { 7, 7 });
  }

  [Test]
  [Category("EdgeCase")]
  public void ScatterAndRemove_GivenDuplicatedFileAndNoDiskToSpare_WhenRemoved_ThenSurvivorKeptAndNothingFails() {
    // the other side of the boundary: with only two members there is nowhere for a second copy, so
    // the survivor is kept, nothing is refused, and the healer is left to restore it later
    this._v1.Seed("dup.bin", false, _Bytes(7, 7));
    this._v2.Seed("dup.bin", true, _Bytes(7, 7));

    var report = this._Lifecycle(2, this._v1, this._v2).ScatterAndRemove(this._v1.MemberId);

    report.FilesMoved.Should().Be(0, "there is no third disk to copy to");
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
  [Category("Exception")]
  public void Replace_GivenOfflineOldMember_WhenReplaced_ThenRefusedSoDataIsNotAbandoned() {
    this._v1.Seed("docs/x.txt", false, _Bytes(1, 1));
    var replacement = new FakeVolumeIO(Guid.NewGuid(), "new", "PHYS-NEW", capacity: 1L << 20);
    this._v1.IsOnline = false; // the disk we are replacing died

    var act = () => this._Lifecycle(1, this._v1, this._v2).Replace(this._v1.MemberId, replacement);

    act.Should().Throw<PoolFsException>("replacing from an unreadable member would migrate 0 files and silently abandon its data")
      .Which.Error.Should().Be(PoolFsError.Offline);
    replacement.FilePaths.Should().NotContain(p => p == "docs/x.txt", "nothing was migrated from the dead member");
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

  [Test]
  [Category("Exception")]
  public void ScatterAndRemove_GivenTheLeavingCopyIsNewerThanTheSurvivor_ThenTheNewerContentIsKept() {
    // B was away while the file was edited on A. B came back, and A is retired before the pool was
    // ever mounted to bring B up to date. The scatter used to see "a copy survives on B" and delete
    // A's — the only copy of the edit — leaving B's stale one as the file.
    this._v1.Seed("f.bin", false, _Bytes(2, 2, 2));
    this._v1.SetTimestamps("f.bin", false, null, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
    this._v2.Seed("f.bin", true, _Bytes(1, 1, 1));
    this._v2.SetTimestamps("f.bin", true, null, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

    this._Lifecycle(2, this._v1, this._v2).ScatterAndRemove(this._v1.MemberId);

    this._v1.FileExists("f.bin", false).Should().BeFalse("the member is retired");
    this._v2.GetContent("f.bin", true).Should().Equal(new byte[] { 2, 2, 2 },
      "the edit made while B was away is the file; retiring A must not leave B's older copy behind as it");
  }

  [Test]
  [Category("EdgeCase")]
  public void ScatterAndRemove_GivenCopiesThatDisagree_ThenTheOverwrittenVersionIsKeptAsAConflict() {
    // Two copies edited outside the pool to different content: nothing proves the newer one is the
    // one that matters. Converging them picks the newer, as recovery does, but the older version must
    // survive somewhere a person can find it, exactly as a scrub keeps it.
    this._v1.Seed("f.bin", false, _Bytes(2, 2, 2));
    this._v1.SetTimestamps("f.bin", false, null, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
    this._v2.Seed("f.bin", true, _Bytes(7, 7, 7, 7));
    this._v2.SetTimestamps("f.bin", true, null, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

    this._Lifecycle(2, this._v1, this._v2).ScatterAndRemove(this._v1.MemberId);

    this._v2.GetContent("f.bin", true).Should().Equal(new byte[] { 2, 2, 2 });
    var kept = this._v2.FilePaths.Where(p => p.Contains("conflicts/", StringComparison.OrdinalIgnoreCase) && p.Contains("f.bin")).ToArray();
    kept.Should().ContainSingle("the version that was overwritten is preserved");
    this._v2.GetContent(kept[0], false).Should().Equal(new byte[] { 7, 7, 7, 7 });
  }

  [Test]
  [Category("Exception")]
  public void ScatterAndRemove_GivenAnInterruptedWriteAcknowledgedOnTheLeavingMember_ThenNoCopyLosesIt() {
    // Two writes a power cut left open, each acknowledged on one copy only: block 0 on A's primary,
    // block 1 on B's shadow. Recovery replays them by range from the copy each names — but once A is
    // gone, the intent naming it can only fall back to "newest copy wins", and one of the two
    // acknowledged blocks is lost. Retiring A must reconcile while A can still answer.
    var original = new byte[8192];
    var onA = (byte[])original.Clone();
    Array.Fill(onA, (byte)0xAA, 0, 4096);
    var onB = (byte[])original.Clone();
    Array.Fill(onB, (byte)0xBB, 4096, 4096);
    this._v1.Seed("F", false, onA);
    this._v2.Seed("F", true, onB);
    this._journal.LogIntent(JournalOp.Write, "F", offset: 0, length: 4096, memberId: this._v1.MemberId, shadow: false);
    this._journal.LogIntent(JournalOp.Write, "F", offset: 4096, length: 4096, memberId: this._v2.MemberId, shadow: true);

    this._Lifecycle(2, this._v1, this._v2, this._v3).ScatterAndRemove(this._v1.MemberId);
    new PoolRecovery([this._v2, this._v3], new Journal(new MemberJournalStore([this._v2, this._v3]))).Run();

    var expected = new byte[8192];
    Array.Fill(expected, (byte)0xAA, 0, 4096);
    Array.Fill(expected, (byte)0xBB, 4096, 4096);
    var copies = 0;
    foreach (var member in new[] { this._v2, this._v3 })
      foreach (var shadow in new[] { false, true })
        if (member.GetContent("F", shadow) is { } copy) {
          ++copies;
          copy.Should().Equal(expected, $"the copy on '{member.DisplayName}' (shadow: {shadow}) holds both acknowledged blocks");
        }

    copies.Should().Be(2, "the file keeps its duplication level on the members that stay");
  }

}
