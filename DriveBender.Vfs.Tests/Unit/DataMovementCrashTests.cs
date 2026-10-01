using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Crash consistency for everything that moves data in bulk: the pool's own jobs (draining the
/// landing zone, healing a lost copy, promoting a surviving shadow) and the operations that move
/// many files or whole trees at once (folder rename, the recycle bin, snapshot restore, retiring a
/// disk).
///
/// These jobs run with nobody watching, on files the user may not have touched in years, and each
/// of them deletes something once it has copied something — which is exactly the shape that loses a
/// file if the order is wrong or a crash lands between the two. The operation matrix in
/// <see cref="CrashConsistencyTests"/> covers what APPLICATIONS do; nothing interrupted these at
/// every step. Each case lets the job run until the Nth storage operation, loses power there, remounts
/// so recovery runs, and requires the file to be whole on every copy that exists.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DataMovementCrashTests {

  private static readonly Guid _pool = Guid.Parse("b9c0f00d-0000-0000-0000-00000000b9c0");

  /// <summary>Thrown by the injected abort — power loss is not a graceful error path.</summary>
  private sealed class PowerLoss : Exception;

  private FakeVolumeIO _ssd = null!;
  private FakeVolumeIO _hdd1 = null!;
  private FakeVolumeIO _hdd2 = null!;
  private int _abortFired;

  private FakeVolumeIO[] _All => [this._ssd, this._hdd1, this._hdd2];

  [SetUp]
  public void SetUp() {
    this._ssd = new(Guid.NewGuid(), "ssd", "PHYS-SSD", capacity: 1L << 24);
    this._hdd1 = new(Guid.NewGuid(), "hdd1", "PHYS-HDD1", capacity: 1L << 24);
    this._hdd2 = new(Guid.NewGuid(), "hdd2", "PHYS-HDD2", capacity: 1L << 24);
  }

  private const string _BASE = """{ "duplication": 2, "write": { "policy": "write-through" }, "readAhead": { "enabled": false } }""";

  private const string _TRASH =
    """{ "duplication": 2, "write": { "policy": "write-through" }, "readAhead": { "enabled": false }, "trash": { "enabled": true, "retention": "7d", "maxSize": "50%" } }""";

  private PoolFileSystem _Engine(bool withLandingZone, string config = _BASE) {
    var cache = new CacheInstance("bm" + Guid.NewGuid().ToString("N"),
      new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [
      new(this._ssd, withLandingZone ? MemberRole.Landing : MemberRole.Capacity),
      new(this._hdd1),
      new(this._hdd2),
    ], cache, ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  /// <summary>Runs an operation with the power cut at its Nth storage operation.</summary>
  private void _Interrupted(int abortAfter, Action operation) {
    this._AbortAfter(abortAfter);
    try {
      operation();
    } catch (Exception) {
      // died part-way (a power loss inside a parallel section arrives wrapped)
    } finally {
      this._Disarm();
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
  }

  private static byte[] _Content() => [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 13 + 5))];

  private void _AbortAfter(int operations) {
    var remaining = operations;
    this._abortFired = 0;
    void Hook(VolumeOp op, string path) {
      if (Interlocked.Decrement(ref remaining) != 0)
        return;

      Interlocked.Exchange(ref this._abortFired, 1);
      throw new PowerLoss();
    }

    foreach (var volume in this._All)
      volume.BeforeOperation = Hook;
  }

  private void _Disarm() {
    foreach (var volume in this._All)
      volume.BeforeOperation = null;
  }

  private void _AssertTheCrashActuallyHappened(int step)
    => (Volatile.Read(ref this._abortFired) == 1).Should().BeTrue(
      $"step {step} is past the end of this job — the case tested nothing, so the range must be tightened");

  private PoolFileSystem _RecoverAfterPowerLoss(bool withLandingZone, string config = _BASE) {
    this._Disarm();
    foreach (var volume in this._All)
      volume.SimulateCrash();

    return this._Engine(withLandingZone, config);
  }

  private static byte[]? _Read(PoolFileSystem fs, string path) {
    NodeHandle handle;
    try {
      handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    } catch (PoolFsException) {
      return null;
    }

    try {
      var buffer = new byte[fs.GetAttributes(path).Length];
      var read = 0;
      while (read < buffer.Length) {
        var got = fs.Read(handle, buffer.AsSpan(read), read);
        if (got == 0)
          break;
        read += got;
      }

      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  /// <summary>The file reads back whole, and no copy of it anywhere holds anything else.</summary>
  private void _AssertWhole(PoolFileSystem recovered, string path, byte[] content, string when) {
    _Read(recovered, path).Should().Equal(content, $"{when}: the file must survive whole");

    var copies = 0;
    foreach (var volume in this._All)
    foreach (var shadow in new[] { false, true })
      if (volume.GetContent(path, shadow) is { } copy) {
        ++copies;
        copy.Should().Equal(content, $"{when}: the copy on '{volume.DisplayName}' (shadow: {shadow}) is not the file");
      }

    copies.Should().BeGreaterThan(0, $"{when}: no copy of the file exists at all");
    recovered.ReadDirectory("").Should().NotContain(e => e.Name.Contains("TEMP.$DRIVEBENDER", StringComparison.OrdinalIgnoreCase),
      $"{when}: a move's temp must never be left in the namespace");
  }

  private void _Store(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  [Test]
  [Category("EdgeCase")]
  // 21 steps: the whole drain, measured — the guard below fails the moment the job gets shorter
  public void Drain_GivenItIsInterruptedAtEveryStep_ThenTheFileSurvivesWholeOnEveryCopy([Range(1, 21)] int abortAfter) {
    var content = _Content();
    using (var fs = this._Engine(withLandingZone: true)) {
      this._Store(fs, "landed.bin", content);
      this._ssd.FileExists("landed.bin", false).Should().BeTrue("the file has to be on the landing zone for there to be a drain");
      fs.Unmount();
    }

    using (var fs = this._Engine(withLandingZone: true)) {
      this._AbortAfter(abortAfter);
      try {
        fs.DrainOneLandingFile();
      } catch (Exception) {
        // a power loss thrown inside a parallel section arrives wrapped; any of it means "died here"
        // the machine died mid-move — the point
      } finally {
        this._Disarm();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss(withLandingZone: true);
    this._AssertWhole(recovered, "landed.bin", content, $"drain interrupted at step {abortAfter}");
  }

  [Test]
  [Category("EdgeCase")]
  // 21 steps: the whole heal, measured
  public void Heal_GivenRebuildingALostCopyIsInterruptedAtEveryStep_ThenTheFileSurvivesWholeOnEveryCopy([Range(1, 21)] int abortAfter) {
    var content = _Content();
    using (var fs = this._Engine(withLandingZone: false)) {
      this._Store(fs, "healed.bin", content);
      fs.Unmount();
    }

    // one copy is lost: the SHADOW, so the primary survives and the heal copies from it
    foreach (var volume in this._All)
      if (volume.FileExists("healed.bin", true))
        volume.Delete("healed.bin", true);

    using (var fs = this._Engine(withLandingZone: false)) {
      fs.RequestHeal();
      this._AbortAfter(abortAfter);
      try {
        for (var i = 0; i < 50 && fs.HealStep(); ++i) {
        }
      } catch (Exception) {
        // a power loss thrown inside a parallel section arrives wrapped; any of it means "died here"
        // the machine died mid-heal
      } finally {
        this._Disarm();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss(withLandingZone: false);
    this._AssertWhole(recovered, "healed.bin", content, $"heal interrupted at step {abortAfter}");
  }

  [Test]
  [Category("EdgeCase")]
  // 33 steps: the promotion plus the copy that restores the second copy afterwards, measured
  public void Heal_GivenPromotingTheOnlySurvivingShadowIsInterruptedAtEveryStep_ThenTheFileSurvives([Range(1, 33)] int abortAfter) {
    // The dangerous one: promotion copies the shadow to a primary on the same member and then
    // DELETES the shadow. That shadow is the only copy of the file in existence.
    var content = _Content();
    using (var fs = this._Engine(withLandingZone: false)) {
      this._Store(fs, "promoted.bin", content);
      fs.Unmount();
    }

    foreach (var volume in this._All)
      if (volume.FileExists("promoted.bin", false))
        volume.Delete("promoted.bin", false);

    this._All.Count(v => v.FileExists("promoted.bin", true)).Should().Be(1, "exactly one shadow must be the sole survivor");

    using (var fs = this._Engine(withLandingZone: false)) {
      fs.RequestHeal();
      this._AbortAfter(abortAfter);
      try {
        for (var i = 0; i < 50 && fs.HealStep(); ++i) {
        }
      } catch (Exception) {
        // a power loss thrown inside a parallel section arrives wrapped; any of it means "died here"
        // the machine died mid-promotion
      } finally {
        this._Disarm();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss(withLandingZone: false);
    this._AssertWhole(recovered, "promoted.bin", content, $"promotion interrupted at step {abortAfter}");
  }

  [Test]
  [Category("EdgeCase")]
  public void RenameFolder_GivenItIsInterruptedAtEveryStep_ThenEveryFileLivesWholeUnderExactlyOneName([Range(1, 12)] int abortAfter) {
    // A folder rename flips a directory on every member that holds it — a tree, across disks.
    var a = _Content();
    var b = _Content().Select(x => (byte)~x).ToArray();
    using (var fs = this._Engine(withLandingZone: false)) {
      fs.MakeDir("dir");
      fs.MakeDir("dir/sub");
      this._Store(fs, "dir/a.bin", a);
      this._Store(fs, "dir/sub/b.bin", b);
      fs.Unmount();
    }

    using (var fs = this._Engine(withLandingZone: false))
      this._Interrupted(abortAfter, () => fs.Rename("dir", "moved", RenameFlags.None));

    using var recovered = this._RecoverAfterPowerLoss(withLandingZone: false);
    foreach (var (relative, content) in new[] { ("a.bin", a), ("sub/b.bin", b) }) {
      var old = _Read(recovered, "dir/" + relative);
      var moved = _Read(recovered, "moved/" + relative);
      (old != null && moved != null).Should().BeFalse($"step {abortAfter}: '{relative}' lives under BOTH names");
      (old ?? moved).Should().NotBeNull($"step {abortAfter}: '{relative}' lives under NEITHER name — lost");
      (old ?? moved).Should().Equal(content, $"step {abortAfter}: '{relative}' must be whole");
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void DeleteToTrash_GivenItIsInterruptedAtEveryStep_ThenTheFileIsInPlaceOrRestorableNeverGone([Range(1, 16)] int abortAfter) {
    var content = _Content();
    using (var fs = this._Engine(withLandingZone: false, _TRASH)) {
      this._Store(fs, "precious.bin", content);
      fs.Unmount();
    }

    using (var fs = this._Engine(withLandingZone: false, _TRASH))
      this._Interrupted(abortAfter, () => fs.Unlink("precious.bin"));

    using var recovered = this._RecoverAfterPowerLoss(withLandingZone: false, _TRASH);
    var inPlace = _Read(recovered, "precious.bin");
    if (inPlace == null) {
      recovered.Trash.List().Select(e => e.OriginalPath).Should().Contain("precious.bin",
        $"step {abortAfter}: a file deleted into the recycle bin is either still in place or IN the bin — it was in neither");
      recovered.RestoreFromTrash("precious.bin");
      inPlace = _Read(recovered, "precious.bin");
    }

    inPlace.Should().Equal(content, $"step {abortAfter}: whatever survived must be the whole file");
  }

  [Test]
  [Category("EdgeCase")]
  public void RestoreFromTrash_GivenItIsInterruptedAtEveryStep_ThenTheFileIsInTheBinOrBackNeverGone([Range(1, 22)] int abortAfter) {
    var content = _Content();
    using (var fs = this._Engine(withLandingZone: false, _TRASH)) {
      this._Store(fs, "undo.bin", content);
      fs.Unlink("undo.bin");
      fs.Unmount();
    }

    using (var fs = this._Engine(withLandingZone: false, _TRASH))
      this._Interrupted(abortAfter, () => fs.RestoreFromTrash("undo.bin"));

    using var recovered = this._RecoverAfterPowerLoss(withLandingZone: false, _TRASH);
    var back = _Read(recovered, "undo.bin");
    if (back == null) {
      recovered.Trash.List().Select(e => e.OriginalPath).Should().Contain("undo.bin",
        $"step {abortAfter}: an interrupted restore must leave the file in the bin or back in place — it was in neither");
      recovered.RestoreFromTrash("undo.bin");
      back = _Read(recovered, "undo.bin");
    }

    back.Should().Equal(content, $"step {abortAfter}: whatever survived must be the whole file");
  }

  [Test]
  [Category("EdgeCase")]
  public void RestoreFromSnapshot_GivenItIsInterruptedAtEveryStep_ThenTheFileIsOneWholeVersion([Range(1, 30)] int abortAfter) { // 30: the whole restore, measured (it writes a replacement beside the live file instead of truncating it)
    var then = _Content();
    var now = _Content().Select(x => (byte)~x).ToArray();
    Guid snapshot;
    using (var fs = this._Engine(withLandingZone: false)) {
      this._Store(fs, "ledger.bin", then);
      snapshot = fs.TakeSnapshot("before").Id;
      var handle = fs.Open("ledger.bin", AccessMode.ReadWrite, ShareMode.Read);
      fs.Write(handle, now, 0, WriteMode.Normal);
      fs.Close(handle);
      fs.Unmount();
    }

    using (var fs = this._Engine(withLandingZone: false))
      this._Interrupted(abortAfter, () => fs.RestoreFromSnapshot(snapshot, "ledger.bin"));

    using var recovered = this._RecoverAfterPowerLoss(withLandingZone: false);
    var found = _Read(recovered, "ledger.bin");
    found.Should().NotBeNull($"step {abortAfter}: restoring an old version must never lose the file");
    (found!.AsSpan().SequenceEqual(then) || found.AsSpan().SequenceEqual(now)).Should().BeTrue(
      $"step {abortAfter}: the file must be the current version or the snapshot's, whole — not a mixture");
  }

  [Test]
  [Category("EdgeCase")]
  public void RemoveMedia_GivenScatteringADiskIsInterruptedAtEveryStep_ThenEveryFileStaysReadableAndWhole([Range(1, 45)] int abortAfter) {
    // Retiring a disk moves every file it holds onto the others, then deletes its own copies.
    var files = Enumerable.Range(0, 4).ToDictionary(i => $"f{i}.bin", i => _Content().Select(x => (byte)(x ^ i)).ToArray());
    using (var fs = this._Engine(withLandingZone: false)) {
      foreach (var (path, content) in files)
        this._Store(fs, path, content);
      fs.Unmount();
    }

    using (var fs = this._Engine(withLandingZone: false)) {
      // the disk holding the most copies — retiring one that holds nothing tests nothing
      var busiest = this._All.OrderByDescending(v => v.FilePaths.Count(p => !p.StartsWith(".drivebenderutility"))).First();
      var media = new MediaLifecycle(this._All, fs.Journal, duplicationLevel: 2);
      this._Interrupted(abortAfter, () => media.ScatterAndRemove(busiest.MemberId));
    }

    using var recovered = this._RecoverAfterPowerLoss(withLandingZone: false);
    foreach (var (path, content) in files)
      _Read(recovered, path).Should().Equal(content, $"step {abortAfter}: '{path}' must survive retiring a disk being cut short");
  }


  [Test]
  [Category("EdgeCase")]
  public void RemoveMedia_GivenTheDiskHoldsASnapshotVersion_ThenTheSnapshotCanStillRestoreIt() {
    // A preserved version is two files: its bytes and the sidecar that names it, and the store only
    // sees a version whose sidecar sits beside it on the same member. Retiring the disk placed each
    // file of the store on whichever member had the most room at that moment — so the bytes and the
    // sidecar could land on different members, and the version, still on disk, was never found again.
    // Members sharing one physical disk report the same free space and tie, so it takes members on
    // different disks to see it.
    const string single = """{ "duplication": 1, "write": { "policy": "write-through" }, "readAhead": { "enabled": false } }""";
    var then = _Content();
    var now = new byte[] { 1, 2, 3 }; // smaller than the version, so the store's two files get different targets
    Guid snapshot;
    using (var fs = this._Engine(withLandingZone: false, single)) {
      this._Store(fs, "quarterly.bin", then);
      snapshot = fs.TakeSnapshot("before-the-swap").Id;
      var handle = fs.Open("quarterly.bin", AccessMode.ReadWrite, ShareMode.Read);
      fs.SetLength(handle, 0);
      fs.Write(handle, now, 0, WriteMode.Normal);
      fs.Close(handle);
      fs.Unmount();
    }

    var holder = this._All.Single(v => v.FilePaths.Any(p => p.EndsWith(".snapver", StringComparison.Ordinal)));
    var staying = this._All.Where(v => v != holder).ToArray();
    using (var fs = this._Engine(withLandingZone: false, single))
      new MediaLifecycle(this._All, fs.Journal, duplicationLevel: 1).ScatterAndRemove(holder.MemberId);

    var cache = new CacheInstance("rm" + Guid.NewGuid().ToString("N"),
      new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    using var remaining = new PoolFileSystem(_pool, [.. staying.Select(v => new EngineMember(v))], cache, ConfigResolver.ResolveEffective(null, single));
    remaining.Mount(new(@"X:\"));
    remaining.RestoreFromSnapshot(snapshot, "quarterly.bin");

    _Read(remaining, "quarterly.bin").Should().Equal(then,
      "the version left the retired disk with its sidecar, so the snapshot still finds what it promised");
  }

  [Test]
  [Category("HappyPath")]
  public void RemoveMedia_GivenDuplicationTwoAndAThirdDiskToSpare_ThenEveryFileStillHasTwoCopiesAfterwards() {
    // Retiring a disk deleted each of its copies as soon as ONE copy survived elsewhere, and made
    // no new ones — so with duplication 2 and a disk to spare, every file that had a copy on the
    // retired disk was left with one, until some later mount's healer got round to it. The moment
    // somebody is swapping hardware is the worst moment to be quietly running on half protection.
    var files = Enumerable.Range(0, 6).ToDictionary(i => $"g{i}.bin", i => _Content().Select(x => (byte)(x + i)).ToArray());
    using (var fs = this._Engine(withLandingZone: false)) {
      foreach (var (path, content) in files)
        this._Store(fs, path, content);
      fs.Unmount();
    }

    var busiest = this._All.OrderByDescending(v => v.FilePaths.Count(p => !p.StartsWith(".drivebenderutility"))).First();
    using (var fs = this._Engine(withLandingZone: false))
      new MediaLifecycle(this._All, fs.Journal, duplicationLevel: 2).ScatterAndRemove(busiest.MemberId);

    foreach (var (path, content) in files) {
      var holders = this._All.Where(v => v != busiest && (v.GetContent(path, false) ?? v.GetContent(path, true)) is { } c && c.AsSpan().SequenceEqual(content)).ToArray();
      holders.Should().HaveCount(2, $"'{path}' must keep its duplication level on the disks that stay");
    }
  }

}
