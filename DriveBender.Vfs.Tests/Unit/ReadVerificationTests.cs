using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// <c>integrity.verifyReads</c>: whether a read is checked against the stored checksum, and when.
///
/// <list type="bullet">
/// <item><b>never</b> — reads are trusted; damage is left to the scrub schedule.</item>
/// <item><b>before</b> — a copy is checked before any of its bytes are handed over; a damaged copy
/// is passed over for an intact one, and the read fails only when no copy matches.</item>
/// <item><b>after</b> — the read is served at full speed and checked in the background; damage is
/// delivered, then warned about in the log and repaired.</item>
/// </list>
///
/// Bit-rot here is what <see cref="FakeVolumeIO.CorruptSilently"/> produces: the bytes change and
/// the size and modification time do not.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ReadVerificationTests {

  private static readonly byte[] _ORIGINAL = [.. Enumerable.Range(0, 200).Select(i => (byte)(i * 7 + 3))];

  private FakeVolumeIO _volume1 = null!;
  private FakeVolumeIO _volume2 = null!;
  private PoolFileSystem _fs = null!;
  private readonly List<string> _log = [];
  private Action<string> _previousLogger = null!;

  private void _Mount(string verifyReads) {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    var cache = new CacheInstance("r" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    this._fs = new(Guid.NewGuid(), [new(this._volume1), new(this._volume2)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 2, "integrity": { "verifyReads": "{{verifyReads}}" } }"""));
    this._fs.Mount(new(@"X:\"));
  }

  [SetUp]
  public void CaptureTheLog() {
    lock (this._log)
      this._log.Clear();

    this._previousLogger = DriveBender.Logger;
    DriveBender.Logger = message => {
      lock (this._log)
        this._log.Add(message);
    };
  }

  [TearDown]
  public void RestoreTheLog() {
    // background checks and repairs must finish inside the test that started them, or they log
    // into the next one
    this._fs?.WaitForReadVerifications(TimeSpan.FromSeconds(10));
    DriveBender.Logger = this._previousLogger;
    this._fs?.Dispose();
  }

  private string _Warnings() {
    lock (this._log)
      return string.Join(Environment.NewLine, this._log.Where(l => l.Contains("[Warning]")));
  }

  /// <summary>Writes a file through the pool, then baselines it the way a scrub does.</summary>
  private void _Store(string path) {
    var handle = this._fs.Create(path, NodeKind.File, CreateFlags.None);
    this._fs.Write(handle, _ORIGINAL, 0, WriteMode.Normal);
    this._fs.Close(handle);
    this._fs.RunScrub();
  }

  /// <summary>The copy a read is served from first — the one worth damaging.</summary>
  private (FakeVolumeIO holder, bool shadow) _ServingCopy(string path) {
    var copy = this._fs.Placement.ResolveCopies(path)[0];
    return ((FakeVolumeIO)copy.Volume, copy.Shadow);
  }

  private (FakeVolumeIO holder, bool shadow) _OtherCopy(string path) {
    var copy = this._fs.Placement.ResolveCopies(path)[1];
    return ((FakeVolumeIO)copy.Volume, copy.Shadow);
  }

  private byte[] _ReadAll(string path) {
    var handle = this._fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var buffer = new byte[_ORIGINAL.Length];
      var got = this._fs.Read(handle, buffer, 0);
      return buffer[..got];
    } finally {
      this._fs.Close(handle);
    }
  }

  private static void _Rot(FakeVolumeIO holder, bool shadow, string path)
    => holder.CorruptSilently(path, shadow, content => content[17] ^= 0xFF);

  #region never

  [Test]
  [Category("HappyPath")]
  public void Never_GivenADamagedServingCopy_WhenRead_ThenItIsServedUncheckedAsBefore() {
    // the default, and exactly the old behaviour: fast, trusting, damage left to the scrub
    this._Mount("never");
    this._Store("f.bin");
    var (holder, shadow) = this._ServingCopy("f.bin");
    _Rot(holder, shadow, "f.bin");

    var read = this._ReadAll("f.bin");

    read.Should().NotEqual(_ORIGINAL, "an unchecked read serves whatever the copy holds");
    this._fs.WaitForReadVerifications(TimeSpan.FromSeconds(5));
    this._Warnings().Should().NotContain("checksum", "nothing checked it, so nothing can have warned");
  }

  #endregion

  #region before

  [Test]
  [Category("HappyPath")]
  public void Before_GivenAnIntactFile_WhenRead_ThenItIsServed() {
    this._Mount("before");
    this._Store("f.bin");

    this._ReadAll("f.bin").Should().Equal(_ORIGINAL);
    this._Warnings().Should().BeEmpty();
  }

  [Test]
  [Category("HappyPath")]
  public void Before_GivenTheServingCopyIsDamaged_WhenRead_ThenTheIntactCopyIsServedAndTheDamagedOneRepaired() {
    this._Mount("before");
    this._Store("f.bin");
    var (holder, shadow) = this._ServingCopy("f.bin");
    _Rot(holder, shadow, "f.bin");

    this._ReadAll("f.bin").Should().Equal(_ORIGINAL, "a damaged copy must be passed over, not handed out, while an intact one exists");

    this._fs.WaitForReadVerifications(TimeSpan.FromSeconds(10)).Should().BeTrue();
    this._Warnings().Should().Contain("f.bin", "finding damage is worth saying even when the read itself was saved");
    holder.GetContent("f.bin", shadow).Should().Equal(_ORIGINAL, "and the damaged copy is repaired from the intact one");
  }

  [Test]
  [Category("Exception")]
  public void Before_GivenEveryCopyIsDamaged_WhenRead_ThenTheReadFailsRatherThanHandingOverDamage() {
    this._Mount("before");
    this._Store("f.bin");
    var (first, firstShadow) = this._ServingCopy("f.bin");
    var (second, secondShadow) = this._OtherCopy("f.bin");
    _Rot(first, firstShadow, "f.bin");
    _Rot(second, secondShadow, "f.bin");

    var act = () => this._ReadAll("f.bin");

    act.Should().Throw<PoolFsException>("with no copy matching its checksum there is nothing safe to hand over")
      .Which.Error.Should().Be(PoolFsError.IoError);
    this._Warnings().Should().Contain("f.bin");
  }

  [Test]
  [Category("EdgeCase")]
  public void Before_GivenNoBaselineWasEverRecorded_WhenRead_ThenItIsServedBecauseThereIsNothingToJudgeItBy() {
    // a pool whose database never saw this file cannot call it damaged; refusing it would make the
    // setting unusable on any pool that has not yet been scrubbed
    this._Mount("before");
    this._volume1.Seed("unknown.bin", false, _ORIGINAL);

    this._ReadAll("unknown.bin").Should().Equal(_ORIGINAL);
  }

  [Test]
  [Category("EdgeCase")]
  public void Before_GivenTheFileWasRewrittenSinceItsBaseline_WhenRead_ThenTheNewContentIsServed() {
    // a positional write marks the recorded checksum stale; the new content must not be mistaken for rot
    this._Mount("before");
    this._Store("f.bin");
    var handle = this._fs.Open("f.bin", AccessMode.ReadWrite, ShareMode.Read);
    this._fs.Write(handle, new byte[] { 1, 2, 3 }, 10, WriteMode.Normal);
    this._fs.Close(handle);
    this._fs.FlushPath("f.bin");

    var read = this._ReadAll("f.bin");

    read[10..13].Should().Equal(new byte[] { 1, 2, 3 }, "a legitimate rewrite is not damage");
    this._Warnings().Should().BeEmpty();
  }

  [Test]
  [Category("EdgeCase")]
  public void Before_GivenTheSameVersionIsReadAgain_WhenRead_ThenItIsNotHashedTwice() {
    // the cost must be once per version of a copy, never once per read
    this._Mount("before");
    this._Store("f.bin");
    this._ReadAll("f.bin");
    var hashedAfterFirst = this._fs.ReadVerificationHashes;

    for (var i = 0; i < 5; ++i)
      this._ReadAll("f.bin");

    this._fs.ReadVerificationHashes.Should().Be(hashedAfterFirst, "an unchanged copy keeps its verdict");
    hashedAfterFirst.Should().BeGreaterThan(0, "or else nothing was checked in the first place");
  }

  #endregion

  #region after

  [Test]
  [Category("HappyPath")]
  public void After_GivenTheServingCopyIsDamaged_WhenRead_ThenTheDamageIsDeliveredThenWarnedAboutAndRepaired() {
    this._Mount("after");
    this._Store("f.bin");
    var (holder, shadow) = this._ServingCopy("f.bin");
    _Rot(holder, shadow, "f.bin");

    var read = this._ReadAll("f.bin");

    read.Should().NotEqual(_ORIGINAL, "\"after\" hands over first — that is its whole point, and its cost");
    this._fs.WaitForReadVerifications(TimeSpan.FromSeconds(10)).Should().BeTrue();
    this._Warnings().Should().Contain("f.bin").And.Contain("already", "the warning must say the damage was served, not merely found");
    holder.GetContent("f.bin", shadow).Should().Equal(_ORIGINAL, "and the damaged copy is repaired so the next read is right");
    this._ReadAll("f.bin").Should().Equal(_ORIGINAL);
  }

  [Test]
  [Category("HappyPath")]
  public void After_GivenAnIntactFile_WhenRead_ThenNothingIsWarned() {
    this._Mount("after");
    this._Store("f.bin");

    this._ReadAll("f.bin").Should().Equal(_ORIGINAL);
    this._fs.WaitForReadVerifications(TimeSpan.FromSeconds(10)).Should().BeTrue();
    this._Warnings().Should().BeEmpty();
  }

  #endregion

  [Test]
  [Category("Exception")]
  public void Config_GivenAnUnknownMode_WhenResolved_ThenItIsRejected() {
    var act = () => ConfigResolver.ResolveEffective(null, """{ "integrity": { "verifyReads": "sometimes" } }""");

    act.Should().Throw<Exception>("a typo must not silently mean \"never\"");
  }

}
