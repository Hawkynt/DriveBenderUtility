using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// A pool spread over storage of GENUINELY different speed — a fast internal disk and a slow
/// removable one — driven through a real mount.
///
/// Everything else in this suite runs both members off the machine's one disk, which is right for
/// correctness and mute about tiering: <c>docs/Performance.md</c> says outright that the landing
/// zone rows there "price the CODE PATH, not an SSD-versus-HDD difference". The point of a landing
/// zone is that the slow disk is not in the way of the write, and the point of a mirror across
/// unequal disks is that the slow copy is not in the way of the read. Neither claim can be tested,
/// or falsified, without two devices that actually differ.
///
/// Every scenario here IGNORES itself when the machine has no second device, so this file is inert
/// on a CI runner and meaningful on a desk.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("Heterogeneous")]
[NonParallelizable]
public class HeterogeneousDeviceEndToEndTests {

  /// <summary>Big enough to outrun any buffer, small enough to fit a removable disk and its drain.</summary>
  private const int _WORKING_SET = 24 * 1024 * 1024;

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>A fast landing zone in front of slow capacity storage — the deployment tiering is for.</summary>
  private static MountedPool _TieredAcrossDevices(string slow)
    => MountedPool.Create(members: 2, landingZones: 1, storageDevices: [null, slow]);

  #region tiering across real devices

  [Test]
  [Category("Performance")]
  [Description("With a genuinely slow capacity disk behind a fast landing zone, a write burst still runs at the fast tier's pace rather than the slow disk's.")]
  public void Tiering_GivenTheCapacityDiskIsGenuinelySlow_ThenAWriteBurstRunsAtTheFastTiersPace() {
    var slow = SlowDevice.RequireSlow();
    var deviceRate = SlowDevice.WriteBytesPerSecond;

    using var pool = _TieredAcrossDevices(slow);
    var content = _Payload(_WORKING_SET, 701);

    var stopwatch = Stopwatch.StartNew();
    File.WriteAllBytes(pool.PathTo("burst.bin"), content);
    stopwatch.Stop();

    var poolRate = _WORKING_SET / stopwatch.Elapsed.TotalSeconds;
    var evidence = $"{SlowDevice.Describe()}; the pool took {stopwatch.Elapsed.TotalSeconds:F2}s for "
                   + $"{_WORKING_SET / (1024 * 1024)} MiB ({poolRate / (1024 * 1024):F1} MiB/s) against the device's "
                   + $"{deviceRate / (1024 * 1024):F1} MiB/s.{Environment.NewLine}{pool.MountLog}";

    // The whole promise of a landing zone: the slow disk is behind the write, not in it. Twice the
    // device's own rate is a deliberately generous bar — a pool that wrote THROUGH to the slow disk
    // could not reach it, and one that merely matched the slow disk would fail it.
    poolRate.Should().BeGreaterThan(deviceRate * 2,
      $"a landing zone exists so that a burst is absorbed at the FAST tier's pace. {evidence}");

    File.ReadAllBytes(pool.PathTo("burst.bin")).Should().Equal(content,
      "absorbing a burst quickly is worthless if the bytes are not the user's bytes");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("Everything the fast tier absorbed arrives byte-for-byte on the slow capacity disk when the drainer moves it down.")]
  public void Tiering_WhenTheBurstDrainsDownToTheSlowDisk_ThenEveryByteArrivesIntact() {
    var slow = SlowDevice.RequireSlow();
    using var pool = _TieredAcrossDevices(slow);
    var content = _Payload(_WORKING_SET, 702);

    File.WriteAllBytes(pool.PathTo("drained.bin"), content);

    // the drain is a whole-file copy onto a slow device, so it is given real time
    var drained = MountedPool.WaitUntil(
      () => File.Exists(Path.Combine(pool.MemberPaths[1], "drained.bin"))
            || File.Exists(Path.Combine(pool.MemberPaths[1], "FOLDER.DUPLICATE.$DRIVEBENDER", "drained.bin")),
      TimeSpan.FromMinutes(4));

    pool.IsMountAlive.Should().BeTrue($"nothing drains if the mount has died.{Environment.NewLine}{pool.MountLog}");
    drained.Should().BeTrue(
      $"a settled file must reach capacity storage even when that storage is slow. {SlowDevice.Describe()}"
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    // the bytes are compared where they LANDED, not through the mount: a cache could serve the
    // right answer while the copy on the slow disk was short or torn
    var onSlowDisk = pool.PhysicalCopies("drained.bin")
      .Where(c => c.where.StartsWith(pool.MemberPaths[1], StringComparison.Ordinal))
      .ToArray();

    onSlowDisk.Should().NotBeEmpty("the copy that just drained has to be readable where it drained to");
    foreach (var (where, bytes) in onSlowDisk)
      bytes.Should().Equal(content, $"the drained copy at {where} must be byte-identical to what was written");

    File.ReadAllBytes(pool.PathTo("drained.bin")).Should().Equal(content, "and the mount must still serve it");
  }

  #endregion

  #region a mirror whose two halves are not equals

  /// <summary>
  /// Puts the PRIMARY copy on the slow disk deterministically, by writing the file while the fast
  /// member is not there to take it, then letting the returning member heal a second copy.
  ///
  /// Placement would otherwise hand the primary to whichever member has the most free space — the
  /// big internal disk, every time — and the scenario would then measure the engine reading from
  /// the fast copy for reasons that have nothing to do with it being fast.
  /// </summary>
  private static MountedPool _MirrorWithThePrimaryOnTheSlowDisk(string slow, string name, byte[] content) {
    var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
      storageDevices: [null, slow]);

    try {
      pool.Eject(0); // the fast disk is not available to take the primary
      File.WriteAllBytes(pool.PathTo(name), content);
      pool.Restore(0);

      var healed = MountedPool.WaitUntil(() => pool.PhysicalCopies(name).Count >= 2, TimeSpan.FromMinutes(4));
      healed.Should().BeTrue(
        $"the returning fast member must take its copy, otherwise there is no mirror to measure. "
        + $"{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      return pool;
    } catch {
      pool.Dispose();
      throw;
    }
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file mirrored across a fast and a slow disk is byte-identical on both, whichever of them took it first.")]
  public void Duplication_GivenOneCopyOnEachDevice_ThenBothCopiesAreWhole() {
    var slow = SlowDevice.RequireSlow();
    var content = _Payload(8 * 1024 * 1024, 703);
    using var pool = _MirrorWithThePrimaryOnTheSlowDisk(slow, "mirrored.bin", content);

    var copies = pool.PhysicalCopies("mirrored.bin");
    copies.Should().HaveCountGreaterThanOrEqualTo(2, $"the mirror must exist.{Environment.NewLine}{pool.DescribeMembers()}");
    foreach (var (where, bytes) in copies)
      bytes.Should().Equal(content, $"the copy at {where} must be whole — a mirror across unequal disks is still a mirror");
  }

  [Test]
  [Category("Performance")]
  [Description("With one copy on a fast disk and one on a slow one, reading the file is not held to the slow disk's pace.")]
  public void Duplication_GivenOneCopyOnEachDevice_ThenReadsAreNotHeldToTheSlowDisksPace() {
    var slow = SlowDevice.RequireSlow();
    if (!PageCache.CanDrop)
      Assert.Ignore("Without evicting the host page cache this would measure RAM, not the disks.");

    var deviceRate = SlowDevice.ReadBytesPerSecond;
    if (deviceRate <= 0)
      Assert.Ignore("Could not measure the slow device's READ rate, so there is nothing to compare against.");

    // printed unconditionally: every rate assertion in this file is relative to these two numbers,
    // and a run whose evidence is only visible on failure cannot be checked for having measured RAM
    TestContext.Out.WriteLine(SlowDevice.Describe());

    var content = _Payload(_WORKING_SET, 704);
    using var pool = _MirrorWithThePrimaryOnTheSlowDisk(slow, "unequal.bin", content);

    // the pool's own cache is dropped by the remount; the HOST's cache has to be evicted by hand,
    // or the slow disk is never touched and the number below is a memory bandwidth measurement
    var dropped = 0;
    pool.WhileUnmounted(() => {
      foreach (var storage in pool.StoragePaths)
        dropped += PageCache.DropTree(storage);
    });

    dropped.Should().BeGreaterThan(0, "with nothing evicted, this scenario cannot tell the disks apart");

    var stopwatch = Stopwatch.StartNew();
    var read = File.ReadAllBytes(pool.PathTo("unequal.bin"));
    stopwatch.Stop();

    read.Should().Equal(content, "correctness first: whichever copy was used, it must be the right bytes");

    var poolRate = _WORKING_SET / stopwatch.Elapsed.TotalSeconds;
    poolRate.Should().BeGreaterThan(deviceRate * 2,
      "a healthy fast copy sits beside the slow one, and a read that ignores it makes duplication a "
      + "performance LIABILITY rather than protection. "
      + $"Read {_WORKING_SET / (1024 * 1024)} MiB in {stopwatch.Elapsed.TotalSeconds:F2}s "
      + $"({poolRate / (1024 * 1024):F1} MiB/s); {SlowDevice.Describe()}.{Environment.NewLine}{pool.MountLog}");
  }

  #endregion

  #region device health, read from the drives themselves

  [Test]
  [Category("EdgeCase")]
  [Description("A member on a real block device reports that device's SMART health into the live snapshot the dashboard reads.")]
  public void Health_GivenAMemberOnARealDevice_ThenItsSmartStateReachesTheSnapshot() {
    // The whole chain in one go: the engine resolves the member to its physical device, the sampler
    // asks smartctl about it, the parser classifies the answer, and the mount publishes it where the
    // management daemon and the dashboard read it. Every part of that has its own test against
    // captured JSON; this is the only one that puts a real drive at the end of it.
    //
    // It self-ignores wherever SMART cannot actually be read, which is most machines: smartctl needs
    // privileges to open a raw device, so a daemon a user starts normally gets nothing. That is a
    // real deployment limit rather than a test problem, and reporting it honestly is the point of the
    // Unknown state — but a scenario that PASSED while learning nothing would hide it.
    var devices = StorageDevices.Fastest;
    if (devices.Count == 0)
      Assert.Ignore("This machine offers no second storage device to put a member on.");

    using var pool = MountedPool.Create(members: 1, storageDevices: [devices[0].Path]);

    // the sampler runs on its own schedule; the first sweep lands within a tick or two of the mount
    System.Text.Json.JsonElement health = default;
    var found = MountedPool.WaitUntil(() => {
      if (DbMount.TryReadMetrics(pool.PoolName) is not { } snapshot
          || !snapshot.TryGetProperty("MemberHealth", out var rows)
          || rows.GetArrayLength() == 0)
        return false;

      health = rows[0];
      return true;
    }, TimeSpan.FromMinutes(2));

    found.Should().BeTrue(
      $"the mount must publish a health row per member, even when the answer is that it could not "
      + $"be read.{Environment.NewLine}{pool.MountLog}");

    var state = health.GetProperty("Health").GetString();
    var detail = health.TryGetProperty("Detail", out var d) ? d.GetString() : null;
    TestContext.Out.WriteLine($"[health] {devices[0].Path} → {state} ({detail})");

    state.Should().BeOneOf("Unknown", "Healthy", "Aging", "Warning", "Failing");

    if (state == "Unknown") {
      detail.Should().NotBeNullOrEmpty(
        "an unknown reading must say WHY — 'smartctl is missing' and 'smartctl was not allowed to "
        + "open the device' need different actions from whoever reads it");
      Assert.Ignore($"SMART is not readable on this machine ({detail}), so there is no real drive "
                    + "reading to check the rest against.");
    }

    // a real reading arrived: it has to be self-consistent, or the dashboard is painting noise
    state.Should().Be("Healthy",
      $"the drives in this machine are not failing; a health colour that is wrong about working "
      + $"hardware is worse than none. Detail: {detail}");

    health.GetProperty("Model").GetString().Should().NotBeNullOrEmpty(
      "a member is named to the operator by its model, which needs smartctl's -i output");
  }

  #endregion

  #region the slow disk misbehaving

  [Test]
  [Category("Exception")]
  [Description("Pulling the slow disk out from under a live write does not stall the pool: the write finishes at the fast disk's pace.")]
  public void SlowMember_WhenItIsPulledMidWrite_ThenTheWriteFinishesWithoutStalling() {
    var slow = SlowDevice.RequireSlow();
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
      storageDevices: [null, slow]);

    const int chunks = 24;
    const int chunkSize = 256 * 1024;
    var written = new List<byte[]>();
    var slowestChunk = TimeSpan.Zero;

    using (var stream = new FileStream(pool.PathTo("pulled.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) {
      for (var chunk = 0; chunk < chunks; ++chunk) {
        if (chunk == chunks / 2)
          pool.Eject(1); // the slow disk leaves, mid-stream

        var payload = _Payload(chunkSize, 800 + chunk);
        var stopwatch = Stopwatch.StartNew();
        stream.Write(payload, 0, payload.Length);
        stopwatch.Stop();

        if (stopwatch.Elapsed > slowestChunk)
          slowestChunk = stopwatch.Elapsed;

        written.Add(payload);
      }

      stream.Flush();
    }

    // A member that has gone is discovered by an operation FAILING against it, and the cost of that
    // discovery is bounded by the engine's fault cooldown, not by the device. Ten seconds for a
    // 256 KiB chunk would mean the pool sat waiting on a disk that is not there.
    slowestChunk.Should().BeLessThan(TimeSpan.FromSeconds(10),
      $"losing a member must cost a bounded stall, not an unbounded one. {SlowDevice.Describe()}"
      + $"{Environment.NewLine}{pool.MountLog}");

    var expected = written.SelectMany(c => c).ToArray();
    File.ReadAllBytes(pool.PathTo("pulled.bin")).Should().Equal(expected,
      $"every acknowledged byte survives the slow disk leaving.{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("Exception")]
  [Description("Filling the only disk in a pool right up is refused cleanly, and everything already stored stays readable and whole.")]
  public void SlowMember_WhenItRunsCompletelyOutOfSpace_ThenTheRefusalIsCleanAndStoredDataIsIntact() {
    var slow = SlowDevice.RequireAvailable();
    var free = SlowDevice.FreeBytes;
    if (free <= 0 || free > 2L * 1024 * 1024 * 1024)
      Assert.Ignore($"'{slow}' has {free / (1024 * 1024)} MiB free — filling it up is not a reasonable thing to do.");

    // one member, on the small disk: there is nowhere else for the data to go, so the pool has to
    // meet the wall rather than route around it
    using var pool = MountedPool.Create(members: 1, storageDevices: [slow]);

    const int chunkSize = 2 * 1024 * 1024;
    var stored = new Dictionary<string, byte[]>();
    Exception? refusal = null;

    // deliberately more than the disk holds
    var attempts = (int)(free / chunkSize) + 8;
    for (var index = 0; index < attempts && refusal == null; ++index) {
      var name = $"fill{index:D3}.bin";
      var payload = _Payload(chunkSize, 900 + index);
      try {
        File.WriteAllBytes(pool.PathTo(name), payload);
        stored[name] = payload;
      } catch (IOException e) {
        refusal = e;
      } catch (UnauthorizedAccessException e) {
        refusal = e;
      }
    }

    refusal.Should().NotBeNull(
      $"a full disk has to be REFUSED, not absorbed — {attempts} writes of {chunkSize / (1024 * 1024)} MiB "
      + $"went into {free / (1024 * 1024)} MiB of space without one of them failing."
      + $"{Environment.NewLine}{pool.MountLog}");

    pool.IsMountAlive.Should().BeTrue(
      $"running out of space is an ordinary condition; it must not take the mount down."
      + $"{Environment.NewLine}{pool.MountLog}");

    // the part that matters: a disk filling up must not damage what was already on it
    foreach (var (name, expected) in stored)
      File.ReadAllBytes(pool.PathTo(name)).Should().Equal(expected,
        $"'{name}' was acknowledged before the disk filled up and must still be exactly what was written");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A removable disk that comes and goes repeatedly leaves the pool with every file whole and still responsive.")]
  public void SlowMember_GivenItComesAndGoesRepeatedly_ThenNothingIsLostAndThePoolStaysResponsive() {
    var slow = SlowDevice.RequireAvailable();
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
      storageDevices: [null, slow]);

    const int files = 4;
    var expected = new Dictionary<string, byte[]>();
    for (var file = 0; file < files; ++file) {
      var content = _Payload(512 * 1024, 950 + file);
      expected[$"flap{file}.bin"] = content;
      File.WriteAllBytes(pool.PathTo($"flap{file}.bin"), content);
    }

    MountedPool.WaitUntil(() => expected.Keys.All(name => pool.PhysicalCopies(name).Count >= 2), TimeSpan.FromMinutes(2));

    // a loose card reader, four times over — each cycle catching the pool in a different state
    var slowestRead = TimeSpan.Zero;
    for (var cycle = 0; cycle < 4; ++cycle) {
      pool.Eject(1);
      foreach (var (name, content) in expected) {
        var stopwatch = Stopwatch.StartNew();
        var read = File.ReadAllBytes(pool.PathTo(name));
        stopwatch.Stop();
        if (stopwatch.Elapsed > slowestRead)
          slowestRead = stopwatch.Elapsed;

        read.Should().Equal(content, $"'{name}' must be whole with the disk out, on cycle {cycle}");
      }

      pool.Restore(1);
      foreach (var (name, content) in expected)
        File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content,
          $"'{name}' must be whole with the disk back, on cycle {cycle}");
    }

    slowestRead.Should().BeLessThan(TimeSpan.FromSeconds(15),
      $"a flapping member must not make ordinary reads unbounded.{Environment.NewLine}{pool.MountLog}");

    // and after all that churn both disks converge on the same content again
    foreach (var (name, content) in expected) {
      MountedPool.WaitUntil(() => pool.PhysicalCopies(name).Count >= 2, TimeSpan.FromMinutes(3));
      foreach (var (where, bytes) in pool.PhysicalCopies(name))
        bytes.Should().Equal(content, $"the copy at {where} must have converged after the flapping stopped");
    }
  }

  #endregion

}
