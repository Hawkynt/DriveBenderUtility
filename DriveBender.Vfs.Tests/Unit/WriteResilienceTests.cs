using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// SAFE-NOLOSS around vanishing storages: the write-back cache holds every block until it
/// reached all available storages — a member failing mid-write redirects instead of erroring,
/// lagging copies converge from RAM (never re-read from the broken storage), and a full cache
/// throttles the writer instead of dropping unwritten blocks.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WriteResilienceTests {

  private static readonly Guid _pool = Guid.Parse("aaaa5afe-0000-0000-0000-000000000012");

  private static string _Staged(string path) => path + "." + DivisonM.DriveBender.DriveBenderConstants.TEMP_EXTENSION;

  private static CacheInstance _Cache(string? writeBufferMax = null) => new("w" + Guid.NewGuid().ToString("N"), new() {
    Size = writeBufferMax == null ? "262144" : "2048",
    BlockSize = "16",
    MetadataEntries = 1000,
    MetadataTtl = "1m",
    Split = writeBufferMax == null ? null : new() { Mode = CacheSplitMode.Separate, ReadCacheMax = "1024", WriteBufferMax = writeBufferMax },
  });

  [Test]
  [Category("Exception")]
  public void Write_GivenAckStorageFailsMidWrite_WhenAnotherHolderAvailable_ThenBlockRedirectsAndDriverSeesSuccess() {
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    var v3 = new FakeVolumeIO(Guid.NewGuid(), "v3", "PHYS-3", capacity: 1L << 20);
    var fs = new PoolFileSystem(_pool, [new(v1), new(v2), new(v3)], _Cache(),
      ConfigResolver.ResolveEffective(null, """{ "duplication": 3, "write": { "policy": "write-back", "minCopiesBeforeAck": 2 } }"""));
    fs.Mount(new(@"X:\"));

    var handle = fs.Create("critical.bin", NodeKind.File, CreateFlags.None); // 3 copies exist
    v2.AlwaysFail(VolumeOp.Write); // this storage now dies on every data write

    var act = () => fs.Write(handle, [1, 2, 3], 0, WriteMode.Normal);
    act.Should().NotThrow("a storage failing mid-write redirects the block — the driver must not see an error while a quorum is reachable");

    // the failed member holds nothing; the survivors carry the quorum
    var survivors = new[] { v1, v3 }.Count(v => (v.GetContent(_Staged("critical.bin"), false) ?? v.GetContent(_Staged("critical.bin"), true))?.Take(3).ToArray().SequenceEqual(new byte[] { 1, 2, 3 }) == true);
    survivors.Should().Be(2, "the quorum landed on the working storages");

    // the storage recovers: its copy converges FROM THE WRITE CACHE, not from another read
    v2.ClearFaults();
    fs.CreateScheduler().Quiesce();
    fs.Close(handle);
    var recovered = v2.GetContent("critical.bin", false) ?? v2.GetContent("critical.bin", true);
    recovered.Should().NotBeNull();
    recovered!.Take(3).Should().Equal(1, 2, 3);
  }

  [Test]
  [Category("HappyPath")]
  public void OwedSync_GivenTheOnlyWrittenStorageTurnsUnreadable_WhenConverging_ThenLaggingCopyServedFromCacheNotFromDisk() {
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    var fs = new PoolFileSystem(_pool, [new(v1), new(v2)], _Cache(),
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 1 } }"""));
    fs.Mount(new(@"X:\"));

    var handle = fs.Create("solo.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [4, 5, 6], 0, WriteMode.Normal); // acked after ONE storage took it

    // find which storage got the sync block and make it UNREADABLE — the pending copy for the
    // other storage must come from the write cache, never from reading this one back
    var holder = (v1.GetContent(_Staged("solo.bin"), false) ?? v1.GetContent(_Staged("solo.bin"), true))?.Take(3).ToArray().SequenceEqual(new byte[] { 4, 5, 6 }) == true ? v1 : v2;
    var lagging = holder == v1 ? v2 : v1;
    holder.AlwaysFail(VolumeOp.OpenRead);

    fs.CreateScheduler().Quiesce();

    var converged = lagging.GetContent(_Staged("solo.bin"), false) ?? lagging.GetContent(_Staged("solo.bin"), true);
    converged.Should().NotBeNull("the lagging copy converged from RAM while the written storage was unreadable");
    converged!.Take(3).Should().Equal(4, 5, 6);
    holder.ClearFaults();
    fs.Close(handle);
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenWriteCacheFull_WhenNextBlockArrives_ThenWriterThrottledUntilOldBlocksLandEverywhere() {
    var v1 = new FakeVolumeIO(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    var v2 = new FakeVolumeIO(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    var fs = new PoolFileSystem(_pool, [new(v1), new(v2)], _Cache(writeBufferMax: "8"),
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 1 } }"""));
    fs.Mount(new(@"X:\"));

    // first file stages its owed copy: the 8-byte budget is now exhausted
    var a = fs.Create("a.bin", NodeKind.File, CreateFlags.None);
    fs.Write(a, [1, 1, 1, 1, 1, 1, 1, 1], 0, WriteMode.Normal);
    fs.WriteBuffer.IsDirty("a.bin").Should().BeTrue();

    // the next block cannot stage — the writer is throttled while a.bin flushes everywhere,
    // then the new block is accepted; nothing errors, nothing is dropped
    var b = fs.Create("b.bin", NodeKind.File, CreateFlags.None);
    var act = () => fs.Write(b, [2, 2, 2, 2, 2, 2, 2, 2], 0, WriteMode.Normal);
    act.Should().NotThrow("a full write cache throttles, it never drops or errors");

    fs.WriteBuffer.IsDirty("a.bin").Should().BeFalse("the throttle flushed the oldest dirty file to ALL its storages first");
    new[] { v1, v2 }.Count(v => (v.GetContent(_Staged("a.bin"), false) ?? v.GetContent(_Staged("a.bin"), true))?.Take(8).All(x => x == 1) == true)
      .Should().Be(2, "a.bin reached every storage before its cache space was reused");

    fs.CreateScheduler().Quiesce();
    fs.Close(a);
    fs.Close(b);
    new[] { v1, v2 }.Count(v => (v.GetContent("b.bin", false) ?? v.GetContent("b.bin", true))?.Take(8).All(x => x == 2) == true)
      .Should().Be(2, "the throttled block converged everywhere too");
  }

}
