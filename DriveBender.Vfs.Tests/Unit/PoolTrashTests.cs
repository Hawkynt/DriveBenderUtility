using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Optional per-pool trash (FR-TRASH, §6.14): recoverable deletes, retention/size purge.</summary>
[TestFixture]
[Category("Unit")]
public class PoolTrashTests {

  private static readonly Guid _pool = Guid.Parse("cdcdcdcd-0000-0000-0000-000000000008");

  private FakeVolumeIO _volume1 = null!;
  private FakeVolumeIO _volume2 = null!;
  private DateTime _now;

  [SetUp]
  public void SetUp() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    this._now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
  }

  private PoolFileSystem _CreateFs(string trashJson = """{ "enabled": true }""", int duplication = 2) {
    var cache = new CacheInstance("t" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    var config = ConfigResolver.ResolveEffective(null, $$"""{ "duplication": {{duplication}}, "trash": {{trashJson}} }""");
    var fs = new PoolFileSystem(_pool, [new(this._volume1), new(this._volume2)], cache, config, clock: () => this._now);
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _CreateWithContent(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Unlink_GivenTrashEnabled_WhenDeleted_ThenRecoverableAndHiddenFromNamespace() {
    var fs = this._CreateFs();
    _CreateWithContent(fs, "precious.doc", [1, 2, 3]);

    fs.Unlink("precious.doc");

    var act = () => fs.GetAttributes("precious.doc");
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
    fs.ReadDirectory("").Should().NotContain(e => e.Name == "precious.doc");

    var entries = fs.Trash.List();
    entries.Should().ContainSingle();
    entries[0].OriginalPath.Should().Be("precious.doc");
    entries[0].DeletedUtc.Should().Be(this._now);
  }

  [Test]
  [Category("HappyPath")]
  public void Restore_GivenTrashedFile_WhenRestored_ThenBackAtOriginalPathWithDuplication() {
    var fs = this._CreateFs();
    _CreateWithContent(fs, "precious.doc", [1, 2, 3]);
    fs.Unlink("precious.doc");

    fs.RestoreFromTrash("precious.doc");

    fs.GetAttributes("precious.doc").Length.Should().Be(3);
    var handle = fs.Open("precious.doc", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[3];
    fs.Read(handle, buffer, 0);
    buffer.Should().Equal(new byte[] { 1, 2, 3 });
    fs.Close(handle);

    var holders = new[] { this._volume1, this._volume2 };
    holders.Count(v => v.FileExists("precious.doc", false)).Should().Be(1);
    holders.Count(v => v.FileExists("precious.doc", true)).Should().Be(1, "restore re-establishes duplication (FR-TRASH)");
    fs.Trash.List().Should().BeEmpty();
  }

  [Test]
  [Category("HappyPath")]
  public void Unlink_GivenDropDuplicatesInTrash_WhenDeleted_ThenSingleCopyKeptButRestorable() {
    var fs = this._CreateFs("""{ "enabled": true, "dropDuplicatesInTrash": true }""");
    _CreateWithContent(fs, "f.bin", [9]);

    fs.Unlink("f.bin");

    var trashCopies = new[] { this._volume1, this._volume2 }
      .Count(v => v.FilePaths.Any(path => path.Contains("trash/f.bin", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".trashinfo")));
    trashCopies.Should().Be(1, "space relief keeps a single restorable copy (§6.14)");

    fs.RestoreFromTrash("f.bin");
    fs.GetAttributes("f.bin").Length.Should().Be(1);
  }

  [Test]
  [Category("HappyPath")]
  public void Purge_GivenRetentionElapsed_WhenMaintenanceRuns_ThenOldEntriesPermanentlyGone() {
    var fs = this._CreateFs("""{ "enabled": true, "retention": "7d", "maxSize": "50%" }""");
    _CreateWithContent(fs, "old.bin", [1]);
    fs.Unlink("old.bin");

    this._now += TimeSpan.FromDays(3);
    _CreateWithContent(fs, "young.bin", [2]);
    fs.Unlink("young.bin");

    this._now += TimeSpan.FromDays(5); // old: 8d (expired), young: 5d (kept)
    var purged = fs.PurgeTrash();

    purged.Should().Be(1, "only entries beyond retention purge, oldest first");
    fs.Trash.List().Should().ContainSingle().Which.OriginalPath.Should().Be("young.bin");

    var act = () => fs.RestoreFromTrash("old.bin");
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound, "purge is the real, final delete");
  }

  [Test]
  [Category("EdgeCase")]
  public void Purge_GivenSizeCapExceeded_WhenMaintenanceRuns_ThenOldestGoFirstUntilUnderCap() {
    var fs = this._CreateFs("""{ "enabled": true, "retention": "365d", "maxSize": "40" }""", duplication: 1);
    _CreateWithContent(fs, "a.bin", new byte[30]);
    fs.Unlink("a.bin");
    this._now += TimeSpan.FromHours(1);
    _CreateWithContent(fs, "b.bin", new byte[30]);
    fs.Unlink("b.bin");

    fs.PurgeTrash();

    var remaining = fs.Trash.List();
    remaining.Should().ContainSingle().Which.OriginalPath.Should().Be("b.bin", "the size cap purges oldest first");
  }

  [Test]
  [Category("HappyPath")]
  public void Unlink_GivenTrashDisabled_WhenDeleted_ThenPermanentExactlyAsBefore() {
    var fs = this._CreateFs("""{ "enabled": false }""");
    _CreateWithContent(fs, "f.bin", [1]);

    fs.Unlink("f.bin");

    fs.Trash.List().Should().BeEmpty("disabled trash means deletes stay permanent (default)");
    foreach (var volume in new[] { this._volume1, this._volume2 })
      volume.FilePaths.Should().NotContain(path => path.Contains("f.bin"));
  }

  [Test]
  [Category("EdgeCase")]
  public void Scheduler_GivenTrashEnabled_WhenQuiesced_ThenMaintenanceRuns() {
    var fs = this._CreateFs("""{ "enabled": true, "retention": "1d", "maxSize": "50%" }""");
    _CreateWithContent(fs, "f.bin", [1]);
    fs.Unlink("f.bin");
    this._now += TimeSpan.FromDays(2);

    fs.CreateScheduler().Quiesce();

    fs.Trash.List().Should().BeEmpty("the background maintenance job applies retention (§6.10)");
  }

  [Test]
  [Category("EdgeCase")]
  public void Recovery_GivenInterruptedTrashMove_WhenRecovered_ThenMoveRollsForward() {
    var fs = this._CreateFs(duplication: 1);
    _CreateWithContent(fs, "f.bin", [3]);
    // simulate a crash mid-trash-move: intent logged, primary already moved on volume1
    var journal = fs.Journal;
    journal.LogIntent(JournalOp.TrashMove, "g.bin", PoolTrash.TrashPrefix + "/g.bin");
    this._volume1.Seed(PoolTrash.TrashPrefix + "/g.bin", false, [7]);
    this._volume2.Seed("g.bin", false, [7]);

    new PoolRecovery([this._volume1, this._volume2], journal).Run();

    this._volume2.FileExists("g.bin", false).Should().BeFalse("the interrupted trash move completes on every member");
  }

}
