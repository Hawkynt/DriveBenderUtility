using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// New files are spread over the members that can take them. Placement weighs each member's
/// measured latency, and a measurement only moves when its member is used — so a member must never
/// be kept out of rotation by a reading nobody gives it the chance to correct, or the pool latches
/// onto one disk and delivers one disk's throughput however many it has.
/// </summary>
[TestFixture]
[Category("Unit")]
public class PlacementBalanceTests {

  private static readonly Guid _pool = Guid.Parse("57a6ed00-0000-0000-0000-000000000077");

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  /// <summary>A clock the test moves by hand, so "a while ago" is exact rather than slept for.</summary>
  private sealed class ManualTime : TimeProvider {
    private long _ticks = TimeSpan.TicksPerDay;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref this._ticks);
    public void Advance(TimeSpan by) => Interlocked.Add(ref this._ticks, by.Ticks);
  }

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 16L << 20);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 16L << 20);
  }

  private static PoolFileSystem _Engine(params IVolumeIO[] members) {
    var cache = new CacheInstance("pb" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [.. members.Select(m => new EngineMember(m))], cache, ConfigResolver.ResolveEffective(null, """{ "duplication": 1 }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _WriteFile(PoolFileSystem fs, string path, int size = 16 * 1024) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    for (var offset = 0; offset < size; offset += 4096)
      fs.Write(handle, new byte[4096], offset, WriteMode.Normal);

    fs.Close(handle);
  }

  private static int _FilesOn(FakeVolumeIO member, string prefix, int count)
    => Enumerable.Range(0, count).Count(i => member.FileExists($"{prefix}{i}.bin", false));

  [Test]
  [Category("EdgeCase")]
  public void Placement_GivenIdenticalMembersThatDifferOnlyBySubMillisecondNoise_WhenFilesAreWrittenOneAfterAnother_ThenBothMembersAreUsed() {
    // Fractions of a millisecond are noise: whichever member happened to see a cold first call reads
    // a quarter of a millisecond slower than the other. Ranked strictly, that decided every file —
    // 20 / 0 — and free space, which is meant to spread an idle pool, never got a turn.
    var one = new MeasuredVolumeIO(this._v1);
    var two = new MeasuredVolumeIO(this._v2);
    one.RecordLatency(0.25);
    two.RecordLatency(0.003);

    var fs = _Engine(one, two);
    const int files = 20;
    for (var i = 0; i < files; ++i)
      _WriteFile(fs, $"f{i}.bin");

    var split = new[] { _FilesOn(this._v1, "f", files), _FilesOn(this._v2, "f", files) };
    split.Sum().Should().Be(files);
    split.Should().OnlyContain(count => count >= files / 4,
      $"two identical disks must both take new files; the split was {split[0]} / {split[1]}");
  }

  [Test]
  [Category("EdgeCase")]
  public void Placement_GivenFilesStillBeingWrittenStripedOverBothMembers_WhenMoreArrive_ThenTheyAreKeptOnBoth() {
    // Striping spreads each file's blocks over both disks, so the load in flight is level and the
    // free space identical. The disk that keeps a file still owes it every block, and that is what
    // tells the disks apart: without it, a burst was kept on one disk alone (0 / 21 on CI).
    var fs = _Engine(this._v1, this._v2);
    const int files = 4;
    var handles = Enumerable.Range(0, files).Select(i => fs.Create($"burst{i}.bin", NodeKind.File, CreateFlags.None)).ToList();
    foreach (var handle in handles)
      fs.Write(handle, new byte[4096], 0, WriteMode.Normal);
    foreach (var handle in handles)
      fs.Close(handle);

    var split = new[] { _FilesOn(this._v1, "burst", files), _FilesOn(this._v2, "burst", files) };
    split.Should().Equal([files / 2, files / 2], $"files written at the same time are kept on both disks; the split was {split[0]} / {split[1]}");
  }

  [TestCase(500, false, TestName ="Placement_GivenAMemberLastMeasuredSlowHalfASecondAgo_WhenTheNextFileIsPlaced_ThenItIsStillAvoided")]
  [TestCase(1500, true, TestName = "Placement_GivenAMemberLastMeasuredSlowOneAndAHalfSecondsAgo_WhenTheNextFileIsPlaced_ThenItIsTriedAgain")]
  [Category("EdgeCase")]
  public void Placement_GivenAMemberWhoseOnlyMeasurementIsOldAndSlow_ThenItIsTriedAgainOnceThatMeasurementIsStale(int millisecondsAgo, bool expectTriedAgain) {
    // A spun-down disk takes half a second to wake for its first access. That one reading used to
    // keep it out of rotation for good: never chosen, so never measured again, so never chosen.
    var time = new ManualTime();
    var waking = new MeasuredVolumeIO(this._v1, time);
    var busy = new MeasuredVolumeIO(this._v2, time);
    var fs = _Engine(waking, busy);
    waking.RecordLatency(500);

    _WriteFile(fs, "before.bin");
    this._v2.FileExists("before.bin", false).Should().BeTrue("a member measured slow a moment ago is avoided");

    time.Advance(TimeSpan.FromMilliseconds(millisecondsAgo));
    busy.RecordLatency(0.5); // the chosen member keeps working, so its own reading stays current

    _WriteFile(fs, "after.bin");
    this._v1.FileExists("after.bin", false).Should().Be(expectTriedAgain,
      $"a reading {millisecondsAgo} ms old is {(expectTriedAgain ? "past" : "within")} the {PoolFileSystem.LatencyStaleAfter.TotalSeconds}s it counts for");
  }

  /// <summary>
  /// Spends exactly <paramref name="milliseconds"/>. Thread.Sleep(3) sleeps about 15 ms on Windows and
  /// about 3 ms on Linux, so the same test measured two different disks — and only the Linux one sat
  /// close enough to the 1 ms floor to expose a disk being taught it was fast.
  /// </summary>
  private static void _Busy(int milliseconds) {
    var until = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * milliseconds / 1000;
    while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
  }

  [Test]
  [Category("HappyPath")]
  public void Placement_GivenOneMemberGenuinelySlow_WhenFilesAreWritten_ThenTheFasterMemberTakesMost() {
    // the other side of the boundary: a member that really is slow — every operation, well above the
    // floor — must still lose to the fast one; ignoring noise must not mean ignoring measurements
    // slow at everything that moves data or opens a file — through Delay, which every stream
    // operation passes; BeforeOperation never sees a stream's writes, so a disk slowed through it
    // was only ever slow at opening
    this._v2.Delay = op => {
      if (op is VolumeOp.Write or VolumeOp.Flush or VolumeOp.OpenWrite)
        _Busy(3);
    };

    var fs = _Engine(new MeasuredVolumeIO(this._v1), new MeasuredVolumeIO(this._v2));
    const int files = 20;
    for (var i = 0; i < files; ++i)
      _WriteFile(fs, $"g{i}.bin");

    _FilesOn(this._v1, "g", files).Should().BeGreaterThan(files * 3 / 4, "the member that is slow at everything is the one to avoid");
  }

}
