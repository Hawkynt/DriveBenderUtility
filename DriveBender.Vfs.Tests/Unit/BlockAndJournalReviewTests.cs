using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class BlockAndJournalReviewTests {
  private static readonly Guid _pool = Guid.Parse("b10cb10c-0000-0000-0000-000000000002");
  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", 1L << 26);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", 1L << 26);
  }

  private PoolFileSystem _Engine(int dup) {
    var cache = new CacheInstance("ap" + Guid.NewGuid().ToString("N"),
      new() { Size = "4194304", BlockSize = "65536", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": {{dup}}, "readAhead": { "enabled": false }, "write": { "closeIsDurable": true } }"""));
    fs.Mount(new(@"X:\"));
    return fs;
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

  /// <summary>Rot after a file was indexed: the scrub must repair it from the parity recorded before, never re-index the rot.</summary>
  [TestCase(1)]
  [TestCase(2)]
  public void Scrub_GivenAnIndexedFileThatRottedSince_ThenItIsRepairedNotReindexed(int dup) => this._Rot(dup, true);

  [TestCase(1)]
  [TestCase(2)]
  public void Read_GivenAnIndexedFileThatRottedSince_ThenItIsRepaired(int dup) => this._Rot(dup, false);
  private void _Rot(int dup, bool scrub) {
    var content = new byte[300_000];
    new Random(1).NextBytes(content);
    using (var writer = this._Engine(dup)) {
      var h = writer.Create("a.bin", NodeKind.File, CreateFlags.Truncate);
      writer.Write(h, content, 0, WriteMode.Normal);
      writer.Close(h);
      writer.WaitForIndexing(TimeSpan.FromSeconds(5));
      writer.IndexFile("a.bin");
    }
    foreach (var (v, s) in this._Copies("a.bin"))
      v.FileExists(BlockIntegrity.IndexPathFor("a.bin", s), false).Should().BeTrue("premise: indexed");

    foreach (var (v, s) in this._Copies("a.bin"))
      v.CorruptSilently("a.bin", s, b => b[123_456] ^= 0x10);

    using var fs = this._Engine(dup);
    if (scrub) fs.RunScrub();                       // what the weekly scrub does: IndexAllFiles + ScrubAll
    fs.WaitForIndexing(TimeSpan.FromSeconds(10));
    _Read(fs, "a.bin").Should().Equal(content, "the parity recorded before the rot could have repaired it");
  }

  [Test]
  public void Journal_GivenALaggingMirrorFailsAnAppend_ThenLaterReadsStillWork() {
    var m1 = new FakeVolumeIO(Guid.NewGuid(), "m1", "P1");
    var m2 = new FakeVolumeIO(Guid.NewGuid(), "m2", "P2");
    var m3 = new FakeVolumeIO(Guid.NewGuid(), "m3", "P3");
    var store = new MemberJournalStore([m1, m2, m3]);
    var journal = new Journal(store);
    var seq = journal.LogIntent(JournalOp.Write, "f.txt"); // all healthy
    var fail = true;
    m3.BeforeOperation = (op, p) => { if (fail && op == VolumeOp.OpenWrite && p.Contains("journal", StringComparison.OrdinalIgnoreCase)) throw new IOException("No space left on device"); };
    var seq2 = journal.LogIntent(JournalOp.Write, "g.txt"); // quorum m1,m2: returns fine
    Thread.Sleep(200);
    fail = false;
    var act = () => store.ReadAll().ToList();
    act.Should().NotThrow("a mirror that failed an append is not a reason for every later read to fail");
  }

  [Test]
  public void Journal_GivenAFailedMirrorGoesOffline_ThenTheTwoHealthyOnesStillTakeEveryAppend() {
    var m1 = new FakeVolumeIO(Guid.NewGuid(), "m1", "P1");
    var m2 = new FakeVolumeIO(Guid.NewGuid(), "m2", "P2");
    var m3 = new FakeVolumeIO(Guid.NewGuid(), "m3", "P3");
    var journal = new Journal(new MemberJournalStore([m1, m2, m3]));
    journal.LogIntent(JournalOp.Write, "f.txt");
    m3.BeforeOperation = (op, p) => { if (op == VolumeOp.OpenWrite && p.Contains("journal")) throw new IOException("EIO"); };
    journal.LogIntent(JournalOp.Write, "g.txt");
    Thread.Sleep(200);
    m3.IsOnline = false;
    var failures = 0;
    for (var i = 0; i < 5; ++i)
      try { journal.LogIntent(JournalOp.Write, "h" + i); } catch (Exception e) { ++failures; TestContext.Out.WriteLine(e.GetType().Name); }
    failures.Should().Be(0, "two healthy mirrors remain");
  }

  [Test]
  public void Rename_GivenAJournalMirrorFailing_ThenTheRenameSucceedsAndLeavesNoGhost() {
    var m1 = new FakeVolumeIO(Guid.NewGuid(), "m1", "P1");
    var m2 = new FakeVolumeIO(Guid.NewGuid(), "m2", "P2");
    var m3 = new FakeVolumeIO(Guid.NewGuid(), "m3", "P3");
    var cache = new CacheInstance("ap" + Guid.NewGuid().ToString("N"), new() { Size = "4194304", BlockSize = "65536", MetadataEntries = 500, MetadataTtl = "1m" });
    using var fs = new PoolFileSystem(_pool, [new(m1), new(m2), new(m3)], cache,
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "readAhead": { "enabled": false }, "write": { "closeIsDurable": true } }"""));
    fs.Mount(new(@"X:"));
    var h = fs.Create("a.txt", NodeKind.File, CreateFlags.Truncate);
    fs.Write(h, new byte[] { 1, 2, 3 }, 0, WriteMode.Normal);
    fs.Close(h);
    Thread.Sleep(6000); // past the compaction interval
    m3.BeforeOperation = (op, p) => { if (op == VolumeOp.OpenWrite && p.Contains("journal")) throw new IOException("ENOSPC"); };
    Exception? thrown = null;
    try { fs.Rename("a.txt", "b.txt", RenameFlags.None); } catch (Exception e) { thrown = e; }
    TestContext.Out.WriteLine($"thrown={thrown?.GetType().Name}: {thrown?.Message}");
    TestContext.Out.WriteLine($"a exists={fs.ReadDirectory("").Any(e => e.Name == "a.txt")} b exists={fs.ReadDirectory("").Any(e => e.Name == "b.txt")}");
    foreach (var m in new[] { m1, m2, m3 }) TestContext.Out.WriteLine(m.DisplayName + ": " + string.Join(",", m.FilePaths));
    m3.BeforeOperation = null;
    string? readA = null; try { readA = string.Join(",", _Read(fs, "a.txt")); } catch (Exception e) { readA = e.GetType().Name + " " + e.Message; }
    TestContext.Out.WriteLine("read a.txt => " + readA);
    var thrown2 = (Exception?)null; try { fs.Unlink("x-nonexist"); } catch (Exception e) { thrown2 = e; }
    TestContext.Out.WriteLine("later op => " + thrown2?.GetType().Name + " " + thrown2?.Message);
    thrown.Should().BeNull("one mirror failing must not fail an operation the other mirrors recorded");
    fs.ReadDirectory("").Should().NotContain(e => e.Name == "a.txt");
    _Read(fs, "b.txt").Should().Equal(new byte[] { 1, 2, 3 });
  }
}
