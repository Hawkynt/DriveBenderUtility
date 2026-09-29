using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// A folder renamed while the landing-zone drainer is moving the files inside it. The drainer copies
/// without holding the file (so reads are never blocked by a slow move) and re-validates afterwards,
/// so the rename can land in the middle of any copy. Whatever it interrupts, every file must end up
/// whole under the new name, and the old name must not come back.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FolderRenameDrainRaceTests {

  private static readonly Guid _pool = Guid.Parse("57a6ed00-0000-0000-0000-000000000079");
  private const int _TRIALS = 30;
  private const int _FILES = 6;

  private static byte[] _Content(int trial, int file) => [.. Enumerable.Range(0, 20_000).Select(i => (byte)(trial * 31 + file * 7 + i))];

  [Test]
  [Category("EdgeCase")]
  public void RenameFolder_GivenTheDrainerIsMovingItsFiles_ThenEveryFileEndsUpWholeUnderTheNewNameAndTheOldNameStaysGone() {
    var ssd = new FakeVolumeIO(Guid.NewGuid(), "ssd", "PHYS-SSD", capacity: 1L << 26);
    var hdd1 = new FakeVolumeIO(Guid.NewGuid(), "hdd1", "PHYS-HDD1", capacity: 1L << 26);
    var hdd2 = new FakeVolumeIO(Guid.NewGuid(), "hdd2", "PHYS-HDD2", capacity: 1L << 26);
    FakeVolumeIO[] all = [ssd, hdd1, hdd2];
    var cache = new CacheInstance("dr" + Guid.NewGuid().ToString("N"), new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    using var fs = new PoolFileSystem(_pool, [new(ssd, MemberRole.Landing), new(hdd1), new(hdd2)], cache,
      ConfigResolver.ResolveEffective(null, """{ "duplication": 1, "write": { "policy": "write-through" }, "readAhead": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));

    var failures = new List<string>();
    var crossed = 0;
    for (var trial = 0; trial < _TRIALS; ++trial) {
      var from = $"d{trial}";
      var to = $"e{trial}";
      fs.Create(from, NodeKind.Directory, CreateFlags.None);
      for (var file = 0; file < _FILES; ++file) {
        var handle = fs.Create($"{from}/f{file}.bin", NodeKind.File, CreateFlags.None);
        fs.Write(handle, _Content(trial, file), 0, WriteMode.Normal);
        fs.Close(handle);
      }

      // rename the moment the drainer first touches a capacity disk: mid-move, by construction
      using var copying = new ManualResetEventSlim();
      foreach (var hdd in new[] { hdd1, hdd2 })
        hdd.BeforeOperation = (_, path) => {
          if (path.StartsWith(from + "/", StringComparison.Ordinal))
            copying.Set();
        };

      var drainer = new Thread(() => {
        try {
          while (fs.DrainOneLandingFile()) { }
        } catch (PoolFsException) {
          // a move the rename pulled the ground from under may fail; it must not lose anything
        }
      });
      drainer.Start();
      if (copying.Wait(TimeSpan.FromSeconds(5)))
        ++crossed;

      fs.Rename(from, to, RenameFlags.None);
      drainer.Join();
      foreach (var hdd in new[] { hdd1, hdd2 })
        hdd.BeforeOperation = null;

      while (fs.DrainOneLandingFile()) { }

      for (var file = 0; file < _FILES; ++file) {
        var path = $"{to}/f{file}.bin";
        var content = new byte[20_000];
        try {
          var reader = fs.Open(path, AccessMode.Read, ShareMode.Read);
          var read = fs.Read(reader, content, 0);
          fs.Close(reader);
          if (read != content.Length || !content.AsSpan().SequenceEqual(_Content(trial, file)))
            failures.Add($"trial {trial}: '{path}' is not whole");
        } catch (PoolFsException e) {
          failures.Add($"trial {trial}: '{path}' unreadable ({e.Error})");
        }
      }

      foreach (var member in all)
        if (member.FolderExists(from, false))
          failures.Add($"trial {trial}: the old folder '{from}' came back on {member.DisplayName}: "
                       + string.Join(", ", member.FilePaths.Where(p => p.StartsWith(from + "/", StringComparison.Ordinal))));
    }

    TestContext.Out.WriteLine($"{crossed} of {_TRIALS} renames landed while the drainer was mid-move");
    crossed.Should().BeGreaterThan(_TRIALS / 2, "the rename must genuinely cross a move, or this proves nothing");
    failures.Should().BeEmpty();
  }

}
