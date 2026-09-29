using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// A folder renamed while a file inside it is open and being written. The rename follows open files
/// to their new path, and a write that is acknowledged must end up in the file under whichever name
/// it has by the end — never in a ghost of the old folder, never nowhere.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FolderRenameRaceTests {

  private static readonly Guid _pool = Guid.Parse("57a6ed00-0000-0000-0000-000000000078");

  private const int _BLOCK = 512;
  private const int _BLOCKS = 160;
  private const int _TRIALS = 40;

  private static PoolFileSystem _Engine(string config, out FakeVolumeIO[] members) {
    members = [
      new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 64L << 20),
      new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 64L << 20),
    ];
    var cache = new CacheInstance("fr" + Guid.NewGuid().ToString("N"), new() { Size = "4194304", BlockSize = "512", MetadataEntries = 1000, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [.. members.Select(m => new EngineMember(m))], cache, ConfigResolver.ResolveEffective(null, config));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static byte[] _Block(int trial, int block) => Enumerable.Repeat((byte)(1 + (trial * 7 + block) % 250), _BLOCK).ToArray();

  [TestCase("""{ "duplication": 1 }""", TestName = "RenameFolder_GivenAChildIsBeingWrittenInAnUnduplicatedPool_ThenEveryAcknowledgedWriteEndsUpInTheRenamedFile")]
  [TestCase("""{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 1 } }""", TestName = "RenameFolder_GivenAChildIsBeingWrittenWithCopiesOwedInTheBuffer_ThenEveryAcknowledgedWriteEndsUpInTheRenamedFile")]
  [Category("EdgeCase")]
  public void RenameFolder_GivenAChildIsBeingWritten_ThenEveryAcknowledgedWriteEndsUpInTheRenamedFile(string config) {
    using var fs = _Engine(config, out var members);
    var failures = new List<string>();
    var overlapped = 0;

    for (var trial = 0; trial < _TRIALS; ++trial) {
      var from = $"a{trial}";
      var to = $"b{trial}";
      fs.Create(from, NodeKind.Directory, CreateFlags.None);
      var seed = fs.Create($"{from}/x.bin", NodeKind.File, CreateFlags.None);
      fs.Write(seed, new byte[_BLOCK * _BLOCKS], 0, WriteMode.Normal);
      fs.Close(seed);

      var handle = fs.Open($"{from}/x.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write | ShareMode.Delete);
      var acknowledged = new bool[_BLOCKS];
      var started = new ManualResetEventSlim();
      var writer = new Thread(() => {
        for (var block = 0; block < _BLOCKS; ++block) {
          if (block == 4)
            started.Set();
          try {
            fs.Write(handle, _Block(trial, block), block * _BLOCK, WriteMode.Normal);
            acknowledged[block] = true;
          } catch (PoolFsException) {
            // a refused write is honest; only an ACKNOWLEDGED one must survive
          }
        }

        started.Set();
      });
      writer.Start();
      started.Wait();

      var renamed = true;
      try {
        fs.Rename(from, to, RenameFlags.None);
      } catch (PoolFsException) {
        renamed = false; // refusing is allowed; losing is not
      }

      if (renamed && writer.IsAlive)
        ++overlapped; // the rename landed while writes were still arriving: the case under test

      writer.Join();
      fs.Close(handle);
      fs.CreateScheduler().Quiesce();

      var final = renamed ? $"{to}/x.bin" : $"{from}/x.bin";
      var ghost = renamed ? from : to;
      var content = new byte[_BLOCK * _BLOCKS];
      var reader = fs.Open(final, AccessMode.Read, ShareMode.Read | ShareMode.Write | ShareMode.Delete);
      fs.Read(reader, content, 0);
      fs.Close(reader);

      for (var block = 0; block < _BLOCKS; ++block)
        if (acknowledged[block] && !content.AsSpan(block * _BLOCK, _BLOCK).SequenceEqual(_Block(trial, block)))
          failures.Add($"trial {trial}: acknowledged block {block} missing from '{final}'"
            + string.Concat(members.Select(m => $" | {m.DisplayName}: {(m.GetContent(final, false) is { } p ? "P " + p.AsSpan(block * _BLOCK, _BLOCK).SequenceEqual(_Block(trial, block)) : "")}{(m.GetContent(final, true) is { } sh ? " S " + sh.AsSpan(block * _BLOCK, _BLOCK).SequenceEqual(_Block(trial, block)) : "")}"))
            + $" | dirty: {string.Join(",", fs.WriteBuffer.DirtyPaths)}");

      foreach (var member in members)
        if (member.FolderExists(ghost, false) || member.FileExists($"{ghost}/x.bin", false))
          failures.Add($"trial {trial}: '{ghost}' reappeared on {member.DisplayName}");
    }

    TestContext.Out.WriteLine($"{overlapped} of {_TRIALS} renames landed while the child was still being written");
    overlapped.Should().BeGreaterThan(_TRIALS / 8, "the rename must genuinely cross the writes, or this proves nothing");
    failures.Should().BeEmpty("a write the pool acknowledged must be in the file under its final name");
  }

}
