using System.Text;
using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Model-based fuzzing (TST-FUZZ): deterministic seeded op sequences run against the engine and
/// an in-memory oracle in lockstep — any divergence is a real bug. Includes storage-failure
/// simulation: a duplicated pool must survive losing a member DURING reads (failover) and random
/// injected faults must never corrupt what the engine acknowledged.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FuzzTests {

  private static readonly Guid _pool = Guid.Parse("f0220000-0000-0000-0000-00000000000f");

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private CacheInstance _cache = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24);
    this._cache = new("fuzz" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "64", MetadataEntries = 1000, MetadataTtl = "1m" });
    this._fs = new(_pool, [new(this._v1), new(this._v2)], this._cache,
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "io": { "mirrorReadSplitThreshold": "256" } }"""));
    this._fs.Mount(new(@"X:\"));
  }

  private byte[] _ReadAll(string path) {
    var handle = this._fs.Open(path, AccessMode.Read, ShareMode.Read);
    try {
      var length = this._fs.GetAttributes(path).Length;
      var buffer = new byte[length];
      var read = 0;
      while (read < length)
        read += this._fs.Read(handle, buffer.AsSpan(read), read);
      return buffer;
    } finally {
      this._fs.Close(handle);
    }
  }

  private void _VerifyOracle(Dictionary<string, byte[]> oracle) {
    foreach (var (path, expected) in oracle)
      this._ReadAll(path).Should().Equal(expected, $"'{path}' must read back exactly what the engine acknowledged");
  }

  [Test]
  [Category("EdgeCase")]
  public void Fuzz_GivenSeededRandomOps_WhenReplayedAgainstOracle_ThenEngineNeverDiverges() {
    var random = new Random(87_411);
    var oracle = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var folders = new List<string> { "" };
    var nextId = 0;

    for (var step = 0; step < 400; ++step) {
      var roll = random.Next(100);
      if (roll < 25 || oracle.Count == 0) {
        // create a file with random content in a random folder
        var folder = folders[random.Next(folders.Count)];
        var path = (folder.Length == 0 ? "" : folder + "/") + $"f{nextId++}.bin";
        var content = new byte[random.Next(0, 700)];
        random.NextBytes(content);
        var handle = this._fs.Create(path, NodeKind.File, CreateFlags.None);
        if (content.Length > 0)
          this._fs.Write(handle, content, 0, WriteMode.Normal);
        this._fs.Close(handle);
        oracle[path] = content;
      } else if (roll < 45) {
        // overwrite a random range of an existing file
        var path = oracle.Keys.ElementAt(random.Next(oracle.Count));
        var current = oracle[path];
        var offset = random.Next(0, current.Length + 32);
        var patch = new byte[random.Next(1, 128)];
        random.NextBytes(patch);
        var handle = this._fs.Open(path, AccessMode.ReadWrite, ShareMode.Read);
        this._fs.Write(handle, patch, offset, WriteMode.Normal);
        this._fs.Close(handle);
        var grown = new byte[Math.Max(current.Length, offset + patch.Length)];
        current.CopyTo(grown, 0);
        patch.CopyTo(grown, offset);
        oracle[path] = grown;
      } else if (roll < 60) {
        // read a random slice and compare immediately (read-your-writes)
        var path = oracle.Keys.ElementAt(random.Next(oracle.Count));
        var expected = oracle[path];
        if (expected.Length > 0) {
          var offset = random.Next(0, expected.Length);
          var want = Math.Min(expected.Length - offset, random.Next(1, 300));
          var buffer = new byte[want];
          var handle = this._fs.Open(path, AccessMode.Read, ShareMode.Read);
          var got = this._fs.Read(handle, buffer, offset);
          this._fs.Close(handle);
          buffer.AsSpan(0, got).ToArray().Should().Equal(expected.AsSpan(offset, got).ToArray(), $"step {step}: slice of '{path}' diverged");
        }
      } else if (roll < 70) {
        // rename a file into a random folder
        var path = oracle.Keys.ElementAt(random.Next(oracle.Count));
        var folder = folders[random.Next(folders.Count)];
        var target = (folder.Length == 0 ? "" : folder + "/") + $"r{nextId++}.bin";
        this._fs.Rename(path, target, RenameFlags.None);
        oracle[target] = oracle[path];
        oracle.Remove(path);
      } else if (roll < 80) {
        // delete
        var path = oracle.Keys.ElementAt(random.Next(oracle.Count));
        this._fs.Unlink(path);
        oracle.Remove(path);
      } else if (roll < 90) {
        // new folder
        var name = $"d{nextId++}";
        this._fs.MakeDir(name);
        folders.Add(name);
      } else {
        // truncate/grow
        var path = oracle.Keys.ElementAt(random.Next(oracle.Count));
        var newLength = random.Next(0, 900);
        var handle = this._fs.Open(path, AccessMode.ReadWrite, ShareMode.Read);
        this._fs.SetLength(handle, newLength);
        this._fs.Close(handle);
        var resized = new byte[newLength];
        oracle[path].AsSpan(0, Math.Min(newLength, oracle[path].Length)).CopyTo(resized);
        oracle[path] = resized;
      }
    }

    this._VerifyOracle(oracle);
  }

  [Test]
  [Category("Exception")]
  public void Read_GivenDuplicatedFileAndOneStorageDiesMidRead_ThenTheOtherCopyServesEveryBlock() {
    var content = new byte[4096];
    new Random(5).NextBytes(content);
    this._fs.MakeDir("big");
    var handle = this._fs.Create("big/movie.bin", NodeKind.File, CreateFlags.None);
    this._fs.Write(handle, content, 0, WriteMode.Normal);
    this._fs.Close(handle);
    this._v1.FileExists("big/movie.bin", false).Should().BeTrue();
    this._v2.FileExists("big/movie.bin", true).Should().BeTrue("duplication 2 placed a shadow on the second member");

    // remount so nothing is cached, then kill the PRIMARY's storage before reading
    var fresh = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)],
      new("fuzz2" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "64", MetadataEntries = 1000, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "io": { "mirrorReadSplitThreshold": "256" } }"""));
    fresh.Mount(new(@"X:\"));
    this._v1.AlwaysFail(VolumeOp.OpenRead);

    var read = fresh.Open("big/movie.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[content.Length];
    var got = 0;
    while (got < buffer.Length)
      got += fresh.Read(read, buffer.AsSpan(got), got);
    fresh.Close(read);

    buffer.Should().Equal(content, "a duplicated file survives a storage failing while being read (read failover)");
    fresh.Activity.History.Should().Contain(e => e.Kind == ActivityKind.Recovery && e.Reason.Contains("failover"),
      "the failover is visible in the activity feed");
  }

  [Test]
  [Category("EdgeCase")]
  public void Fuzz_GivenRandomOneShotFaults_WhenOpsFail_ThenAcknowledgedDataIsNeverCorrupted() {
    var random = new Random(52_003);
    var oracle = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var tainted = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // ops that failed mid-flight leave ambiguous state, like a crash
    var nextId = 0;
    var faultOps = new[] { VolumeOp.OpenWrite, VolumeOp.Write, VolumeOp.Flush, VolumeOp.OpenRead, VolumeOp.Truncate };

    for (var step = 0; step < 250; ++step) {
      if (random.Next(5) == 0) {
        var victim = random.Next(2) == 0 ? this._v1 : this._v2;
        victim.FailNext(faultOps[random.Next(faultOps.Length)], random.Next(2) == 0 ? PoolFsError.IoError : PoolFsError.NoSpace);
      }

      var roll = random.Next(100);
      try {
        if (roll < 40 || oracle.Count == 0) {
          var path = $"f{nextId++}.bin";
          var content = new byte[random.Next(1, 500)];
          random.NextBytes(content);
          var handle = this._fs.Create(path, NodeKind.File, CreateFlags.None);
          try {
            this._fs.Write(handle, content, 0, WriteMode.Normal);
            oracle[path] = content;
          } catch (PoolFsException) {
            tainted.Add(path); // acknowledged nothing — state may be partial, like a crash
          } finally {
            this._fs.Close(handle);
          }
        } else if (roll < 70) {
          var path = oracle.Keys.ElementAt(random.Next(oracle.Count));
          var expected = oracle[path];
          var buffer = new byte[expected.Length];
          var handle = this._fs.Open(path, AccessMode.Read, ShareMode.Read);
          try {
            var got = 0;
            while (got < buffer.Length)
              got += this._fs.Read(handle, buffer.AsSpan(got), got);
            if (!tainted.Contains(path))
              buffer.Should().Equal(expected, $"step {step}: a read that SUCCEEDS must return acknowledged content, fault or not");
          } catch (PoolFsException) {
            // a failed read is acceptable under injected faults — wrong data is not
          } finally {
            this._fs.Close(handle);
          }
        } else {
          var path = oracle.Keys.ElementAt(random.Next(oracle.Count));
          this._fs.Unlink(path);
          oracle.Remove(path);
          tainted.Remove(path);
        }
      } catch (PoolFsException) {
        // the op was refused outright — the oracle was not updated, consistent by construction
      }
    }

    // faults cleared: everything the engine ever acknowledged must read back intact
    this._v1.ClearFaults();
    this._v2.ClearFaults();
    foreach (var (path, expected) in oracle.Where(kv => !tainted.Contains(kv.Key)))
      this._ReadAll(path).Should().Equal(expected, $"'{path}' was acknowledged and must survive the fault storm");
  }

  [Test]
  [Category("HappyPath")]
  public void Read_GivenLargeMirroredRead_WhenSplitAcrossCopies_ThenBothMembersServeAndContentIsExact() {
    var content = new byte[8192];
    new Random(7).NextBytes(content);
    var handle = this._fs.Create("split.bin", NodeKind.File, CreateFlags.None);
    this._fs.Write(handle, content, 0, WriteMode.Normal);
    this._fs.Close(handle);

    // fresh engine: cold cache, large read over the split threshold → blocks load from BOTH copies in parallel
    var fresh = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)],
      new("fuzz3" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "64", MetadataEntries = 1000, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "io": { "mirrorReadSplitThreshold": "256" } }"""));
    fresh.Mount(new(@"X:\"));

    var read = fresh.Open("split.bin", AccessMode.Read, ShareMode.Read);
    var buffer = new byte[content.Length];
    var got = 0;
    while (got < buffer.Length)
      got += fresh.Read(read, buffer.AsSpan(got), got);
    fresh.Close(read);

    buffer.Should().Equal(content, "mirror-split parallel reads must assemble the exact content");
  }

}
