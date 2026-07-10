using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// SAFE-OFFLINE / FR-HEAL: a member dropping out mid-operation loses no data and throws no
/// errors while at least one copy survives; when it returns, the pool restores itself to
/// full health — missed deletes/renames replay (no ghost resurrection), stale content
/// re-syncs to the newest write, and owed duplication heals in the background.
/// </summary>
[TestFixture]
[Category("Unit")]
public class MemberReturnHealTests {

  private static readonly Guid _pool = Guid.Parse("cccccccc-5555-6666-7777-888888888888");

  private FakeVolumeIO _volume1 = null!;
  private FakeVolumeIO _volume2 = null!;

  [SetUp]
  public void SetUp() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
  }

  private PoolFileSystem _CreateFs(string configJson = """{ "duplication": 2, "trash": { "enabled": false } }""", bool mount = true) {
    var cache = new CacheInstance("hr" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._volume1), new(this._volume2)], cache, ConfigResolver.ResolveEffective(null, configJson));
    if (mount)
      fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _CreateWithContent(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private byte[]? _AnyCopy(FakeVolumeIO volume, string path)
    => volume.GetContent(path, false) ?? volume.GetContent(path, true);

  [Test]
  [Category("HappyPath")]
  public void Write_GivenMemberOfflineDuringWrite_WhenItReturns_ThenDuplicationHealsWithoutAnyExplicitOp() {
    var fs = this._CreateFs();
    var scheduler = fs.CreateScheduler();
    fs.PollMembers(); // baseline the watcher

    this._volume2.IsOnline = false;
    fs.PollMembers();

    // the write proceeds degraded — no error while one copy is reachable (§10 SAFE-DEGRADE)
    _CreateWithContent(fs, "vacation.jpg", [1, 2, 3, 4, 5]);
    this._volume1.GetContent("vacation.jpg", false).Should().Equal(new byte[] { 1, 2, 3, 4, 5 }, "the surviving member carries every acknowledged byte");
    this._AnyCopy(this._volume2, "vacation.jpg").Should().BeNull("the offline member took nothing");

    this._volume2.IsOnline = true;
    scheduler.Quiesce(); // member-watch sees the return, heal re-establishes duplication

    this._AnyCopy(this._volume2, "vacation.jpg").Should().Equal(new byte[] { 1, 2, 3, 4, 5 },
      "the pool heals back to duplication level 2 on its own when the member returns (FR-HEAL)");
    fs.HealPending.Should().BeFalse("the heal converged completely");
  }

  [Test]
  [Category("HappyPath")]
  public void Unlink_GivenMemberOfflineDuringDelete_WhenItReturns_ThenNoGhostResurrects() {
    var fs = this._CreateFs();
    _CreateWithContent(fs, "old.log", [9, 9]);
    fs.PollMembers();

    this._volume2.IsOnline = false;
    fs.PollMembers();
    fs.Unlink("old.log");

    this._volume2.IsOnline = true;
    fs.PollMembers(); // return → the missed delete replays from the tombstone log (SAFE-OFFLINE)

    this._AnyCopy(this._volume2, "old.log").Should().BeNull("the returned member's stale copies were removed");
    fs.ReadDirectory("").Should().NotContain(e => e.Name == "old.log", "a deleted file never resurrects into the pool");
  }

  [Test]
  [Category("HappyPath")]
  public void Rename_GivenMemberOfflineDuringRename_WhenItReturns_ThenRenameReplays() {
    var fs = this._CreateFs();
    _CreateWithContent(fs, "draft.txt", [1, 2]);
    fs.PollMembers();

    this._volume2.IsOnline = false;
    fs.PollMembers();
    fs.Rename("draft.txt", "final.txt", RenameFlags.None);

    this._volume2.IsOnline = true;
    fs.PollMembers();

    this._AnyCopy(this._volume2, "draft.txt").Should().BeNull("the old name is gone on the returned member too");
    this._AnyCopy(this._volume2, "final.txt").Should().Equal(new byte[] { 1, 2 }, "the returned member followed the rename it missed");
    var names = fs.ReadDirectory("").Select(e => e.Name).ToArray();
    names.Should().Contain("final.txt");
    names.Should().NotContain("draft.txt", "the union namespace shows exactly one name after the replay");
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenStaleCopyOnReturnedMember_WhenItReturns_ThenNewestContentWinsEverywhere() {
    var fs = this._CreateFs();
    _CreateWithContent(fs, "report.doc", [1, 1, 1]);
    fs.PollMembers();

    this._volume2.IsOnline = false;
    fs.PollMembers();

    Thread.Sleep(30); // the overwrite must carry a measurably newer timestamp
    var handle = fs.Open("report.doc", AccessMode.ReadWrite, ShareMode.None);
    fs.Write(handle, [7, 7, 7], 0, WriteMode.Normal);
    fs.Close(handle);

    this._volume2.IsOnline = true;
    fs.PollMembers(); // return → quick scan re-syncs the stale copy from the newest write

    this._AnyCopy(this._volume2, "report.doc").Should().Equal(new byte[] { 7, 7, 7 },
      "the copy that missed writes while offline re-synchronizes to the newest content (SAFE-OFFLINE)");

    var buffer = new byte[3];
    var readHandle = fs.Open("report.doc", AccessMode.Read, ShareMode.Read);
    fs.Read(readHandle, buffer, 0);
    fs.Close(readHandle);
    buffer.Should().Equal(new byte[] { 7, 7, 7 }, "reads only ever see the acknowledged newest bytes");
  }

  [Test]
  [Category("EdgeCase")]
  public void Heal_GivenShadowOnlySurvivor_WhenMounted_ThenPrimaryPromotedAndDuplicationRestored() {
    // only a shadow copy survives (the primary's member died and was replaced empty)
    this._volume1.Seed("survivor.dat", true, [7, 7]);
    var fs = this._CreateFs();
    var scheduler = fs.CreateScheduler();

    scheduler.Quiesce(); // the mount-time heal request drains

    this._volume1.GetContent("survivor.dat", false).Should().Equal(new byte[] { 7, 7 }, "the surviving shadow was promoted to a primary");
    this._volume1.GetContent("survivor.dat", true).Should().BeNull("the promoted shadow does not linger as a duplicate on the same member");
    this._volume2.GetContent("survivor.dat", true).Should().Equal(new byte[] { 7, 7 }, "duplication level 2 was re-established on the other member");
  }

  [Test]
  [Category("EdgeCase")]
  public void Churn_GivenFlappingMemberUnderLoad_WhenSettled_ThenNoLossNoErrorsNoGhostsAndFullHealth() {
    var volume3 = new FakeVolumeIO(Guid.NewGuid(), "v3", "PHYS-3", capacity: 1L << 20);
    var cache = new CacheInstance("ch" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._volume1), new(this._volume2), new(volume3)], cache,
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "trash": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));
    var scheduler = fs.CreateScheduler();
    var volumes = new[] { this._volume1, this._volume2, volume3 };

    var random = new Random(20260710); // seeded — reproducible
    var oracle = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    FakeVolumeIO? offline = null;
    var fileCounter = 0;

    // overwriting a file whose copy sits on the offline member would legitimately create a
    // stale copy (covered by its own test); the churn keeps every op loss-free by contract
    bool FullyReachable(string path) => offline == null || this._AnyCopy(offline, path) == null;

    for (var op = 0; op < 200; ++op)
      switch (random.Next(10)) {
        case 0: // flap: exactly one member is ever away, like a real cable/USB dropout
          if (offline == null) {
            offline = volumes[random.Next(volumes.Length)];
            offline.IsOnline = false;
          } else {
            offline.IsOnline = true;
            offline = null;
          }

          fs.PollMembers();
          break;

        case 1 or 2: {
          var name = $"f{fileCounter++}.bin";
          var content = new byte[random.Next(1, 64)];
          random.NextBytes(content);
          _CreateWithContent(fs, name, content);
          oracle[name] = content;
          break;
        }

        case 3 or 4: {
          var candidates = oracle.Keys.Where(FullyReachable).ToArray();
          if (candidates.Length == 0)
            break;

          var name = candidates[random.Next(candidates.Length)];
          var content = new byte[random.Next(1, 64)];
          random.NextBytes(content);
          var handle = fs.Open(name, AccessMode.ReadWrite, ShareMode.None);
          fs.Write(handle, content, 0, WriteMode.Normal);
          fs.SetLength(handle, content.Length);
          fs.Close(handle);
          oracle[name] = content;
          break;
        }

        case 5: {
          if (oracle.Count == 0)
            break;

          var name = oracle.Keys.ElementAt(random.Next(oracle.Count));
          fs.Unlink(name);
          oracle.Remove(name);
          deleted.Add(name);
          break;
        }

        default:
          scheduler.Pump();
          break;
      }

    if (offline != null)
      offline.IsOnline = true;
    fs.PollMembers();
    scheduler.Quiesce(); // heal + owed sync settle completely

    foreach (var (name, expected) in oracle) {
      var buffer = new byte[expected.Length];
      var handle = fs.Open(name, AccessMode.Read, ShareMode.Read);
      var total = 0;
      while (total < expected.Length) {
        var got = fs.Read(handle, buffer.AsSpan(total), total);
        if (got == 0)
          break;

        total += got;
      }

      fs.Close(handle);
      total.Should().Be(expected.Length, $"file '{name}' must be fully readable after the churn");
      buffer.Should().Equal(expected, $"file '{name}' carries exactly the acknowledged bytes (SAFE-NOLOSS)");

      var holders = volumes.Where(v => this._AnyCopy(v, name) != null).ToArray();
      holders.Length.Should().BeGreaterThanOrEqualTo(2, $"'{name}' is back at duplication level 2 after settling (FR-HEAL)");
      foreach (var holder in holders)
        this._AnyCopy(holder, name).Should().Equal(expected, $"every copy of '{name}' is identical");
    }

    foreach (var name in deleted)
    foreach (var volume in volumes)
      this._AnyCopy(volume, name).Should().BeNull($"deleted '{name}' never resurrects (SAFE-OFFLINE)");

    fs.HealPending.Should().BeFalse("nothing is left to heal once the pool settled");
  }

  [Test]
  [Category("Exception")]
  public void Read_GivenAllCopiesUnreachable_WhenRead_ThenFailsCleanlyWithoutCorruption() {
    var fs = this._CreateFs("""{ "duplication": 1, "trash": { "enabled": false } }""");
    _CreateWithContent(fs, "single.bin", [5]);
    fs.PollMembers();

    var holder = new[] { this._volume1, this._volume2 }.Single(v => this._AnyCopy(v, "single.bin") != null);
    var handle = fs.Open("single.bin", AccessMode.Read, ShareMode.Read);
    holder.IsOnline = false;
    fs.PollMembers();

    var act = () => fs.Read(handle, new byte[1], 0);
    act.Should().Throw<PoolFsException>("with NO copy left the read must fail — but only then");
    fs.Close(handle);

    holder.IsOnline = true;
    fs.PollMembers();
    var again = fs.Open("single.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[1];
    fs.Read(again, buffer, 0).Should().Be(1);
    buffer.Should().Equal(new byte[] { 5 }, "the data is intact once its member is back");
    fs.Close(again);
  }

}

/// <summary>
/// Detection vs. correction on the integrity layer: health checks must never mutate the
/// pool, and copies that silently diverged (each matching its own checksum DB) are found
/// and re-synchronized from the newest write.
/// </summary>
[TestFixture]
[Category("Unit")]
public class StaleCopyIntegrityTests {

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
  }

  private IntegrityService _SeedDivergentButDbMatchingCopies() {
    // both copies match their OWN checksum DB — but not each other (a member missed writes)
    this._v1.Seed("f.bin", false, [9, 9]);
    this._v2.Seed("f.bin", true, [1, 1]);
    this._v1.SetTimestamps("f.bin", false, null, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)); // newer
    this._v2.SetTimestamps("f.bin", true, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    var integrity = new IntegrityService([this._v1, this._v2]);
    integrity.RecordWholeFile(this._v1, "f.bin", false, [9, 9]);
    integrity.RecordWholeFile(this._v2, "f.bin", true, [1, 1]);
    integrity.SaveAll();
    return integrity;
  }

  [Test]
  [Category("HappyPath")]
  public void DetectAll_GivenStaleDivergentCopies_WhenDetected_ThenReportedButNothingMutated() {
    var integrity = this._SeedDivergentButDbMatchingCopies();

    var issues = integrity.DetectAll();

    issues.Should().ContainSingle(i => i.Kind == IntegrityIssueKind.StaleCopyDetected && i.Path == "f.bin");
    this._v2.GetContent("f.bin", true).Should().Equal(new byte[] { 1, 1 }, "detection NEVER mutates the pool");
    this._v2.FilePaths.Should().NotContain(p => p.Contains("conflicts"), "nothing was quarantined by a detect-only pass");
  }

  [Test]
  [Category("HappyPath")]
  public void ScrubAll_GivenStaleDivergentCopies_WhenScrubbed_ThenNewestWinsEverywhere() {
    var integrity = this._SeedDivergentButDbMatchingCopies();

    var issues = integrity.ScrubAll();

    issues.Should().ContainSingle(i => i.Kind == IntegrityIssueKind.StaleCopyRepaired && i.Path == "f.bin");
    this._v2.GetContent("f.bin", true).Should().Equal(new byte[] { 9, 9 }, "the stale copy re-synchronized from the newest write");
  }

  [Test]
  [Category("EdgeCase")]
  public void ScrubAll_GivenDivergenceWithIdenticalTimestamps_WhenScrubbed_ThenConflictPreservedNeverGuessed() {
    var stamp = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
    this._v1.Seed("f.bin", false, [9, 9]);
    this._v2.Seed("f.bin", true, [1, 1]);
    this._v1.SetTimestamps("f.bin", false, null, stamp);
    this._v2.SetTimestamps("f.bin", true, null, stamp);

    var integrity = new IntegrityService([this._v1, this._v2]);
    integrity.RecordWholeFile(this._v1, "f.bin", false, [9, 9]);
    integrity.RecordWholeFile(this._v2, "f.bin", true, [1, 1]);

    var issues = integrity.ScrubAll();

    issues.Should().ContainSingle(i => i.Kind == IntegrityIssueKind.Conflict && i.Path == "f.bin");
    this._v1.GetContent("f.bin", false).Should().Equal(new byte[] { 9, 9 }, "no copy is overwritten when the winner is ambiguous");
    this._v2.GetContent("f.bin", true).Should().Equal(new byte[] { 1, 1 });
  }

}
