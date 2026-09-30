using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Concurrency stress (TST-CONCURRENCY): foreground I/O running against the background
/// scheduler, which is where the engine's remaining races live. The seeded fuzz suite is
/// SEQUENTIAL — it can never observe a background job mutating a file under a foreground
/// writer's feet, so these tests are the ones that hold the per-file locking discipline
/// honest (SAFE-NOLOSS, SAFE-COHERE).
///
/// The pool is configured with <c>minCopiesBeforeAck: 1</c> and duplication 2 on purpose:
/// that is the only shape where a write acks from ONE copy and leaves the second OWED in
/// the write buffer, so <see cref="OwedSyncJob"/> genuinely races the foreground path.
///
/// Each file has exactly ONE owner thread, so its expected content is unambiguous — any
/// divergence is the engine, never an ambiguous interleaving of the test's own writes.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ConcurrencyFuzzTests {

  private const string _CONFIG = """
    {
      "duplication": 2,
      "write": { "policy": "write-back", "minCopiesBeforeAck": 1 },
      "io": { "mirrorReadSplitThreshold": "256" },
      "readAhead": { "enabled": false }
    }
    """;

  private static readonly Guid _pool = Guid.Parse("c00c0000-0000-0000-0000-00000000000c");

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 26);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 26);
    this._fs = new(_pool, [new(this._v1), new(this._v2)],
      new("conc" + Guid.NewGuid().ToString("N"), new() { Size = "16777216", BlockSize = "512", MetadataEntries = 1000, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, _CONFIG));
    this._fs.Mount(new(@"X:\"));
  }

  [TearDown]
  public void TearDown() => this._fs.Dispose();

  /// <summary>Content of version <paramref name="version"/>: self-describing, so a divergence names the version that leaked.</summary>
  private static byte[] _Content(int file, int version, int length) {
    var content = new byte[length];
    for (var i = 0; i < length; ++i)
      content[i] = (byte)((file * 31 + version * 7 + i) & 0xFF);

    return content;
  }

  private byte[] _ReadAll(string path) {
    var handle = this._fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var length = this._fs.GetAttributes(path).Length;
      var buffer = new byte[length];
      var read = 0;
      while (read < length) {
        var got = this._fs.Read(handle, buffer.AsSpan(read), read);
        if (got <= 0)
          break;

        read += got;
      }

      return buffer.AsSpan(0, read).ToArray();
    } finally {
      this._fs.Close(handle);
    }
  }

  /// <summary>Every physical copy of a path, primary and shadow, across both members.</summary>
  private List<(string where, byte[] content)> _PhysicalCopies(string path) {
    var found = new List<(string, byte[])>();
    foreach (var (volume, name) in new[] { (this._v1, "v1"), (this._v2, "v2") })
      foreach (var shadow in new[] { false, true })
        if (volume.GetContent(path, shadow) is { } content)
          found.Add(($"{name}{(shadow ? "/shadow" : "/primary")}", content));

    return found;
  }

  /// <summary>Runs <paramref name="body"/> on its own thread while the background scheduler is pumped hard.</summary>
  private void _WithBackgroundPump(Action body) {
    var scheduler = this._fs.CreateScheduler();
    using var stop = new ManualResetEventSlim(false);
    Exception? pumpFailure = null;
    var pump = new Thread(() => {
      try {
        while (!stop.IsSet) {
          scheduler.Pump();
          Thread.Yield();
        }
      } catch (Exception e) {
        pumpFailure = e;
      }
    }) { IsBackground = true, Name = "bg-pump" };

    pump.Start();
    try {
      body();
    } finally {
      stop.Set();
      pump.Join(TimeSpan.FromSeconds(30));
    }

    scheduler.Quiesce();
    pumpFailure.Should().BeNull("the background scheduler must never throw an unhandled exception");
  }

  [Test]
  [Category("EdgeCase")]
  public void Concurrency_GivenForegroundWritesAndBackgroundJobs_ThenEveryAcknowledgedWriteSurvives() {
    const int files = 6;
    const int versions = 40;
    const int length = 2048;

    var paths = Enumerable.Range(0, files).Select(f => $"c{f}.bin").ToArray();
    foreach (var path in paths)
      this._fs.Close(this._fs.Create(path, NodeKind.File, CreateFlags.None));

    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

    this._WithBackgroundPump(() => {
      var workers = paths.Select((path, file) => new Thread(() => {
        for (var version = 1; version <= versions; ++version) {
          var expected = _Content(file, version, length);
          var handle = this._fs.Open(path, AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
          try {
            this._fs.Write(handle, expected, 0, WriteMode.Normal);
          } finally {
            this._fs.Close(handle);
          }

          // read-your-writes (SAFE-COHERE): this thread owns the file, so the very next read
          // must return exactly what the engine just acknowledged — no background job may
          // roll it back to an earlier version
          var got = this._ReadAll(path);
          if (!got.AsSpan().SequenceEqual(expected))
            failures.Add($"'{path}' v{version}: read-your-writes violated (got {got.Length} bytes, first mismatch at {_FirstDiff(got, expected)})");
        }
      }) { IsBackground = true, Name = $"writer-{file}" }).ToArray();

      foreach (var worker in workers)
        worker.Start();
      foreach (var worker in workers)
        worker.Join(TimeSpan.FromMinutes(2)).Should().BeTrue("a writer thread must not hang");
    });

    failures.Should().BeEmpty("no acknowledged write may be rolled back by a background job");

    // final state: the last acknowledged version of every file, on every copy
    for (var file = 0; file < files; ++file) {
      var expected = _Content(file, versions, length);
      this._ReadAll(paths[file]).Should().Equal(expected, $"'{paths[file]}' must settle on its last acknowledged version");
      foreach (var (where, content) in this._PhysicalCopies(paths[file]))
        content.Should().Equal(expected, $"copy {where} of '{paths[file]}' must converge on the last acknowledged version");
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Concurrency_GivenBackgroundHealDuringForegroundWrites_ThenCopiesNeverDiverge() {
    const int files = 4;
    const int rounds = 30;
    const int length = 1536;

    var paths = Enumerable.Range(0, files).Select(f => $"h{f}.bin").ToArray();
    foreach (var path in paths)
      this._fs.Close(this._fs.Create(path, NodeKind.File, CreateFlags.None));

    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

    this._WithBackgroundPump(() => {
      // a heal scan is requested continuously, so _HealOne runs against files a foreground
      // thread is actively rewriting — the guard it uses (IsDirty/IsOpen) is a TOCTOU check
      var healer = new Thread(() => {
        for (var i = 0; i < rounds * files; ++i) {
          this._fs.RequestHeal();
          Thread.Yield();
        }
      }) { IsBackground = true, Name = "heal-requester" };

      var workers = paths.Select((path, file) => new Thread(() => {
        for (var version = 1; version <= rounds; ++version) {
          var expected = _Content(file, version, length);
          var handle = this._fs.Open(path, AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
          try {
            this._fs.Write(handle, expected, 0, WriteMode.Normal);
          } finally {
            this._fs.Close(handle);
          }

          var got = this._ReadAll(path);
          if (!got.AsSpan().SequenceEqual(expected))
            failures.Add($"'{path}' v{version}: heal rolled back an acknowledged write (first mismatch at {_FirstDiff(got, expected)})");
        }
      }) { IsBackground = true, Name = $"heal-writer-{file}" }).ToArray();

      healer.Start();
      foreach (var worker in workers)
        worker.Start();
      foreach (var worker in workers)
        worker.Join(TimeSpan.FromMinutes(2)).Should().BeTrue("a writer thread must not hang");
      healer.Join(TimeSpan.FromSeconds(30));
    });

    failures.Should().BeEmpty("the heal job must never publish a stale copy over acknowledged data");

    for (var file = 0; file < files; ++file) {
      var expected = _Content(file, rounds, length);
      var copies = this._PhysicalCopies(paths[file]);
      copies.Should().NotBeEmpty($"'{paths[file]}' must still exist");
      foreach (var (where, content) in copies)
        content.Should().Equal(expected, $"copy {where} of '{paths[file]}' diverged after concurrent heal");
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Concurrency_GivenConcurrentCreateDeleteAndBackgroundJobs_ThenTheEngineStaysConsistent() {
    const int threads = 4;
    const int rounds = 40;

    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

    this._WithBackgroundPump(() => {
      var workers = Enumerable.Range(0, threads).Select(worker => new Thread(() => {
        for (var round = 0; round < rounds; ++round) {
          var path = $"churn-{worker}-{round}.bin";
          var content = _Content(worker, round, 700);
          try {
            var handle = this._fs.Create(path, NodeKind.File, CreateFlags.None);
            try {
              this._fs.Write(handle, content, 0, WriteMode.Normal);
            } finally {
              this._fs.Close(handle);
            }

            var got = this._ReadAll(path);
            if (!got.AsSpan().SequenceEqual(content))
              failures.Add($"'{path}': content diverged immediately after create+write");

            this._fs.Unlink(path);
          } catch (PoolFsException e) {
            failures.Add($"'{path}': unexpected {e.Error} — {e.Message}");
          }
        }
      }) { IsBackground = true, Name = $"churn-{worker}" }).ToArray();

      foreach (var worker in workers)
        worker.Start();
      foreach (var worker in workers)
        worker.Join(TimeSpan.FromMinutes(2)).Should().BeTrue("a churn thread must not hang");
    });

    failures.Should().BeEmpty("create/write/read/delete under background pressure must never fail or diverge");

    // every file was deleted — no physical residue may remain on any member
    foreach (var volume in new[] { this._v1, this._v2 })
      volume.FilePaths.Where(p => p.Contains("churn-", StringComparison.OrdinalIgnoreCase))
        .Should().BeEmpty($"'{volume.DisplayName}' must hold no residue of deleted files");
  }

  [Test]
  [Category("EdgeCase")]
  public void Concurrency_GivenTheHealerRebuildsAMissingCopyWhileTheFileIsRewritten_ThenNoAcknowledgedWriteIsRolledBack() {
    // The same race as above, with the healer given real work: each file starts with its second copy
    // missing, so every heal is a genuine cross-disk copy racing the writers — not a no-op on a file
    // that is already whole.
    const int files = 4;
    const int rounds = 30;
    const int length = 1536;

    var paths = Enumerable.Range(0, files).Select(f => $"m{f}.bin").ToArray();
    foreach (var path in paths) {
      var handle = this._fs.Create(path, NodeKind.File, CreateFlags.None);
      this._fs.Write(handle, _Content(99, 0, length), 0, WriteMode.Normal);
      this._fs.Close(handle);
    }

    this._fs.CreateScheduler().Quiesce();
    foreach (var path in paths)
    foreach (var volume in new[] { this._v1, this._v2 })
      if (volume.FileExists(path, true))
        volume.Delete(path, true); // the second copy is gone: the healer has to make it again
    this._fs.Placement.InvalidateAll();

    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    this._WithBackgroundPump(() => {
      var healer = new Thread(() => {
        for (var i = 0; i < rounds * files; ++i) {
          this._fs.RequestHeal();
          Thread.Yield();
        }
      }) { IsBackground = true, Name = "heal-requester" };

      var workers = paths.Select((path, file) => new Thread(() => {
        for (var version = 1; version <= rounds; ++version) {
          var expected = _Content(file, version, length);
          var handle = this._fs.Open(path, AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
          try {
            this._fs.Write(handle, expected, 0, WriteMode.Normal);
          } finally {
            this._fs.Close(handle);
          }

          var got = this._ReadAll(path);
          if (!got.AsSpan().SequenceEqual(expected))
            failures.Add($"'{path}' v{version}: heal rolled back an acknowledged write (first mismatch at {_FirstDiff(got, expected)})");
        }
      }) { IsBackground = true, Name = $"heal-writer-{file}" }).ToArray();

      healer.Start();
      foreach (var worker in workers)
        worker.Start();
      foreach (var worker in workers)
        worker.Join(TimeSpan.FromMinutes(2)).Should().BeTrue("a writer thread must not hang");
      healer.Join(TimeSpan.FromSeconds(30));
    });

    failures.Should().BeEmpty("the heal job must never publish a stale copy over acknowledged data");
  }

  [Test]
  [Category("EdgeCase")]
  public void Heal_GivenTheFileIsRewrittenAtTheSameLengthWithinOneTimestampTick_ThenTheStaleCopyIsNotPublished() {
    // FAT32 keeps modification times in two-second steps (exFAT in ten-millisecond ones), and SD
    // cards and USB sticks are pool members too. A rewrite of the same length inside one step leaves
    // size AND time exactly as they were — which is all the healer used to check before publishing
    // the copy it had been making. Simulated here by putting the source's time back after the write.
    const int length = 1536;
    var handle = this._fs.Create("tick.bin", NodeKind.File, CreateFlags.None);
    this._fs.Write(handle, _Content(7, 1, length), 0, WriteMode.Normal);
    this._fs.Close(handle);
    this._fs.CreateScheduler().Quiesce();

    FakeVolumeIO? source = null;
    foreach (var volume in new[] { this._v1, this._v2 })
      if (volume.FileExists("tick.bin", true))
        volume.Delete("tick.bin", true);
      else if (volume.FileExists("tick.bin", false))
        source = volume;
    this._fs.Placement.InvalidateAll();
    var sourceTime = source!.Stat("tick.bin", false)!.Value.LastWriteTimeUtc;

    // pause the heal once it has copied every byte (as it stamps its temp, just before the commit),
    // rewrite the file, and leave the time as a FAT disk would
    var target = source == this._v1 ? this._v2 : this._v1;
    var rewritten = _Content(7, 2, length);
    var fired = 0;
    target.BeforeOperation = (op, path) => {
      if (op != VolumeOp.SetTimestamps || !path.StartsWith("tick.bin.", StringComparison.Ordinal) || Interlocked.Exchange(ref fired, 1) == 1)
        return;

      var writer = new Thread(() => {
        var h = this._fs.Open("tick.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
        this._fs.Write(h, rewritten, 0, WriteMode.Normal);
        this._fs.Close(h);
        source.SetTimestamps("tick.bin", false, null, sourceTime);
      });
      writer.Start();
      writer.Join();
    };

    this._fs.RequestHeal();
    this._fs.CreateScheduler().Quiesce();
    target.BeforeOperation = null;

    fired.Should().Be(1, "the rewrite has to land inside the heal's copy, or this proves nothing");
    foreach (var volume in new[] { this._v1, this._v2 })
    foreach (var shadow in new[] { false, true })
      if (volume.GetContent("tick.bin", shadow) is { } copy)
        copy.Should().Equal(rewritten, $"the copy on '{volume.DisplayName}' (shadow: {shadow}) must be the rewritten file, not the one the heal began copying");
  }

  private static string _FirstDiff(byte[] got, byte[] expected) {
    if (got.Length != expected.Length)
      return $"length {got.Length} != {expected.Length}";

    for (var i = 0; i < got.Length; ++i)
      if (got[i] != expected[i])
        return $"byte {i} ({got[i]} != {expected[i]})";

    return "none";
  }

}
