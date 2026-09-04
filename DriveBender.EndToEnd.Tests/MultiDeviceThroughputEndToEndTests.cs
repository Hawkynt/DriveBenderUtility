using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Whether two storages behind one tier actually COMBINE, measured on hardware that has two.
///
/// This is the one claim in the scatter work that could not be settled. `docs/Performance.md` prices
/// overlapped I/O against a queue depth of one and is explicit about the limit: its "2 copies" row
/// shares a single device, so it "prices the split's overhead rather than the gain", and it says the
/// split "can only pay for itself on hardware that actually has two". A machine with two independent
/// disks can answer it, and this asks the question in the only form that cannot be argued with — the
/// SAME read, against a mirror spread over two devices and against a mirror stacked on one.
///
/// Both caches are cold for every measurement: the pool's, cleared by a remount, and the host's,
/// evicted by hand. Without the second one this file would compare RAM against RAM.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("Heterogeneous")]
[NonParallelizable]
public class MultiDeviceThroughputEndToEndTests {

  /// <summary>Large enough that the transfer, not the setup, is what is being timed.</summary>
  private const int _TRANSFER = 384 * 1024 * 1024;

  /// <summary>Two devices, fastest first — or an ignored scenario, on a machine that has one.</summary>
  private static (StorageDevice first, StorageDevice second) _RequireTwoDevices() {
    var devices = StorageDevices.Fastest;
    if (devices.Count < 2)
      Assert.Ignore(
        $"This scenario needs TWO independent storage devices and this machine offers {devices.Count}. "
        + "Set DBE2E_DEVICES to writable directories on two of them, separated by the path separator.");

    var (first, second) = (devices[0], devices[1]);
    var room = _TRANSFER * 3L;
    if (StorageDevices.FreeBytesOf(first.Path) < room || StorageDevices.FreeBytesOf(second.Path) < room)
      Assert.Ignore($"{first} and {second} do not both have {room / (1024 * 1024)} MiB free.");

    return (first, second);
  }

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>
  /// Times a cold read of a file already in the pool, with both caches emptied first.
  ///
  /// The pool's cache goes with the remount. The HOST's page cache has to be evicted explicitly, and
  /// it is the one that matters most here: a freshly written 384 MiB file is entirely in RAM on a
  /// machine with any memory at all, and a read of it would come back at memory bandwidth from
  /// whichever member the engine chose, making every arrangement look identical.
  /// </summary>
  private static double _ColdReadRate(MountedPool pool, string name, byte[] expected) {
    pool.WhileUnmounted(() => {
      foreach (var storage in pool.StoragePaths)
        PageCache.DropTree(storage);
    });

    var buffer = new byte[1 << 20];
    var read = 0L;
    var stopwatch = Stopwatch.StartNew();
    using (var stream = new FileStream(pool.PathTo(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20)) {
      int count;
      while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        read += count;
    }

    stopwatch.Stop();
    read.Should().Be(expected.Length, $"'{name}' must read back at its full length");
    return stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : expected.Length / stopwatch.Elapsed.TotalSeconds;
  }

  [Test]
  [Category("Performance")]
  [Explicit("Benchmark: both arrangements land near the engine's own read ceiling, so the difference "
            + "between them is smaller than this host's run-to-run variation. Run it deliberately on "
            + "a quiet machine and read the printed numbers; it cannot police a ratio in the battery.")]
  [Description("Prices a mirror spread across two independent devices against the same mirror stacked on one.")]
  public void Scatter_GivenAMirrorAcrossTwoDevices_ThenItReadsFasterThanTheSameMirrorOnOne() {
    var (first, second) = _RequireTwoDevices();
    if (!PageCache.CanDrop)
      Assert.Ignore("Without evicting the host page cache this would compare RAM against RAM.");

    var content = _Payload(_TRANSFER, 810);

    // both copies on ONE device: the control, and what docs/Performance.md could already measure
    double stacked;
    using (var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
             storageDevices: [first.Path, first.Path])) {
      File.WriteAllBytes(pool.PathTo("stacked.bin"), content);
      MountedPool.WaitUntil(() => pool.PhysicalCopies("stacked.bin").Count >= 2, TimeSpan.FromMinutes(5))
        .Should().BeTrue($"the control needs both copies to exist.{Environment.NewLine}{pool.DescribeMembers()}");

      stacked = _ColdReadRate(pool, "stacked.bin", content);
    }

    // one copy on each device: two queues instead of one
    double spread;
    using (var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk,
             storageDevices: [first.Path, second.Path])) {
      File.WriteAllBytes(pool.PathTo("spread.bin"), content);
      MountedPool.WaitUntil(() => pool.PhysicalCopies("spread.bin").Count >= 2, TimeSpan.FromMinutes(5))
        .Should().BeTrue($"the subject needs both copies to exist.{Environment.NewLine}{pool.DescribeMembers()}");

      spread = _ColdReadRate(pool, "spread.bin", content);
    }

    var evidence = $"{_TRANSFER / (1024 * 1024)} MiB cold read: {spread / (1024 * 1024):F0} MiB/s across "
                   + $"{first} and {second}, against {stacked / (1024 * 1024):F0} MiB/s with both copies on "
                   + $"{first}.";

    TestContext.Out.WriteLine(evidence);

    // No ratio is asserted, and that is a finding rather than a shrug. Three runs on this host gave
    // spread-against-stacked of +7%, +9% and -24%; the sign of the difference tracks what else the
    // machine was doing, not which arrangement was used, because both land near the engine's own
    // cached-read ceiling and neither is device-bound. An assertion tuned until those three all pass
    // would be measuring nothing and would say so in green. What IS asserted is the part that holds
    // whatever the load: both arrangements return the file whole (checked inside _ColdReadRate) and
    // both produce a rate at all.
    spread.Should().BeGreaterThan(0, $"the spread mirror must be readable. {evidence}");
    stacked.Should().BeGreaterThan(0, $"the stacked control must be readable. {evidence}");
  }

  [Test]
  [Category("Performance")]
  [Description("A tier built from two independent devices spreads a write burst across both of them, which is the precondition for combining their throughput at all.")]
  public void Scatter_GivenATierOnTwoDevices_ThenTheBurstIsSpreadAcrossBoth() {
    var (first, second) = _RequireTwoDevices();
    var content = _Payload(_TRANSFER, 811);

    // unduplicated, two members: each file goes to ONE of them, so the tier's capacity to absorb is
    // the two devices working at once rather than either taking turns
    using var pool = MountedPool.Create(members: 2, storageDevices: [first.Path, second.Path]);

    const int files = 8;
    var chunk = _TRANSFER / files;
    var stopwatch = Stopwatch.StartNew();
    Parallel.For(0, files, file => {
      var payload = new byte[chunk];
      content.AsSpan(file * chunk, chunk).CopyTo(payload);
      File.WriteAllBytes(pool.PathTo($"tier{file}.bin"), payload);
    });

    stopwatch.Stop();
    var rate = _TRANSFER / stopwatch.Elapsed.TotalSeconds;

    // Where the files LANDED, which is the claim underneath the rate and the only part of it that a
    // loaded machine cannot distort. Combining two devices' throughput presupposes that both of them
    // are given work; if a tier sends every file to one disk, no amount of overlapped I/O inside that
    // disk will reach the pair's combined rate, and the timing above would only ever be measuring
    // one of them.
    var perMember = Enumerable.Range(0, pool.MemberPaths.Count)
      .Select(member => Enumerable.Range(0, files)
        .Count(file => File.Exists(Path.Combine(pool.MemberPaths[member], $"tier{file}.bin"))))
      .ToArray();

    var evidence = $"{_TRANSFER / (1024 * 1024)} MiB across {files} files in {stopwatch.Elapsed.TotalSeconds:F2}s "
                   + $"({rate / (1024 * 1024):F0} MiB/s) over {first} and {second}; "
                   + $"files per member: {string.Join(" / ", perMember)}."
                   + $"{Environment.NewLine}{pool.MountLog}";

    TestContext.Out.WriteLine(evidence);

    perMember.Should().OnlyContain(count => count > 0,
      $"a tier of two devices has to give BOTH of them work — a burst that lands entirely on one "
      + $"disk cannot combine two disks' throughput however well it overlaps. {evidence}");

    // and none of that is worth anything if a byte moved
    for (var file = 0; file < files; ++file)
      File.ReadAllBytes(pool.PathTo($"tier{file}.bin")).Should()
        .Equal(content[(file * chunk)..((file + 1) * chunk)], $"'tier{file}.bin' must be exactly what was written");
  }

}
