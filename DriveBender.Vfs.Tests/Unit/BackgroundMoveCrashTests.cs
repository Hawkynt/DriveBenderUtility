using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Crash consistency for the pool's OWN data movement: draining the landing zone, healing a lost
/// copy, and promoting a surviving shadow back to a primary.
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
public class BackgroundMoveCrashTests {

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

  private PoolFileSystem _Engine(bool withLandingZone) {
    var cache = new CacheInstance("bm" + Guid.NewGuid().ToString("N"),
      new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [
      new(this._ssd, withLandingZone ? MemberRole.Landing : MemberRole.Capacity),
      new(this._hdd1),
      new(this._hdd2),
    ], cache, ConfigResolver.ResolveEffective(null,
      """{ "duplication": 2, "write": { "policy": "write-through" }, "readAhead": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));
    return fs;
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

  private PoolFileSystem _RecoverAfterPowerLoss(bool withLandingZone) {
    this._Disarm();
    foreach (var volume in this._All)
      volume.SimulateCrash();

    return this._Engine(withLandingZone);
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

}
