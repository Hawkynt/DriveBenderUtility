using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Block-checked reads and parity repair, with the product defaults (verifyReads "blocks",
/// parity 2): damage on one copy is served around and repaired, damage on every copy is rebuilt
/// from parity, damage beyond parity fails the read rather than hand over wrong bytes, and an index
/// is never believed once the file has changed — not even when the change kept its length and time.
/// </summary>
[TestFixture]
[Category("Unit")]
public class BlockIntegrityTests {

  private static readonly Guid _pool = Guid.Parse("b10cb10c-0000-0000-0000-000000000001");
  private const int _SIZE = 300_000;

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  private readonly System.Collections.Concurrent.ConcurrentQueue<string> _log = new();

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", 1L << 26);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", 1L << 26);
    this._log.Clear();
    DriveBender.Logger = message => this._log.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");
  }

  /// <summary>On a failure, what the engine said and what each copy's index looked like — the evidence a CI run leaves.</summary>
  [TearDown]
  public void TearDown() {
    if (TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed)
      return;

    foreach (var line in this._log)
      TestContext.Out.WriteLine("log: " + line);
    foreach (var path in new[] { "film.bin", "db.bin", "x.bin", "archive.bin", "log.bin", "old.bin", "a.bin", "b.bin" })
      foreach (var (volume, shadow) in this._Copies(path)) {
        var meta = volume.Stat(path, shadow)!.Value;
        var bytes = volume.GetContent(BlockIntegrity.IndexPathFor(path, shadow), false);
        var index = bytes == null ? null : Integrity.BlockIndex.Deserialize(bytes);
        TestContext.Out.WriteLine($"copy {path} {volume.DisplayName} shadow={shadow} len={meta.Length} t={meta.LastWriteTimeUtc.Ticks} "
                                  + $"index={(bytes == null ? "none" : index == null ? "corrupt" : $"{index.Length}/{index.MTimeTicks} describes={index.Describes(meta.Length, meta.LastWriteTimeUtc)}")}");
      }
  }

  private PoolFileSystem _Engine(string extra = "") {
    var cache = new CacheInstance("bi" + Guid.NewGuid().ToString("N"),
      new() { Size = "4194304", BlockSize = "65536", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 2, "readAhead": { "enabled": false }, "write": { "closeIsDurable": true } {{extra}} }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static byte[] _Content(int seed = 1) {
    var bytes = new byte[_SIZE];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static void _Write(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.Truncate);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private static byte[] _Read(PoolFileSystem fs, string path) {
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var buffer = new byte[fs.GetAttributes(path).Length];
      var read = 0;
      while (read < buffer.Length && fs.Read(handle, buffer.AsSpan(read), read) is var got and > 0)
        read += got;
      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  private (FakeVolumeIO Volume, bool Shadow)[] _Copies(string path)
    => [.. new[] { this._v1, this._v2 }.SelectMany(v => new[] { false, true }.Where(s => v.FileExists(path, s)).Select(s => (v, s)))];

  /// <summary>A fresh engine, so nothing the previous one cached can hide what is on disk.</summary>
  private PoolFileSystem _Indexed(string path, byte[] content, string extra = "") {
    using (var writer = this._Engine(extra)) {
      _Write(writer, path, content);
      writer.IndexFile(path);
    }

    foreach (var (volume, shadow) in this._Copies(path))
      volume.FileExists(BlockIntegrity.IndexPathFor(path, shadow), false).Should().BeTrue("the premise: every copy has its index");

    return this._Engine(extra);
  }

  [Test]
  [Category("HappyPath")]
  public void Index_GivenAWrittenFile_ThenEveryCopyGetsOneAndReadsCheckIt() {
    using var fs = this._Indexed("film.bin", _Content());

    _Read(fs, "film.bin").Should().Equal(_Content());
    fs.BlockIntegrityStats.Checked.Should().BeGreaterThan(0, "the read was checked shard by shard");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenOneCopySilentlyDamaged_ThenTheIntactOneServesItAndTheDamageIsRepaired() {
    var content = _Content();
    using var fs = this._Indexed("film.bin", content);
    var (damaged, shadow) = this._Copies("film.bin")[0];
    var stamped = damaged.Stat("film.bin", shadow)!.Value.LastWriteTimeUtc;
    damaged.CorruptSilently("film.bin", shadow, bytes => bytes[100_000] ^= 0xFF);

    _Read(fs, "film.bin").Should().Equal(content, "a damaged shard is never served");

    _Eventually(() => damaged.GetContent("film.bin", shadow)!.SequenceEqual(content)).Should().BeTrue("the damaged copy is repaired");
    damaged.Stat("film.bin", shadow)!.Value.LastWriteTimeUtc.Should().Be(stamped, "a repair restores content, not the file's time");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenEveryCopyDamagedInTheSameShard_ThenParityRebuildsItAndBothAreRepaired() {
    var content = _Content();
    using var fs = this._Indexed("film.bin", content);
    foreach (var (volume, shadow) in this._Copies("film.bin"))
      volume.CorruptSilently("film.bin", shadow, bytes => { bytes[70_000] ^= 0x01; bytes[200_000] ^= 0x80; });

    _Read(fs, "film.bin").Should().Equal(content, "parity rebuilds what no copy holds intact");

    _Eventually(() => this._Copies("film.bin").All(c => c.Volume.GetContent("film.bin", c.Shadow)!.SequenceEqual(content)))
      .Should().BeTrue("every damaged copy is repaired from the rebuilt shards");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenMoreDamageThanParityOnEveryCopy_ThenTheReadFailsRatherThanServeWrongBytes() {
    using var fs = this._Indexed("film.bin", _Content());
    foreach (var (volume, shadow) in this._Copies("film.bin"))
      volume.CorruptSilently("film.bin", shadow, bytes => { bytes[1_000] ^= 1; bytes[70_000] ^= 1; bytes[140_000] ^= 1; });
    this._AssertIndexedNow("film.bin");

    var read = () => _Read(fs, "film.bin");

    read.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.IoError);
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenARewriteThatKeepsLengthAndTime_ThenTheOldIndexIsNeverUsedToRollItBack() {
    // the case that would turn the feature into data loss: the same length and the same timestamp
    // make an old index look current, and "repairing" from its parity writes the old content back
    var original = _Content(1);
    using var fs = this._Indexed("db.bin", original);
    var times = this._Copies("db.bin").Select(c => c.Volume.Stat("db.bin", c.Shadow)!.Value.LastWriteTimeUtc).ToArray();

    // The writer keeps the file open (so nothing re-indexes it meanwhile) and fsyncs (so the new
    // bytes are on disk, not in the write buffer's overlay); then the times go back. The old index
    // is still on disk and now looks current — exactly what a same-tick rewrite leaves behind.
    var handle = fs.Open("db.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
    fs.Write(handle, Enumerable.Repeat((byte)0xAB, 4096).ToArray(), 100_000, WriteMode.Normal);
    fs.Flush(handle);
    var copies = this._Copies("db.bin");
    for (var i = 0; i < copies.Length; ++i)
      copies[i].Volume.SetTimestamps("db.bin", copies[i].Shadow, null, times[i]); // within one tick of the clock

    var expected = (byte[])original.Clone();
    Array.Fill(expected, (byte)0xAB, 100_000, 4096);
    _Read(fs, "db.bin").Should().Equal(expected, "the new content, not the old one rebuilt from the old index's parity");
    fs.WaitForReadVerifications(TimeSpan.FromSeconds(5));
    Thread.Sleep(300); // any repair the read queued has had its chance
    foreach (var (volume, shadow) in this._Copies("db.bin"))
      volume.GetContent("db.bin", shadow).Should().Equal(expected, "nothing rolled the acknowledged write back");
    fs.Close(handle);

    using var remounted = this._Engine();
    _Read(remounted, "db.bin").Should().Equal(expected, "after a remount too: the old index was dropped, not merely ignored");
    fs.WaitForIndexing(TimeSpan.FromSeconds(5));
    foreach (var (volume, shadow) in this._Copies("db.bin"))
      volume.GetContent("db.bin", shadow).Should().Equal(expected, "nothing rolled the write back");
  }

  [Test]
  [Category("EdgeCase")]
  public void Index_GivenCopiesThatDisagree_ThenNoneIsBuilt() {
    using (var writer = this._Engine())
      _Write(writer, "x.bin", _Content());
    BlockIntegrity.DeleteIndexes([this._v1, this._v2], "x.bin"); // whatever the writer's own indexer made while they agreed
    using var fs = this._Engine();
    var (volume, shadow) = this._Copies("x.bin")[0];
    volume.CorruptSilently("x.bin", shadow, bytes => bytes[5] ^= 1); // after the mount, whose quick scan would reconcile it
    fs.IndexFile("x.bin");

    this._Copies("x.bin").Should().OnlyContain(c => !c.Volume.FileExists(BlockIntegrity.IndexPathFor("x.bin", c.Shadow), false),
      "an index built now would enshrine whichever copy was read as the truth");
  }

  [Test]
  [Category("EdgeCase")]
  public void Recovery_GivenAnInterruptedWrite_ThenTheFilesIndexesAreDropped() {
    var fs = this._Indexed("log.bin", _Content());
    fs.Journal.LogIntent(JournalOp.Write, "log.bin"); // the power fails with it open
    fs.Dispose();

    using var recovered = this._Engine();

    this._Copies("log.bin").Should().OnlyContain(c => !c.Volume.FileExists(BlockIntegrity.IndexPathFor("log.bin", c.Shadow), false));
  }

  [Test]
  [Category("EdgeCase")]
  public void Index_GivenTheIndexItselfIsDamaged_ThenItIsIgnoredAndTheDataServed() {
    var content = _Content();
    using var fs = this._Indexed("film.bin", content);
    foreach (var (volume, shadow) in this._Copies("film.bin"))
      volume.CorruptSilently(BlockIntegrity.IndexPathFor("film.bin", shadow), false, bytes => bytes[40] ^= 0xFF);

    _Read(fs, "film.bin").Should().Equal(content, "a rotted index proves nothing about the data");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenNoParityAndOneCopyDamaged_ThenTheOtherServesIt_AndDamageOnBothFailsTheRead() {
    var content = _Content();
    const string noParity = """, "integrity": { "parity": 0 }""";
    using (var fs = this._Indexed("a.bin", content, noParity)) {
      var (volume, shadow) = this._Copies("a.bin")[0];
      volume.CorruptSilently("a.bin", shadow, bytes => bytes[10] ^= 1);
      _Read(fs, "a.bin").Should().Equal(content);
    }

    using (var fs = this._Indexed("b.bin", content, noParity)) {
      foreach (var (volume, shadow) in this._Copies("b.bin"))
        volume.CorruptSilently("b.bin", shadow, bytes => bytes[10] ^= 1);
      var read = () => _Read(fs, "b.bin");
      read.Should().Throw<PoolFsException>("without parity, damage on every copy can only be refused");
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Config_GivenAParityThatIsNotAPowerOfTwo_ThenItIsRefused() {
    var resolve = () => ConfigValidator.Validate(ConfigResolver.ResolveEffective(null, """{ "integrity": { "parity": 3 } }"""));
    resolve.Should().Throw<ConfigValidationException>();
  }

  [Test]
  [Category("EdgeCase")]
  public void Scrub_GivenRotOnEveryCopy_ThenParityRepairsWhatACopyComparisonCannot() {
    var content = _Content();
    using var fs = this._Indexed("archive.bin", content);
    fs.RunScrub(); // the whole-file baseline the scrub compares against
    foreach (var (volume, shadow) in this._Copies("archive.bin"))
      volume.CorruptSilently("archive.bin", shadow, bytes => bytes[123_456] ^= 0x10);

    var issues = fs.RunScrub();

    issues.Should().Contain(i => i.Path == "archive.bin" && i.Kind == IntegrityIssueKind.BitRotRepaired);
    issues.Should().NotContain(i => i.Kind == IntegrityIssueKind.BitRotUnrecoverable);
    foreach (var (volume, shadow) in this._Copies("archive.bin"))
      volume.GetContent("archive.bin", shadow).Should().Equal(content);
  }

  /// <summary>The premise of a test about checked reads: every copy's index is intact and describes the copy right now.</summary>
  private void _AssertIndexedNow(string path) {
    foreach (var (volume, shadow) in this._Copies(path)) {
      var meta = volume.Stat(path, shadow)!.Value;
      var index = Integrity.BlockIndex.Deserialize(volume.GetContent(BlockIntegrity.IndexPathFor(path, shadow), false)
                                                   ?? throw new AssertionException($"premise: {volume.DisplayName} has no index"));
      index.Should().NotBeNull($"premise: {volume.DisplayName}'s index is intact");
      index!.Describes(meta.Length, meta.LastWriteTimeUtc).Should().BeTrue(
        $"premise: {volume.DisplayName}'s index describes the copy (index {index.Length}/{index.MTimeTicks}, copy {meta.Length}/{meta.LastWriteTimeUtc.Ticks})");
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Index_GivenRotOnEveryCopyThatTheChecksumDatabaseRecordedBefore_ThenTheRotIsNotIndexed() {
    using (var writer = this._Engine()) {
      _Write(writer, "old.bin", _Content());
      writer.RunScrub(); // records the whole-file baseline
      writer.WaitForIndexing(TimeSpan.FromSeconds(5));
    }

    BlockIntegrity.DeleteIndexes([this._v1, this._v2], "old.bin"); // a file indexed only later
    foreach (var (volume, shadow) in this._Copies("old.bin"))
      volume.CorruptSilently("old.bin", shadow, bytes => bytes[50_000] ^= 0x40);

    using var fs = this._Engine();
    fs.IndexFile("old.bin");

    this._Copies("old.bin").Should().OnlyContain(c => !c.Volume.FileExists(BlockIntegrity.IndexPathFor("old.bin", c.Shadow), false),
      "identical rot on every copy contradicts the recorded checksum, and must not become the content an index defends");
  }

  private static bool _Eventually(Func<bool> condition) {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (DateTime.UtcNow < deadline) {
      if (condition())
        return true;
      Thread.Sleep(20);
    }

    return condition();
  }

}
