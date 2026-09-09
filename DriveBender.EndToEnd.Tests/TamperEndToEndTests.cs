using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Somebody messes with the pool's own bookkeeping, behind its back.
///
/// Every other suite assumes the members hold what the pool put there. This one assumes the
/// opposite, because that is what actually happens: a user browses the member disks and "cleans
/// up", a sync tool touches a hidden folder, a restored backup puts back an old journal, an editor
/// rewrites a sidecar, half a disk comes back from a filesystem check with holes in it. None of
/// those are crashes and none of them are bit-rot — they are edits, by something that had every
/// right to write to that disk and no idea what the files meant.
///
/// The bar is the same for all of them, and it is deliberately not "the feature keeps working":
///
///  1. **Never lose acknowledged data.** A file the pool accepted is readable afterwards. This is
///     absolute; everything else can degrade.
///  2. **Never crash the mount.** A damaged sidecar takes out that sidecar, not the filesystem.
///  3. **Degrade legibly.** Where a promise cannot be kept, the pool must say so — an error the
///     caller can see. Silently substituting different content for what was asked for is the one
///     outcome worse than failing, because nothing downstream can detect it.
///
/// Rule 3 is why several of these tests assert on an *error*. A snapshot that has lost its stored
/// version and answers with the live file instead has not degraded; it has lied.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("EdgeCase")]
[NonParallelizable]
public class TamperEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);

  private const string _UTILITY = ".drivebenderutility";

  /// <summary>A pool whose deletes go to the recycle bin, so there are sidecars to damage.</summary>
  private const string _TRASH_ON =
    """{ "duplication": 1, "trash": { "enabled": true, "retention": "7d", "maxSize": "50%" } }""";

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  #region reaching into the members

  /// <summary>Every copy of one of the pool's own files, across every member.</summary>
  private static IReadOnlyList<string> _MetadataFiles(MountedPool pool, string relative)
    => [.. pool.MemberPaths.Select(m => Path.Combine(m, relative.Replace('/', Path.DirectorySeparatorChar)))
        .Where(File.Exists)];

  /// <summary>Every file under a member's hidden tree whose name ends the given way.</summary>
  private static IReadOnlyList<string> _HiddenFiles(MountedPool pool, string suffix)
    => [.. pool.MemberPaths
        .Select(m => Path.Combine(m, _UTILITY))
        .Where(Directory.Exists)
        .SelectMany(u => Directory.EnumerateFiles(u, "*", SearchOption.AllDirectories))
        .Where(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))];

  private static string _TakeSnapshot(MountedPool pool, string name) {
    var taken = DbMount.RunExpectingSuccess(_CLI, "pool-snapshot-take", pool.PoolName, name);
    return taken.StandardOutput.Split(':').Last().Trim().Split('\n')[0].Trim();
  }

  /// <summary>Reads a path through the mount, or says why it could not.</summary>
  private static (bool ok, byte[] content, string error) _TryRead(string path) {
    try {
      return (true, File.ReadAllBytes(path), "");
    } catch (Exception e) {
      return (false, [], $"{e.GetType().Name}: {e.Message}");
    }
  }

  #endregion

  #region the journal

  [Test]
  [Description("The journal is truncated mid-line on every member: the pool mounts and every file is intact.")]
  public void Journal_GivenEveryMirrorIsTruncatedMidRecord_ThenThePoolStillMountsAndKeepsItsFiles() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(64 * 1024, 201);
    File.WriteAllBytes(pool.PathTo("kept.bin"), content);

    var journals = _MetadataFiles(pool, $"{_UTILITY}/journal.jsonl");
    journals.Should().NotBeEmpty("the journal has to exist before damaging it means anything");

    pool.WhileUnmounted(() => {
      foreach (var journal in journals) {
        var text = File.ReadAllText(journal);
        File.WriteAllText(journal, text[..(text.Length * 2 / 3)]); // stops mid-record, as a power cut does
      }
    });

    File.ReadAllBytes(pool.PathTo("kept.bin")).Should().Equal(content,
      $"a damaged journal describes what the pool was DOING, never what it holds — the files are on "
      + $"the disks either way.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Description("The journal is replaced with text that is not JSON at all: the pool mounts and keeps its files.")]
  public void Journal_GivenEveryMirrorIsGarbage_ThenThePoolStillMountsAndKeepsItsFiles() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(32 * 1024, 202);
    File.WriteAllBytes(pool.PathTo("kept.bin"), content);

    pool.WhileUnmounted(() => {
      foreach (var journal in _MetadataFiles(pool, $"{_UTILITY}/journal.jsonl"))
        File.WriteAllText(journal, "this is not json\nneither is this\n\0\0\0binary rubbish\n");
    });

    File.ReadAllBytes(pool.PathTo("kept.bin")).Should().Equal(content,
      $"a line the pool cannot parse is skipped, not fatal."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    // and the pool is still WRITABLE afterwards — a mount that comes up read-only or wedged has
    // failed just as completely as one that will not come up
    File.WriteAllBytes(pool.PathTo("after.bin"), [7, 7, 7]);
    File.ReadAllBytes(pool.PathTo("after.bin")).Should().Equal(new byte[] { 7, 7, 7 });
  }

  [Test]
  [Description("Somebody deletes the journal off every member: the pool mounts and keeps its files.")]
  public void Journal_GivenItIsDeletedEverywhere_ThenThePoolStillMountsAndKeepsItsFiles() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(48 * 1024, 203);
    File.WriteAllBytes(pool.PathTo("kept.bin"), content);

    pool.WhileUnmounted(() => {
      foreach (var journal in _MetadataFiles(pool, $"{_UTILITY}/journal.jsonl"))
        File.Delete(journal);
    });

    File.ReadAllBytes(pool.PathTo("kept.bin")).Should().Equal(content,
      $"no journal means no interrupted work to replay — which is a pool that is simply idle, not "
      + $"a pool that is broken.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.WriteAllBytes(pool.PathTo("after.bin"), [8]);
    File.ReadAllBytes(pool.PathTo("after.bin")).Should().Equal(new byte[] { 8 },
      "and the pool writes a fresh journal rather than refusing to work without one");
  }

  [Test]
  [Description("A restored backup puts an OLD journal back, holding a completed delete: recovery must not replay it against the file that exists now.")]
  public void Journal_GivenAStaleIntentNamesALiveFile_ThenRecoveryDoesNotDestroyIt() {
    // The nastiest journal case, and the one a user can actually cause: they restore the member
    // from a backup, or copy an old .drivebenderutility over the current one. The journal now
    // describes work that finished long ago, against a path that has since been recreated. Replaying
    // it deletes a file nobody asked to delete — data loss caused entirely by the recovery machinery.
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(16 * 1024, 204);
    File.WriteAllBytes(pool.PathTo("recreated.bin"), content);

    pool.WhileUnmounted(() => {
      foreach (var journal in _MetadataFiles(pool, $"{_UTILITY}/journal.jsonl"))
        // op 3 is Delete, and the sequence is deliberately far ABOVE anything the pool has used:
        // a low one might collide with a record that already carries a completion, in which case the
        // intent reads as finished and recovery correctly ignores it — proving nothing.
        File.AppendAllText(journal,
          """{"seq":999999,"op":3,"path":"recreated.bin"}""" + "\n");
    });

    File.Exists(pool.PathTo("recreated.bin")).Should().BeTrue(
      $"an intent from a restored-over journal must never delete a file that is here now."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
    File.ReadAllBytes(pool.PathTo("recreated.bin")).Should().Equal(content, "with its content untouched");
  }

  #endregion

  #region the tombstone log

  [Test]
  [Description("The tombstone log is corrupted while a member is away: the pool mounts, and the member's return does not resurrect anything readable as live.")]
  public void Tombstones_GivenTheLogIsCorrupted_ThenThePoolStillMountsAndStaysConsistent() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    File.WriteAllBytes(pool.PathTo("doomed.bin"), _Payload(8 * 1024, 205));
    File.WriteAllBytes(pool.PathTo("kept.bin"), _Payload(8 * 1024, 206));

    pool.Eject(1);
    File.Delete(pool.PathTo("doomed.bin")); // member 1 misses this, so a tombstone is written for it

    pool.WhileUnmounted(() => {
      foreach (var log in _MetadataFiles(pool, $"{_UTILITY}/tombstones.jsonl"))
        File.WriteAllText(log, "{\"broken\": ");
    });

    pool.Restore(1);
    // the returning member is noticed on the online probe's cycle, and replay runs after that; the
    // assertion is about the state once the pool has SEEN it come back
    MountedPool.WaitUntil(() => File.Exists(pool.PathTo("kept.bin")), TimeSpan.FromMinutes(1));

    File.Exists(pool.PathTo("kept.bin")).Should().BeTrue(
      $"a corrupted tombstone log must not cost the pool a file that was never deleted."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
    _TryRead(pool.PathTo("kept.bin")).ok.Should().BeTrue("and it must still be readable");
  }

  [Test]
  [Description("Somebody deletes the tombstone log: the pool mounts, works, and never loses a file that was not deleted.")]
  public void Tombstones_GivenTheLogIsDeleted_ThenThePoolStillMountsAndKeepsWhatItHas() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(24 * 1024, 207);
    File.WriteAllBytes(pool.PathTo("kept.bin"), content);

    pool.WhileUnmounted(() => {
      foreach (var log in _MetadataFiles(pool, $"{_UTILITY}/tombstones.jsonl"))
        File.Delete(log);
    });

    File.ReadAllBytes(pool.PathTo("kept.bin")).Should().Equal(content,
      $"the log records what an ABSENT member owes; losing it can only cost catch-up work, never a "
      + $"file.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.WriteAllBytes(pool.PathTo("after.bin"), [9]);
    File.ReadAllBytes(pool.PathTo("after.bin")).Should().Equal(new byte[] { 9 });
  }

  #endregion

  #region the recycle bin's sidecars

  [Test]
  [Description("A .trashinfo sidecar is deleted: listing the bin still works and the remaining entries are still restorable.")]
  public void Trash_GivenASidecarIsDeleted_ThenTheBinStillListsAndRestoresWhatItCan() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);
    File.WriteAllBytes(pool.PathTo("orphaned.bin"), _Payload(4096, 208));
    File.WriteAllBytes(pool.PathTo("intact.bin"), _Payload(4096, 209));
    var intact = File.ReadAllBytes(pool.PathTo("intact.bin"));

    File.Delete(pool.PathTo("orphaned.bin"));
    File.Delete(pool.PathTo("intact.bin"));

    pool.WhileUnmounted(() => {
      var sidecars = _HiddenFiles(pool, ".trashinfo");
      sidecars.Should().NotBeEmpty("the bin has to have sidecars before removing one means anything");
      File.Delete(sidecars.First(s => s.Contains("orphaned", StringComparison.OrdinalIgnoreCase)));
    });

    var listed = DbMount.RunExpectingSuccess(_CLI, "pool-trash-list", pool.PoolName, "--json");
    listed.StandardOutput.Should().Contain("intact.bin",
      $"an entry whose sidecar is gone must not take the whole listing with it."
      + $"{Environment.NewLine}{listed.Output}{Environment.NewLine}{pool.MountLog}");

    DbMount.RunExpectingSuccess(_CLI, "pool-trash-restore", pool.PoolName, "intact.bin");
    File.ReadAllBytes(pool.PathTo("intact.bin")).Should().Equal(intact,
      $"and the entries that are still described restore exactly."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Description("A .trashinfo sidecar is rewritten as garbage: the bin still lists and still restores the healthy entries.")]
  public void Trash_GivenASidecarIsGarbage_ThenTheBinStillListsAndRestoresWhatItCan() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);
    File.WriteAllBytes(pool.PathTo("mangled.bin"), _Payload(4096, 210));
    File.WriteAllBytes(pool.PathTo("intact.bin"), _Payload(4096, 211));
    var intact = File.ReadAllBytes(pool.PathTo("intact.bin"));

    File.Delete(pool.PathTo("mangled.bin"));
    File.Delete(pool.PathTo("intact.bin"));

    pool.WhileUnmounted(() => {
      foreach (var sidecar in _HiddenFiles(pool, ".trashinfo").Where(s => s.Contains("mangled", StringComparison.OrdinalIgnoreCase)))
        File.WriteAllText(sidecar, "{ this was opened in a text editor and saved");
    });

    var listed = DbMount.RunExpectingSuccess(_CLI, "pool-trash-list", pool.PoolName, "--json");
    listed.StandardOutput.Should().Contain("intact.bin",
      $"one unreadable sidecar cannot cost the user the rest of their bin."
      + $"{Environment.NewLine}{listed.Output}{Environment.NewLine}{pool.MountLog}");

    DbMount.RunExpectingSuccess(_CLI, "pool-trash-restore", pool.PoolName, "intact.bin");
    File.ReadAllBytes(pool.PathTo("intact.bin")).Should().Equal(intact);
  }

  #endregion

  #region the snapshot store

  [Test]
  [Description("A snapshot's stored version is deleted from the disk: reading it through the view FAILS rather than quietly returning today's file.")]
  public void Snapshot_GivenAStoredVersionIsDeleted_ThenReadingItFailsRatherThanReturningTheLiveFile() {
    // The single most dangerous outcome in this whole suite. The view falls back to the live file
    // when nothing has been preserved for a path — which is correct, and is what makes a snapshot of
    // an idle pool cost nothing. But if a version that WAS preserved is then lost, the same fallback
    // hands back the CURRENT content under the snapshot's name. The caller asked for what the pool
    // held on Monday and got Friday's file, with no error and no way to tell. A restore driven off
    // that overwrites good data with newer data and calls it a recovery.
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var monday = _Payload(32 * 1024, 212);
    File.WriteAllBytes(pool.PathTo("ledger.db"), monday);

    _TakeSnapshot(pool, "monday");
    var friday = _Payload(8 * 1024, 213);
    File.WriteAllBytes(pool.PathTo("ledger.db"), friday);

    pool.WhileUnmounted(() => {
      var versions = _HiddenFiles(pool, ".snapver");
      versions.Should().NotBeEmpty("the overwrite must have preserved a version to destroy");
      foreach (var version in versions)
        File.Delete(version);
    });

    var read = _TryRead(pool.PathTo(Path.Combine(".snapshots", "monday", "ledger.db")));
    read.content.Should().NotEqual(friday,
      $"a snapshot whose stored version is gone must NOT answer with the live file. Returning "
      + $"today's content under Monday's name is undetectable downstream, and a restore driven off "
      + $"it destroys the good copy."
      + $"{Environment.NewLine}{read.error}{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    read.ok.Should().BeFalse(
      $"and it has to say so — an error is the only honest answer once the bytes are gone."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("ledger.db")).Should().Equal(friday,
      "while the live file, which was never in question, is untouched");
  }

  [Test]
  [Description("A snapshot's .snapinfo sidecar is deleted, leaving the stored bytes: the view must not silently fall back to the live file.")]
  public void Snapshot_GivenTheVersionSidecarIsDeleted_ThenTheViewDoesNotSilentlySubstituteTheLiveFile() {
    // Same lie, reached the other way. The bytes are still on the disk; it is the record of WHICH
    // snapshot they belong to that is gone. If losing that record downgrades the path to "nothing
    // was ever preserved", the fallback substitutes the live file exactly as above.
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var monday = _Payload(32 * 1024, 214);
    File.WriteAllBytes(pool.PathTo("ledger.db"), monday);

    _TakeSnapshot(pool, "monday");
    var friday = _Payload(8 * 1024, 215);
    File.WriteAllBytes(pool.PathTo("ledger.db"), friday);

    pool.WhileUnmounted(() => {
      var sidecars = _HiddenFiles(pool, ".snapinfo");
      sidecars.Should().NotBeEmpty("there has to be a sidecar to remove");
      foreach (var sidecar in sidecars)
        File.Delete(sidecar);
    });

    var read = _TryRead(pool.PathTo(Path.Combine(".snapshots", "monday", "ledger.db")));
    read.content.Should().NotEqual(friday,
      $"whatever the view answers, it must not be today's file wearing Monday's name."
      + $"{Environment.NewLine}{read.error}{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Description("A snapshot's index file is corrupted: the pool mounts, the live files are untouched, and the snapshot is not half-listed.")]
  public void Snapshot_GivenTheIndexIsCorrupted_ThenThePoolMountsAndTheLiveFilesAreUntouched() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(16 * 1024, 216);
    File.WriteAllBytes(pool.PathTo("ledger.db"), content);
    _TakeSnapshot(pool, "monday");

    pool.WhileUnmounted(() => {
      var indexes = _HiddenFiles(pool, ".json").Where(f => f.Contains("snapshots", StringComparison.OrdinalIgnoreCase)).ToList();
      indexes.Should().NotBeEmpty("the snapshot has to have an index before corrupting it means anything");
      foreach (var index in indexes)
        File.WriteAllText(index, "{\"Id\": \"not-a-guid\", \"Paths\": ");
    });

    File.ReadAllBytes(pool.PathTo("ledger.db")).Should().Equal(content,
      $"a snapshot's bookkeeping is not the user's data, and losing the first cannot touch the second."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    var listed = DbMount.Run(_CLI, "pool-snapshot-list", pool.PoolName, "--json");
    listed.ExitCode.Should().Be(0,
      $"listing must answer rather than fail — an operator finding out that a snapshot is gone is "
      + $"the whole point of asking.{Environment.NewLine}{listed.Output}");
    listed.StandardOutput.Should().NotContain("monday",
      "and an index that cannot be read is a snapshot that is gone, not one that is half there");

    // the pool still takes new snapshots — one damaged index must not wedge the feature
    var again = DbMount.Run(_CLI, "pool-snapshot-take", pool.PoolName, "tuesday");
    again.ExitCode.Should().Be(0, $"the store has to keep working after losing one index.{Environment.NewLine}{again.Output}");
  }

  [Test]
  [Description("Somebody deletes the whole hidden folder off one member: the pool mounts, and every duplicated file is still there.")]
  public void Utility_GivenTheHiddenTreeIsDeletedFromOneMember_ThenTheOtherMemberCarriesThePool() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(64 * 1024, 217);
    File.WriteAllBytes(pool.PathTo("kept.bin"), content);
    _TakeSnapshot(pool, "monday");

    pool.WhileUnmounted(() => {
      var hidden = Path.Combine(pool.MemberPaths[0], _UTILITY);
      Directory.Exists(hidden).Should().BeTrue("the member has to have a hidden tree to lose");
      Directory.Delete(hidden, recursive: true);
    });

    File.ReadAllBytes(pool.PathTo("kept.bin")).Should().Equal(content,
      $"a user 'cleaning up' a hidden folder on one disk must not cost the pool anything — the "
      + $"journal and the tombstone log are mirrored precisely so this is survivable."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.WriteAllBytes(pool.PathTo("after.bin"), [1, 2]);
    File.ReadAllBytes(pool.PathTo("after.bin")).Should().Equal(new byte[] { 1, 2 },
      "and the pool rebuilds what it needs rather than refusing to work");
  }

  [Test]
  [Description("A member holding a snapshot's stored version is ejected: reading it through the view fails legibly, and the live pool carries on.")]
  public void Snapshot_GivenTheMemberHoldingAVersionIsGone_ThenTheViewFailsLegiblyAndThePoolCarriesOn() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var monday = _Payload(32 * 1024, 218);
    File.WriteAllBytes(pool.PathTo("ledger.db"), monday);
    _TakeSnapshot(pool, "monday");
    var friday = _Payload(4 * 1024, 219);
    File.WriteAllBytes(pool.PathTo("ledger.db"), friday);

    // The member holding a stored .snapver, NOT merely one with a snapshots folder — the index is
    // written across the members and the version is not, so picking the first folder would usually
    // eject the index and test something else entirely.
    var holder = pool.MemberPaths
      .Select((path, index) => (path, index))
      .Where(m => Directory.Exists(Path.Combine(m.path, _UTILITY, "snapshots"))
                  && Directory.EnumerateFiles(Path.Combine(m.path, _UTILITY, "snapshots"), "*.snapver", SearchOption.AllDirectories).Any())
      .Select(m => m.index)
      .DefaultIfEmpty(-1)
      .First();

    if (holder < 0)
      Assert.Ignore("no member holds a preserved version to eject");

    pool.Eject(holder);
    try {
      var read = _TryRead(pool.PathTo(Path.Combine(".snapshots", "monday", "ledger.db")));
      read.content.Should().NotEqual(friday,
        $"a version on a disk that is not here is unavailable, not replaced by the live file."
        + $"{Environment.NewLine}{read.error}{Environment.NewLine}{pool.MountLog}");

      File.ReadAllBytes(pool.PathTo("ledger.db")).Should().Equal(friday,
        $"and losing a disk that held HISTORY does not touch the live namespace."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
    } finally {
      pool.Restore(holder);
    }
  }

  #endregion

  #region the members themselves

  [Test]
  [Description("A member goes read-only under a live pool while the ack policy demands two copies: the write is REFUSED rather than acknowledged with less redundancy than promised.")]
  public void Member_GivenOneGoesReadOnlyAndTwoCopiesAreRequired_ThenTheWriteIsRefusedRatherThanQuietlyLessSafe() {
    // The refusal is the feature, and it is worth a test precisely because it looks like a failure.
    // A pool configured to acknowledge nothing until two copies are down cannot honour that promise
    // with one disk frozen, and the honest answer is to say no. Acking anyway would tell the
    // application its data is redundant when it is not — which on a backup target is the whole
    // proposition quietly evaporating.
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var before = _Payload(32 * 1024, 220);
    File.WriteAllBytes(pool.PathTo("before.bin"), before);
    pool.Remount(); // permission changes bite at open, so take the handles down first

    if (!pool.MakeReadOnly(0))
      Assert.Ignore("this filesystem cannot be made read-only without root");

    try {
      File.ReadAllBytes(pool.PathTo("before.bin")).Should().Equal(before,
        $"a read-only member is still a perfectly good member to READ from."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      var write = () => File.WriteAllBytes(pool.PathTo("after.bin"), _Payload(4096, 221));
      write.Should().Throw<IOException>(
        $"with minCopiesBeforeAck at 2 and one member frozen, the pool cannot make the write as safe "
        + $"as it promised — so it must refuse rather than acknowledge it."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      File.ReadAllBytes(pool.PathTo("before.bin")).Should().Equal(before,
        $"and a refused write costs nothing that was already there."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      // WHY it was refused matters as much as that it was. The duplicate copy the frozen member
      // could not take has to be DEFERRED — the same answer a member with no room gets — so the
      // refusal comes from the ack policy, deliberately, rather than from an exception escaping the
      // duplication path and taking the write down with it.
      pool.MountLog.Should().Contain("refused the duplicate copy",
        $"a member that cannot take the second copy must defer it, not fail the write outright."
        + $"{Environment.NewLine}{pool.MountLog}");
      pool.MountLog.Should().Contain("minCopiesBeforeAck",
        "and the refusal the caller sees must be the ack policy speaking, not a raw disk error");
    } finally {
      pool.Uncripple(0);
    }
  }

  [Test]
  [Description("A member goes read-only under a live pool that acks on one copy: writes are routed to the healthy member and the owed duplicate is deferred.")]
  public void Member_GivenOneGoesReadOnlyAndOneCopySuffices_ThenWritesRouteToTheHealthyMember() {
    // The other half, and the one that used to fail. A member whose filesystem has remounted itself
    // read-only — the commonest real disk failure there is — stays online, keeps plenty of free
    // space and looks like the obvious place to put a new file. Placement kept choosing it, the
    // create retried onto the same member every time, and the duplicate copy it could not take
    // failed the whole write. A healthy disk sat beside it throughout.
    // Duplication 1, because the config validator will not let a duplicated pool acknowledge on
    // fewer copies than it promises — min(2, D) is a floor, and rightly so. One copy is what makes
    // "the other member can simply take it" true, which is the property under test.
    using var pool = MountedPool.Create(members: 2, poolDefaults: """{ "duplication": 1 }""");

    var before = _Payload(32 * 1024, 222);
    File.WriteAllBytes(pool.PathTo("before.bin"), before);
    pool.Remount();

    if (!pool.MakeReadOnly(0))
      Assert.Ignore("this filesystem cannot be made read-only without root");

    try {
      var content = _Payload(16 * 1024, 223);
      var write = () => File.WriteAllBytes(pool.PathTo("after.bin"), content);
      write.Should().NotThrow(
        $"one member can take the file and the ack policy only asks for one copy, so the frozen one "
        + $"must be routed around rather than retried into the ground."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      File.ReadAllBytes(pool.PathTo("after.bin")).Should().Equal(content,
        $"and what it acknowledged reads back."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      File.ReadAllBytes(pool.PathTo("before.bin")).Should().Equal(before, "with nothing older disturbed");
    } finally {
      pool.Uncripple(0);
    }
  }

  [Test]
  [Description("Somebody renames a stored file on a member behind the pool's back: the namespace shows the rename's effect and nothing is silently wrong.")]
  public void Member_GivenAStoredFileIsRenamedBehindThePoolsBack_ThenTheNamespaceAgreesWithTheDisk() {
    // The pool stores whole files under their own names, which is exactly what makes an out-of-band
    // rename possible and what makes it survivable: whatever a user renames a file to on the member,
    // that is what the pool serves. What must NOT happen is a namespace that keeps offering the old
    // name and then fails to open it — a listing that disagrees with reality is worse than either.
    using var pool = MountedPool.Create(members: 2);
    var content = _Payload(16 * 1024, 222);
    File.WriteAllBytes(pool.PathTo("original.bin"), content);
    var copies = pool.WaitForPhysicalCopies("original.bin");
    copies.Should().NotBeEmpty($"the write must reach a member.{Environment.NewLine}{pool.DescribeMembers()}");

    pool.WhileUnmounted(() => {
      foreach (var copy in copies)
        File.Move(copy.where, Path.Combine(Path.GetDirectoryName(copy.where)!, "renamed.bin"));
    });

    var listing = Directory.EnumerateFiles(pool.MountPath).Select(Path.GetFileName).ToList();
    listing.Should().Contain("renamed.bin",
      $"the pool serves what is on the disks; a file renamed there is renamed here."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("renamed.bin")).Should().Equal(content,
      "under its new name, with its content intact");

    foreach (var stale in listing.Where(name => name == "original.bin"))
      _TryRead(pool.PathTo(stale!)).ok.Should().BeTrue(
        $"anything the listing still offers must still open — a name that lists and will not open "
        + $"is the one outcome a caller cannot handle.{Environment.NewLine}{pool.MountLog}");
  }

  #endregion

}
