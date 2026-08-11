using DivisonM.Backends;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The cases where the engine could silently serve, keep or resurrect the wrong bytes: a copy
/// that is merely BEHIND rather than broken, a remote that fails to answer rather than answering
/// "no", and a member that drops out in the middle of a delete. None of these are I/O errors the
/// caller ever sees — which is exactly why each needs pinning.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DataSafetyTests {

  private static readonly Guid _pool = Guid.Parse("5afe0000-0000-0000-0000-00000000005a");

  private static PoolFileSystem _NewPool(FakeVolumeIO v1, FakeVolumeIO v2, string config)
    => new(_pool, [new(v1), new(v2)],
      new("safety" + Guid.NewGuid().ToString("N"), new() { Size = "4194304", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, config));

  private const string _MIRRORED = """
    { "duplication": 2, "write": { "policy": "write-through" }, "readAhead": { "enabled": false } }
    """;

  #region a copy that is behind, not broken

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenOneCopyIsTruncatedBehindTheOthers_ThenTheFullContentIsServedFromTheGoodCopy() {
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24);
    using var fs = _NewPool(v1, v2, _MIRRORED);
    fs.Mount(new(@"X:\"));

    var content = new byte[2048];
    new Random(11).NextBytes(content);
    var write = fs.Create("lagging.bin", NodeKind.File, CreateFlags.None);
    fs.Write(write, content, 0, WriteMode.Normal);
    fs.Close(write);

    // One copy is left holding an OLDER, shorter version — exactly what a member that was away
    // during the write looks like on its return: not an I/O error, just stale. Seeding restores
    // the fake's default (older) timestamp, which is what makes it identifiable as the stale one.
    var holder = v1.GetContent("lagging.bin", false) != null ? v1 : v2;
    var shadowSide = holder.GetContent("lagging.bin", false) == null;
    holder.Seed("lagging.bin", shadowSide, content.AsSpan(0, 512).ToArray());
    fs.Cache.Pages.InvalidatePool(_pool);
    fs.Cache.Metadata.InvalidatePool(_pool);

    var handle = fs.Open("lagging.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[content.Length];
    var read = 0;
    while (read < buffer.Length) {
      var got = fs.Read(handle, buffer.AsSpan(read), read);
      if (got <= 0)
        break;

      read += got;
    }

    fs.Close(handle);
    read.Should().Be(content.Length, "the intact copy can serve every byte — a lagging copy must be failed over, not served short");
    buffer.Should().Equal(content);
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenOneCopyIsBehind_ThenItsShortBlockIsNeverCachedAndRereadsStayCorrect() {
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24);
    using var fs = _NewPool(v1, v2, _MIRRORED);
    fs.Mount(new(@"X:\"));

    var content = new byte[2048];
    new Random(12).NextBytes(content);
    var write = fs.Create("cached.bin", NodeKind.File, CreateFlags.None);
    fs.Write(write, content, 0, WriteMode.Normal);
    fs.Close(write);

    var holder = v1.GetContent("cached.bin", false) != null ? v1 : v2;
    var shadowSide = holder.GetContent("cached.bin", false) == null;
    holder.Seed("cached.bin", shadowSide, content.AsSpan(0, 512).ToArray());
    fs.Cache.Pages.InvalidatePool(_pool);
    fs.Cache.Metadata.InvalidatePool(_pool);

    var handle = fs.Open("cached.bin", AccessMode.Read, ShareMode.Read);
    var first = _ReadWhole(fs, handle, content.Length);
    var second = _ReadWhole(fs, handle, content.Length); // served from the page cache
    fs.Close(handle);

    first.Should().Equal(content);
    second.Should().Equal(content,
      "the lagging copy's short block must never enter the page cache — otherwise the failover is undone by the next cache hit");
  }

  private static byte[] _ReadWhole(PoolFileSystem fs, NodeHandle handle, int length) {
    var buffer = new byte[length];
    var read = 0;
    while (read < length) {
      var got = fs.Read(handle, buffer.AsSpan(read), read);
      if (got <= 0)
        break;

      read += got;
    }

    return buffer.AsSpan(0, read).ToArray();
  }

  #endregion

  #region a remote that does not answer

  /// <summary>A store whose transport can be broken at will — the blip that must never read as "the file is gone".</summary>
  private sealed class FlakyStore(IWholeFileStore inner) : IWholeFileStore {
    public bool Broken;

    private T _Guard<T>(Func<T> operation) {
      if (this.Broken)
        throw new IOException("injected: the transport dropped");

      return operation();
    }

    public bool ThreadSafe => inner.ThreadSafe;
    public void Connect() => this._Guard<object?>(() => { inner.Connect(); return null; });
    public bool Probe() => this._Guard(inner.Probe);
    public StoreMeta? Stat(string path) => this._Guard(() => inner.Stat(path));
    public IEnumerable<StoreEntry> List(string path) => this._Guard(() => inner.List(path).ToArray().AsEnumerable());
    public byte[] Download(string path) => this._Guard(() => inner.Download(path));
    public void Upload(string path, byte[] content) => this._Guard<object?>(() => { inner.Upload(path, content); return null; });
    public void DeleteFile(string path) => this._Guard<object?>(() => { inner.DeleteFile(path); return null; });
    public void CreateFolder(string path) => this._Guard<object?>(() => { inner.CreateFolder(path); return null; });
    public void DeleteFolder(string path) => this._Guard<object?>(() => { inner.DeleteFolder(path); return null; });
    public void Dispose() => inner.Dispose();
  }

  [Test]
  [Category("Exception")]
  public void FileExists_GivenTheTransportFails_ThenTheMemberReportsOfflineRatherThanClaimingTheFileIsGone() {
    var root = Path.Combine(Path.GetTempPath(), "dbsafety-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
      var flaky = new FlakyStore(new DirectoryStore(root));
      var now = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
      using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "remote", "REMOTE-1", flaky, () => now);

      using (var stream = volume.OpenWrite("kept.bin", false, true)) {
        stream.Write([1, 2, 3, 4], 0, 4);
        stream.Flush();
      }

      volume.FileExists("kept.bin", false).Should().BeTrue();
      volume.IsOnline.Should().BeTrue();

      flaky.Broken = true;

      // the file is still there — the STORE simply cannot be asked. Answering "false" would let
      // a delete skip this member's copy (a ghost that resurrects) and let placement conclude a
      // copy is missing; the member must instead be treated as unreachable.
      volume.FileExists("kept.bin", false).Should().BeFalse("an unanswerable query is not a 'yes'");
      volume.IsOnline.Should().BeFalse("a member that cannot answer must be reported unreachable, not healthy-and-empty");

      flaky.Broken = false;

      // a member believed DOWN is re-probed on a short cadence: writing it off for the full
      // 30 s online-TTL would turn one dropped packet into a lastingly degraded pool
      now = now.AddSeconds(3);
      volume.IsOnline.Should().BeTrue("a recovered member must be picked up again within seconds, not after the full online TTL");
      volume.FileExists("kept.bin", false).Should().BeTrue("the file was never gone");
    } finally {
      if (Directory.Exists(root))
        Directory.Delete(root, true);
    }
  }

  #endregion

  #region a member that drops out mid-delete

  [Test]
  [Category("EdgeCase")]
  public void Unlink_GivenAMemberFailsDuringTheDelete_ThenItIsTombstonedSoTheFileCannotResurrect() {
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24);
    using var fs = _NewPool(v1, v2, _MIRRORED);
    fs.Mount(new(@"X:\"));

    var write = fs.Create("doomed.bin", NodeKind.File, CreateFlags.None);
    fs.Write(write, [7, 7, 7, 7], 0, WriteMode.Normal);
    fs.Close(write);

    v1.GetContent("doomed.bin", false).Should().NotBeNull();
    v2.GetContent("doomed.bin", true).Should().NotBeNull();

    // v2 dies exactly during the delete — it is still ONLINE when the tombstone sweep for
    // already-offline members runs, so nothing would have been recorded for it
    v2.AlwaysFail(VolumeOp.Delete);
    fs.Unlink("doomed.bin");
    v2.ClearFaults();

    v1.GetContent("doomed.bin", false).Should().BeNull("the reachable member's copy is gone");
    v2.GetContent("doomed.bin", true).Should().NotBeNull("the failing member kept its copy — that is the situation under test");

    // the surviving copy must NOT come back as a live file: the delete it missed is replayed
    var tombstones = new TombstoneLog([v1, v2]);
    var replayed = tombstones.ReplayFor(v2, [v1.MemberId, v2.MemberId]);

    replayed.Should().BeGreaterThan(0, "the missed delete must have been recorded for the member that failed it");
    v2.GetContent("doomed.bin", true).Should().BeNull("replaying the tombstone removes the orphan before it can resurrect");
  }

  [Test]
  [Category("Exception")]
  public void Unlink_GivenEveryMemberFailsTheDelete_ThenItIsReportedAsAFailureRatherThanSilentlyAccepted() {
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24);
    using var fs = _NewPool(v1, v2, _MIRRORED);
    fs.Mount(new(@"X:\"));

    var write = fs.Create("stuck.bin", NodeKind.File, CreateFlags.None);
    fs.Write(write, [1], 0, WriteMode.Normal);
    fs.Close(write);

    v1.AlwaysFail(VolumeOp.Delete);
    v2.AlwaysFail(VolumeOp.Delete);

    var deleting = () => fs.Unlink("stuck.bin");
    deleting.Should().Throw<PoolFsException>("a delete that removed nothing anywhere is a failure, not a degraded success");

    v1.ClearFaults();
    v2.ClearFaults();
  }

  #endregion

}
