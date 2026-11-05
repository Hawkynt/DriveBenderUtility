using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Administrative media operations run against a pool that is MOUNTED and serving.
///
/// The daemon starts them on a background thread while the filesystem stays up — that is the whole
/// point of them, an operator does not unmount a pool to replace a disk. But they move files
/// between members through <see cref="MediaLifecycle"/>, which talks to the volumes directly and
/// knows nothing about the engine's caches, so the engine's idea of where a file lives can be left
/// describing a layout that no longer exists.
/// </summary>
[TestFixture]
[Category("Unit")]
public class MountedMediaOpTests {

  private static readonly Guid _pool = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 20);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 20);
  }

  private PoolFileSystem _Mounted(string config) {
    var cache = new CacheInstance("mm" + Guid.NewGuid().ToString("N"),
      new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._a), new(this._b)], cache,
      ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _Create(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static byte[] _Read(PoolFileSystem fs, string path, int length) {
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var buffer = new byte[length];
      var read = fs.Read(handle, buffer, 0);
      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  [Test]
  [Category("Exception")]
  public void ScatterAndRemove_GivenThePoolIsMounted_ThenTheMovedFileIsStillVisible() {
    // Removing a member from a live pool relocates everything it holds onto the others. The data is
    // safe the whole time — but the engine resolved this file's copies before the move and cached
    // the answer, so it goes on believing the file lives on the member that no longer has it.
    var fs = this._Mounted("""{ "duplication": 1, "trash": { "enabled": false } }""");
    _Create(fs, "moved.bin", [1, 2, 3, 4]);

    // a read through the mount, so the placement cache definitely holds the pre-move layout
    _Read(fs, "moved.bin", 4).Should().Equal(new byte[] { 1, 2, 3, 4 });

    var holder = this._a.GetContent("moved.bin", false) != null ? this._a : this._b;
    var journal = new Journal(new MemberJournalStore([this._a, this._b]));
    new MediaLifecycle([this._a, this._b], journal, 1).ScatterAndRemove(holder.MemberId);

    // the bytes are demonstrably still in the pool, on the other member
    var survivor = holder == this._a ? this._b : this._a;
    survivor.GetContent("moved.bin", false).Should().Equal(new byte[] { 1, 2, 3, 4 },
      "the media operation must have relocated it rather than lost it");

    fs.GetAttributes("moved.bin").Length.Should().Be(4,
      "the file was moved between members, not deleted — a mounted pool must not lose sight of it "
      + "because its own cache still describes where it used to be");

    _Read(fs, "moved.bin", 4).Should().Equal(new byte[] { 1, 2, 3, 4 },
      "and it must still read back");
  }

  [Test]
  [Category("Exception")]
  public void RestorePool_GivenThePoolIsMountedAndAPrimaryWasLost_ThenTheFileIsStillServed() {
    // The promote: no primary survives, so the surviving shadow is copied to a primary and the
    // shadow is deleted. That rearranges exactly the thing the engine cached — and RestorePool is
    // run by the daemon against a MOUNTED pool, from the UI, while reads are being served.
    var fs = this._Mounted("""{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "trash": { "enabled": false } }""");
    _Create(fs, "promoted.bin", [9, 8, 7, 6]);

    var primaryHolder = this._a.GetContent("promoted.bin", false) != null ? this._a : this._b;
    var shadowHolder = primaryHolder == this._a ? this._b : this._a;
    shadowHolder.GetContent("promoted.bin", true).Should().NotBeNull("the file must start duplicated");

    // the primary's member loses it (bit rot, a bad sector, an out-of-band delete)
    primaryHolder.Delete("promoted.bin", false);

    // a read now, so the cache records "the only copy is the shadow"
    _Read(fs, "promoted.bin", 4).Should().Equal(new byte[] { 9, 8, 7, 6 });

    var journal = new Journal(new MemberJournalStore([this._a, this._b]));
    new MediaLifecycle([this._a, this._b], journal, 2, allowSamePhysical: true).RestorePool();

    // the promote moved the surviving copy from the shadow container to a primary
    shadowHolder.GetContent("promoted.bin", false).Should().Equal(new byte[] { 9, 8, 7, 6 },
      "the restore must have promoted the shadow rather than lost it");

    fs.GetAttributes("promoted.bin").Length.Should().Be(4,
      "the restore turned this file's shadow into a primary on the same member. Nothing was lost and "
      + "nothing moved disk — but the engine cached 'the copy is in the shadow container', and that "
      + "container is now empty. A mounted pool must not lose a file to its own repair.");

    _Read(fs, "promoted.bin", 4).Should().Equal(new byte[] { 9, 8, 7, 6 },
      "and it must still read back");
  }

  [Test]
  [Category("Exception")]
  public void Restore_GivenThePoolIsMountedAndTheOnlyCopyIsWrittenDuringThePromote_ThenTheWriteSurvives() {
    // Restore used to promote a lone shadow by copying it to a primary and deleting the shadow,
    // straight on the member, while the mount went on serving the file. A write landing between the
    // copy and the rename went to the shadow the engine still knew about; the rename then published
    // the image taken before it, and the delete removed the only copy holding the write.
    var fs = this._Mounted("""{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "write": { "minCopiesBeforeAck": 1 }, "trash": { "enabled": false } }""");
    _Create(fs, "promoted.bin", [9, 8, 7, 6]);

    var primaryHolder = this._a.GetContent("promoted.bin", false) != null ? this._a : this._b;
    var shadowHolder = primaryHolder == this._a ? this._b : this._a;
    primaryHolder.Delete("promoted.bin", false);
    _Read(fs, "promoted.bin", 4).Should().Equal(new byte[] { 9, 8, 7, 6 });

    // the application writes on its own thread the moment the promoted copy is about to be renamed
    // into place — and is never waited on for long, so a restore that holds the file is not wedged
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    Thread? application = null;
    var fired = 0;
    shadowHolder.BeforeOperation = (op, path) => {
      if (op != VolumeOp.AtomicReplace || path != "promoted.bin" || Interlocked.Exchange(ref fired, 1) == 1)
        return;

      application = new Thread(() => {
        try {
          var handle = fs.Open("promoted.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
          fs.Write(handle, [1, 1, 1, 1], 0, WriteMode.Normal);
          fs.Close(handle);
        } catch (PoolFsException e) {
          failures.Add($"{e.Error} {e.Message}");
        }
      });
      application.Start();
      application.Join(TimeSpan.FromMilliseconds(300));
    };

    _RestoreMounted(fs);
    shadowHolder.BeforeOperation = null;
    fired.Should().Be(1, "the restore must have promoted the shadow");
    application!.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("the application's write finishes");
    failures.Should().BeEmpty();

    fs.CreateScheduler().Quiesce(); // whatever the restore left owed converges
    _Read(fs, "promoted.bin", 4).Should().Equal(new byte[] { 1, 1, 1, 1 }, "the acknowledged write is the file");
    foreach (var member in new[] { this._a, this._b })
      foreach (var shadow in new[] { false, true })
        if (member.GetContent("promoted.bin", shadow) is { } copy)
          copy.Should().Equal(new byte[] { 1, 1, 1, 1 }, $"the copy on '{member.DisplayName}' (shadow: {shadow}) is the file, not the image taken before the write");
  }

  /// <summary>
  /// The restore the daemon runs for a mounted pool, with this thread standing in for the mount's
  /// scheduler pump — which is what drives the healer in a real mount.
  /// </summary>
  private void _RestoreMounted(PoolFileSystem fs) {
    var media = new MediaLifecycle([this._a, this._b], fs.Journal, 2, allowSamePhysical: true);
    var restore = Task.Run(() => media.RestoreMounted(fs, settleCheckEvery: TimeSpan.FromMilliseconds(100), giveUpAfter: TimeSpan.FromSeconds(5)));
    var scheduler = fs.CreateScheduler();
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (!restore.IsCompleted && DateTime.UtcNow < deadline)
      if (scheduler.Pump() == 0)
        Thread.Sleep(5);

    restore.Wait(TimeSpan.FromSeconds(1)).Should().BeTrue("the restore must finish");
  }

}
