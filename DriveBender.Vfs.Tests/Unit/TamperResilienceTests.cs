using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// What the pool does when its own bookkeeping is wrong — because it came back from a backup, was
/// edited by hand, or simply cannot be reached any more.
///
/// The end-to-end suite drives these through a real mount; these are the same rules stated where
/// they are cheap to check and impossible to misattribute. Two of them exist because a test found
/// the pool doing the opposite.
/// </summary>
[TestFixture]
[Category("Unit")]
public class TamperResilienceTests {

  private static readonly Guid _pool = Guid.Parse("eeeeeeee-1111-2222-3333-888888888888");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 22);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 22);
  }

  private PoolFileSystem _Mounted(string config = """{ "duplication": 1, "trash": { "enabled": false } }""") {
    var cache = new CacheInstance("tr" + Guid.NewGuid().ToString("N"),
      new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._a), new(this._b)], cache,
      ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static byte[] _ReadThroughTree(PoolFileSystem fs, string path) {
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var buffer = new byte[(int)fs.GetAttributes(path).Length];
      var read = fs.Read(handle, buffer, 0);
      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  #region a journal that describes work already done

  [Test]
  [Category("Exception")]
  public void Recovery_GivenADeleteIntentOlderThanTheFile_ThenTheFileIsNotDestroyed() {
    // The shape a user can actually produce: they restore a member from a backup, or copy an old
    // .drivebenderutility over the current one. The journal now describes a delete that finished
    // long ago, against a path that has since been recreated — and rolling it forward destroys a
    // file nobody asked to delete, with the recovery machinery itself as the cause.
    var logged = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    var journal = new Journal(new MemberJournalStore([this._a, this._b]), () => logged);
    journal.LogIntent(JournalOp.Delete, "recreated.bin");

    // written AFTER the intent, so it cannot be the content the intent was about
    this._a.Seed("recreated.bin", false, [1, 2, 3]);
    this._a.SetTimestamps("recreated.bin", false, null, logged.AddDays(30));

    new PoolRecovery([this._a, this._b], journal).Run();

    this._a.FileExists("recreated.bin", false).Should().BeTrue(
      "an intent cannot be about content that did not exist when it was written, so finishing it "
      + "would destroy a file the pool holds rather than complete an interrupted operation");
    this._a.GetContent("recreated.bin", false).Should().Equal(new byte[] { 1, 2, 3 }, "untouched");
  }

  [Test]
  [Category("HappyPath")]
  public void Recovery_GivenADeleteIntentNewerThanTheFile_ThenItStillRollsForward() {
    // The guard must not cost genuine crash recovery. In a real crash the file was written BEFORE
    // the delete was logged, which is exactly the case this proves still completes.
    var written = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    this._a.Seed("doomed.bin", false, [9]);
    this._b.Seed("doomed.bin", true, [9]);
    this._a.SetTimestamps("doomed.bin", false, null, written);
    this._b.SetTimestamps("doomed.bin", true, null, written);

    var journal = new Journal(new MemberJournalStore([this._a, this._b]), () => written.AddMinutes(1));
    journal.LogIntent(JournalOp.Delete, "doomed.bin");

    new PoolRecovery([this._a, this._b], journal).Run();

    this._a.FileExists("doomed.bin", false).Should().BeFalse("an interrupted delete is finished on every member");
    this._b.FileExists("doomed.bin", true).Should().BeFalse("shadow copies included");
  }

  [Test]
  [Category("Exception")]
  public void Recovery_GivenAnIntentWithNoTimestamp_ThenItIsNotAllowedToDestroyAnything() {
    // Written by an older version, or forged by hand. It proves nothing about when it was made, so
    // it never gets to delete a file: declining costs an operation the caller was never told had
    // succeeded, while proceeding costs the file.
    this._a.Seed("kept.bin", false, [4, 5]);
    var forged = """{"seq":424242,"op":3,"path":"kept.bin"}""";
    new MemberJournalStore([this._a, this._b]).Append(forged);

    new PoolRecovery([this._a, this._b], new Journal(new MemberJournalStore([this._a, this._b]))).Run();

    this._a.FileExists("kept.bin", false).Should().BeTrue(
      "an intent that cannot prove when it was logged must not be allowed to destroy anything");
  }

  #endregion

  #region a snapshot whose stored version is gone

  [Test]
  [Category("Exception")]
  public void Snapshot_GivenItsStoredVersionIsGone_ThenReadingItFailsRatherThanReturningTheLiveFile() {
    // The most dangerous outcome the tamper suite looks for. Reading through to the live file is
    // correct when NOTHING has been preserved for a path — that is what makes a snapshot of an idle
    // pool free. Reached because a preserved version was LOST, the same fallback hands back today's
    // content under the snapshot's name: no error, nothing downstream can tell, and a restore driven
    // off it overwrites the good copy with the newer one.
    var fs = this._Mounted();
    _Write(fs, "ledger.db", [1, 1, 1, 1]);
    fs.TakeSnapshot("monday");
    _Write(fs, "ledger.db", [9, 9]);

    // whatever the store set aside, taken off the disks behind the pool's back
    foreach (var member in new[] { this._a, this._b })
      foreach (var path in member.FilePaths.Where(p => p.Contains("snapshots", StringComparison.OrdinalIgnoreCase)).ToList())
        member.Delete(path, false);

    var read = () => _ReadThroughTree(fs, ".snapshots/monday/ledger.db");
    read.Should().Throw<PoolFsException>(
      "a snapshot whose stored version is gone must say so — serving the live file in its place is "
      + "worse than failing, because failing is detectable");

    _ReadThroughTree(fs, "ledger.db").Should().Equal(new byte[] { 9, 9 },
      "while the live file, which was never in question, is untouched");
  }

  [Test]
  [Category("HappyPath")]
  public void Snapshot_GivenNothingHasTouchedTheFile_ThenTheViewStillReadsThroughToIt() {
    // The other side of the same rule, and the reason it cannot simply be "always demand a version":
    // most of what a snapshot holds was never copied anywhere, because nothing destroyed it.
    var fs = this._Mounted();
    _Write(fs, "stable.bin", [7, 7, 7]);
    fs.TakeSnapshot("nightly");

    _ReadThroughTree(fs, ".snapshots/nightly/stable.bin").Should().Equal(new byte[] { 7, 7, 7 },
      "an untouched file is still part of the snapshot, and reading it must not demand a copy that "
      + "was never needed");
  }

  #endregion

  #region a member that refuses to take a copy

  [Test]
  [Category("Exception")]
  public void Duplication_GivenAMemberRefusesTheSecondCopy_ThenTheFileIsStillCreatedAndTheCopyIsOwed() {
    // A member with no ROOM for the second copy already defers it and lets the create through. A
    // member whose filesystem remounted itself read-only under us — the commonest real disk failure
    // there is — is the same situation reached by a different road, and it used to throw straight
    // out of the duplication path and take the create with it.
    //
    // What the pool does about ACKNOWLEDGING a write it cannot make redundant is a separate and
    // deliberate policy (minCopiesBeforeAck, exercised end to end); this is only about the file
    // being placed at all rather than an exception escaping.
    var fs = this._Mounted(
      """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "trash": { "enabled": false } }""");

    this._b.AlwaysFail(VolumeOp.OpenWrite);

    var create = () => fs.Close(fs.Create("important.bin", NodeKind.File, CreateFlags.Truncate));
    create.Should().NotThrow(
      "one member can hold the file, so a second that refuses its copy costs redundancy — which the "
      + "healer restores — and must not cost the create");

    this._a.FileExists("important.bin", false).Should().BeTrue("and the healthy member is holding it");
  }

  [Test]
  [Category("EdgeCase")]
  public void Duplication_GivenAMemberRefusesEveryWrite_ThenPlacementStopsChoosingIt() {
    // The other half. A member that has just refused has to drop out of the running, or a two-member
    // pool picks the frozen one on every pass and the create loop retries into it until it gives up
    // — with a healthy disk sitting beside it the whole time.
    var fs = this._Mounted();
    this._b.AlwaysFail(VolumeOp.OpenWrite);

    var first = () => _Write(fs, "first.bin", [1]);
    first.Should().NotThrow("the healthy member takes the file");

    var second = () => _Write(fs, "second.bin", [2]);
    second.Should().NotThrow("and the next one goes there too, without rediscovering the failure");

    this._b.ClearFaults();
    _ReadThroughTree(fs, "second.bin").Should().Equal(new byte[] { 2 });
    this._a.FileExists("second.bin", false).Should().BeTrue("both files are on the member that works");
  }

  #endregion

}
