using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>The journalled M2 write path: write-through durability, duplication, namespace ops.</summary>
[TestFixture]
[Category("Unit")]
public class PoolFileSystemWriteTests {

  private static readonly Guid _pool = Guid.Parse("ffffffff-0000-0000-0000-000000000006");

  private FakeVolumeIO _volume1 = null!;
  private FakeVolumeIO _volume2 = null!;
  private CacheInstance _cache = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    this._fs = this._CreateFs("""{ "duplication": 2, "io": { "mirrorReadSplitThreshold": "64" } }""");
    this._fs.Mount(new(@"X:\"));
  }

  private PoolFileSystem _CreateFs(string configJson) {
    this._cache = new("t" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    return new(_pool, [new(this._volume1), new(this._volume2)], this._cache, ConfigResolver.ResolveEffective(null, configJson));
  }

  private NodeHandle _CreateFileWithContent(string path, byte[] content) {
    var handle = this._fs.Create(path, NodeKind.File, CreateFlags.None);
    if (content.Length > 0)
      this._fs.Write(handle, content, 0, WriteMode.Normal);
    return handle;
  }

  [Test]
  [Category("HappyPath")]
  public void Create_GivenDuplicatedPool_WhenCreated_ThenPrimaryAndShadowOnDistinctDomains() {
    this._fs.MakeDir("docs");
    var handle = this._fs.Create("docs/new.txt", NodeKind.File, CreateFlags.None);
    this._fs.Close(handle);

    var primaries = new[] { this._volume1, this._volume2 }.Count(v => v.FileExists("docs/new.txt", false));
    var shadows = new[] { this._volume1, this._volume2 }.Count(v => v.FileExists("docs/new.txt", true));

    primaries.Should().Be(1);
    shadows.Should().Be(1, "duplication level 2 = primary + 1 shadow (SAFE-DUP)");
    this._fs.GetAttributes("docs/new.txt").Length.Should().Be(0);
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenDuplicatedFile_WhenWritten_ThenBothCopiesCarryTheBytesDurably() {
    var handle = this._CreateFileWithContent("f.bin", [1, 2, 3, 4]);
    this._fs.Close(handle);

    var holders = new[] { this._volume1, this._volume2 };
    var primary = holders.Single(v => v.FileExists("f.bin", false));
    var shadowHolder = holders.Single(v => v.FileExists("f.bin", true));

    primary.GetContent("f.bin", false).Should().Equal(1, 2, 3, 4);
    shadowHolder.GetContent("f.bin", true).Should().Equal(new byte[] { 1, 2, 3, 4 }, "write-through updates every copy before the ack (FR-WT)");

    // durability: the ack'd write survives power loss on every member (SAFE-NOLOSS)
    this._volume1.SimulateCrash();
    this._volume2.SimulateCrash();
    primary.GetContent("f.bin", false).Should().Equal(1, 2, 3, 4);
    shadowHolder.GetContent("f.bin", true).Should().Equal(1, 2, 3, 4);
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenWriteThenRead_WhenReadBack_ThenNewBytesReturned() {
    var handle = this._CreateFileWithContent("f.bin", [9, 9, 9, 9]);

    var buffer = new byte[4];
    this._fs.Read(handle, buffer, 0).Should().Be(4);
    buffer.Should().Equal(9, 9, 9, 9);

    this._fs.Write(handle, [7, 7], 1, WriteMode.Normal);
    this._fs.Read(handle, buffer, 0).Should().Be(4);
    buffer.Should().Equal(new byte[] { 9, 7, 7, 9 }, "a read after a write returns the written bytes (SAFE-COHERE)");
    this._fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenAppendMode_WhenWritten_ThenLandsAtEof() {
    var handle = this._CreateFileWithContent("log.txt", [1, 2]);
    this._fs.Write(handle, [3, 4], 0, WriteMode.Append);

    this._fs.GetAttributes("log.txt").Length.Should().Be(4);
    var buffer = new byte[4];
    this._fs.Read(handle, buffer, 0);
    buffer.Should().Equal(1, 2, 3, 4);
    this._fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void SetLength_GivenGrowAndShrink_WhenApplied_ThenAllCopiesFollow() {
    var handle = this._CreateFileWithContent("t.bin", [1, 2, 3, 4]);

    this._fs.SetLength(handle, 2);
    this._fs.GetAttributes("t.bin").Length.Should().Be(2);

    this._fs.SetLength(handle, 6);
    var buffer = new byte[6];
    this._fs.Read(handle, buffer, 0).Should().Be(6);
    buffer.Should().Equal(new byte[] { 1, 2, 0, 0, 0, 0 }, "growth zero-fills (FR-TRUNC)");

    var staged = "t.bin." + DivisonM.DriveBender.DriveBenderConstants.TEMP_EXTENSION; // still open — physically a temp (FR-STAGED-WRITE)
    var holders = new[] { this._volume1, this._volume2 };
    holders.Single(v => v.FileExists(staged, true)).GetContent(staged, true)!.Length.Should().Be(6, "truncate applies to all copies");
    this._fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Unlink_GivenDuplicatedFile_WhenDeleted_ThenNoCopyOrOrphanRemains() {
    var handle = this._CreateFileWithContent("gone.bin", [1]);
    this._fs.Close(handle);

    this._fs.Unlink("gone.bin");

    foreach (var volume in new[] { this._volume1, this._volume2 }) {
      volume.FileExists("gone.bin", false).Should().BeFalse();
      volume.FileExists("gone.bin", true).Should().BeFalse("no orphan copies remain (FR-DELETE)");
    }

    var act = () => this._fs.GetAttributes("gone.bin");
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("HappyPath")]
  public void Rename_GivenDuplicatedFile_WhenRenamed_ThenAllCopiesFlipAndContentSurvives() {
    var handle = this._CreateFileWithContent("old.bin", [5, 5]);
    this._fs.Close(handle);

    this._fs.Rename("old.bin", "new.bin", RenameFlags.None);

    this._fs.GetAttributes("new.bin").Length.Should().Be(2);
    foreach (var volume in new[] { this._volume1, this._volume2 }) {
      volume.FileExists("old.bin", false).Should().BeFalse();
      volume.FileExists("old.bin", true).Should().BeFalse();
    }

    var readHandle = this._fs.Open("new.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[2];
    this._fs.Read(readHandle, buffer, 0);
    buffer.Should().Equal(5, 5);
    this._fs.Close(readHandle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenExistingTargetWithReplaceFlag_WhenRenamed_ThenTargetOverwrittenWithoutOrphans() {
    this._fs.Close(this._CreateFileWithContent("src.bin", [1]));
    this._fs.Close(this._CreateFileWithContent("dst.bin", [2]));

    this._fs.Rename("src.bin", "dst.bin", RenameFlags.ReplaceExisting);

    var handle = this._fs.Open("dst.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[1];
    this._fs.Read(handle, buffer, 0);
    buffer.Should().Equal(1);
    this._fs.Close(handle);
  }

  [Test]
  [Category("Exception")]
  public void Rename_GivenExistingTargetWithoutReplaceFlag_WhenRenamed_ThenExists() {
    this._fs.Close(this._CreateFileWithContent("src.bin", [1]));
    this._fs.Close(this._CreateFileWithContent("dst.bin", [2]));

    var act = () => this._fs.Rename("src.bin", "dst.bin", RenameFlags.None);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
  }

  [Test]
  [Category("HappyPath")]
  public void Rename_GivenFolderWithFilesAndShadows_WhenRenamed_ThenSubtreeFlipsOnEveryMember() {
    this._fs.MakeDir("photos");
    this._fs.Close(this._CreateFileWithContent("photos/pic.bin", [1, 2, 3]));

    this._fs.Rename("photos", "images", RenameFlags.None);

    this._fs.GetAttributes("images/pic.bin").Length.Should().Be(3, "the file follows the renamed folder");
    var members = new[] { this._volume1, this._volume2 };
    members.Count(v => v.FileExists("images/pic.bin", false)).Should().Be(1, "the primary moved");
    members.Count(v => v.FileExists("images/pic.bin", true)).Should().Be(1, "the embedded shadow moved with the folder");
    members.Any(v => v.FolderExists("photos", false)).Should().BeFalse("nothing lingers under the old name");

    var readHandle = this._fs.Open("images/pic.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[3];
    this._fs.Read(readHandle, buffer, 0);
    this._fs.Close(readHandle);
    buffer.Should().Equal(1, 2, 3);
  }

  [Test]
  [Category("HappyPath")]
  public void Rename_GivenFolderWithOpenChildHandle_WhenRenamed_ThenHandleStaysUsable() {
    this._fs.MakeDir("work");
    var handle = this._CreateFileWithContent("work/doc.bin", [9, 9]);

    this._fs.Rename("work", "done", RenameFlags.None);
    this._fs.Write(handle, [7], 2, WriteMode.Normal);
    this._fs.Close(handle);

    var readHandle = this._fs.Open("done/doc.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[3];
    this._fs.Read(readHandle, buffer, 0);
    this._fs.Close(readHandle);
    buffer.Should().Equal(9, 9, 7); // the open handle followed the folder rename
  }

  [Test]
  [Category("Exception")]
  public void Rename_GivenFolderTargetAlreadyExists_WhenRenamed_ThenExists() {
    this._fs.MakeDir("a");
    this._fs.MakeDir("b");

    var act = () => this._fs.Rename("a", "b", RenameFlags.None);

    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
  }

  [Test]
  [Category("Exception")]
  public void Rename_GivenFolderMovedIntoItself_WhenRenamed_ThenRefused() {
    this._fs.MakeDir("a");

    var act = () => this._fs.Rename("a", "a/b", RenameFlags.None);

    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.InvalidArgument);
  }

  [Test]
  [Category("HappyPath")]
  public void MakeDirRemoveDir_GivenEmptyFolder_WhenRoundTripped_ThenNamespaceConsistent() {
    this._fs.MakeDir("newdir");
    this._fs.ReadDirectory("").Should().ContainSingle(e => e.Name == "newdir" && e.Kind == NodeKind.Directory);

    this._fs.RemoveDir("newdir");
    this._fs.ReadDirectory("").Should().NotContain(e => e.Name == "newdir");
  }

  [Test]
  [Category("Exception")]
  public void RemoveDir_GivenNonEmptyFolder_WhenRemoved_ThenNotEmpty() {
    this._fs.MakeDir("dir");
    this._fs.Close(this._fs.Create("dir/f.txt", NodeKind.File, CreateFlags.None));

    var act = () => this._fs.RemoveDir("dir");
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotEmpty);
  }

  [Test]
  [Category("Exception")]
  public void Create_GivenExclusiveFlagAndExistingFile_WhenCreated_ThenExists() {
    this._fs.Close(this._CreateFileWithContent("f.bin", [1]));

    var act = () => this._fs.Create("f.bin", NodeKind.File, CreateFlags.Exclusive);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Exists);
  }

  [Test]
  [Category("EdgeCase")]
  public void Create_GivenTruncateFlagAndExistingFile_WhenCreated_ThenContentCleared() {
    this._fs.Close(this._CreateFileWithContent("f.bin", [1, 2, 3]));

    var handle = this._fs.Create("f.bin", NodeKind.File, CreateFlags.Truncate);
    this._fs.GetAttributes("f.bin").Length.Should().Be(0);
    this._fs.Close(handle);
  }

  [Test]
  [Category("Exception")]
  public void Create_GivenMissingParent_WhenCreated_ThenNotFound() {
    var act = () => this._fs.Create("no-dir/f.bin", NodeKind.File, CreateFlags.None);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("Exception")]
  public void Write_GivenQuorumUnreachableAndDegradedWritesRefused_WhenWriting_ThenNoAckAndError() {
    // strict mode: the pool explicitly opted out of degraded writes (SAFE-LZ stays a hard floor)
    var fs = this._CreateFs("""{ "duplication": 2, "resilience": { "acceptDegradedWrites": false } }""");
    fs.Mount(new(@"X:\"));
    var handle = fs.Create("critical.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1, 2], 0, WriteMode.Normal);

    // the shadow holder disappears — only 1 of the required 2 copies is reachable
    var staged = "critical.bin." + DivisonM.DriveBender.DriveBenderConstants.TEMP_EXTENSION; // still open — physically a temp
    var shadowHolder = new[] { this._volume1, this._volume2 }.Single(v => v.FileExists(staged, true));
    shadowHolder.IsOnline = false;
    fs.Placement.Invalidate(staged);

    var act = () => fs.Write(handle, [9], 0, WriteMode.Normal);
    act.Should().Throw<PoolFsException>("fewer copies than minCopiesBeforeAck must never be acknowledged when degraded writes are refused (SAFE-LZ)");
    shadowHolder.IsOnline = true; // Close publishes the staged file — both members reachable again
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenMemberLostMidFile_WhenWriting_ThenDegradedWriteSucceedsOnSurvivor() {
    // default mode (§10 SAFE-DEGRADE): one lost drive degrades redundancy, not availability
    var handle = this._CreateFileWithContent("keep-going.bin", [1, 2]);
    var staged = "keep-going.bin." + DivisonM.DriveBender.DriveBenderConstants.TEMP_EXTENSION;
    var shadowHolder = new[] { this._volume1, this._volume2 }.Single(v => v.FileExists(staged, true));
    shadowHolder.IsOnline = false;
    this._fs.Placement.Invalidate(staged);

    this._fs.Write(handle, [9], 0, WriteMode.Normal);
    this._fs.Close(handle);

    var survivor = new[] { this._volume1, this._volume2 }.Single(v => v != shadowHolder);
    survivor.GetContent("keep-going.bin", false).Should().Equal(new byte[] { 9, 2 }, "the surviving copy carries every acknowledged byte (SAFE-NOLOSS)");
  }

  [Test]
  [Category("Exception")]
  public void Write_GivenVolumeFull_WhenWriting_ThenNoSpaceSurfaced() {
    this._volume1.Capacity = 2048;
    this._volume2.Capacity = 2048;
    var handle = this._CreateFileWithContent("big.bin", [1]);

    var act = () => this._fs.Write(handle, new byte[100_000], 0, WriteMode.Normal);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace, "FR-BIGFILE: a file no volume can hold reports NoSpace clearly");
    this._fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Create_GivenNonDuplicatedPool_WhenCreated_ThenNoShadowCopy() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1");
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2");
    var fs = this._CreateFs("""{ "duplication": 1 }""");
    fs.Mount(new(@"Y:\"));

    fs.Close(fs.Create("single.bin", NodeKind.File, CreateFlags.None));

    new[] { this._volume1, this._volume2 }.Count(v => v.FileExists("single.bin", true)).Should().Be(0);
  }

  [Test]
  [Category("EdgeCase")]
  public void Create_GivenBothMembersOnOnePhysicalDisk_WhenCreated_ThenShadowDeferredNotCoLocated() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-SAME");
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-SAME");
    var fs = this._CreateFs("""{ "duplication": 2, "write": { "minCopiesBeforeAck": 1 } }""");
    fs.Mount(new(@"Y:\"));

    fs.Close(fs.Create("f.bin", NodeKind.File, CreateFlags.None));

    var copies = new[] { this._volume1, this._volume2 }.Count(v => v.FileExists("f.bin", false) || v.FileExists("f.bin", true));
    copies.Should().Be(1, "copies are never co-located in one failure domain (SAFE-PHYS)");
  }

}
