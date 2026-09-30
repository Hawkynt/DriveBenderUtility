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

  [TestCase("""{ "duplication": 1 }""", TestName = "RenameFolder_GivenFilesAreBeingCreatedInItInAnUnduplicatedPool_ThenEachEndsUpUnderTheNewNameOrWasRefused")]
  [TestCase("""{ "duplication": 2 }""", TestName = "RenameFolder_GivenFilesAreBeingCreatedInItInADuplicatedPool_ThenEachEndsUpUnderTheNewNameOrWasRefused")]
  [Category("EdgeCase")]
  public void RenameFolder_GivenFilesAreBeingCreatedInIt_ThenEachEndsUpUnderTheNewNameOrWasRefused(string config) {
    // A copy into a folder while somebody renames it. Each file is created either BEFORE the rename
    // (so it moves with the folder) or AFTER it (so its parent is gone and the create is refused).
    // What must never happen is the third outcome: the old folder brought back to hold it.
    using var fs = _Engine(config, out var members);
    var failures = new List<string>();
    var overlapped = 0;

    for (var trial = 0; trial < _TRIALS; ++trial) {
      var from = $"c{trial}";
      var to = $"d{trial}";
      fs.Create(from, NodeKind.Directory, CreateFlags.None);

      var created = new bool[_BLOCKS];
      var started = new ManualResetEventSlim();
      var copier = new Thread(() => {
        for (var file = 0; file < _BLOCKS; ++file) {
          if (file == 4)
            started.Set();
          try {
            var handle = fs.Create($"{from}/f{file}.bin", NodeKind.File, CreateFlags.None);
            fs.Write(handle, _Block(trial, file), 0, WriteMode.Normal);
            fs.Close(handle);
            created[file] = true;
          } catch (PoolFsException) {
            // refused because the folder is gone: the honest answer to a create that lost the race
          }
        }

        started.Set();
      });
      copier.Start();
      started.Wait();
      fs.Rename(from, to, RenameFlags.None);
      if (copier.IsAlive)
        ++overlapped;
      copier.Join();
      fs.CreateScheduler().Quiesce();

      foreach (var member in members)
        if (member.FolderExists(from, false))
          failures.Add($"trial {trial}: '{from}' came back on {member.DisplayName} holding "
                       + string.Join(", ", member.FilePaths.Where(p => p.StartsWith(from + "/", StringComparison.Ordinal))));

      for (var file = 0; file < _BLOCKS; ++file) {
        if (!created[file])
          continue;

        var path = $"{to}/f{file}.bin";
        var content = new byte[_BLOCK];
        try {
          var reader = fs.Open(path, AccessMode.Read, ShareMode.Read);
          fs.Read(reader, content, 0);
          fs.Close(reader);
          if (!content.AsSpan().SequenceEqual(_Block(trial, file)))
            failures.Add($"trial {trial}: created '{path}' is not whole");
        } catch (PoolFsException e) {
          failures.Add($"trial {trial}: created 'f{file}.bin' is not under '{to}' ({e.Error})");
        }
      }
    }

    TestContext.Out.WriteLine($"{overlapped} of {_TRIALS} renames landed while files were still being created");
    overlapped.Should().BeGreaterThan(_TRIALS / 8, "the rename must genuinely cross the creates, or this proves nothing");
    failures.Should().BeEmpty("a created file moves with its folder or was refused — the old folder never comes back");
  }

  [Test]
  [Category("EdgeCase")]
  public void RenameFolder_GivenTheApplicationClosedAChildThatIsBeingPublished_ThenTheFileIsPublishedUnderTheNewName() {
    // "The application closed it" is where a new file is published on Windows (CLEANUP arrives long
    // before the kernel's CLOSE). The publish takes the file off the staging list BEFORE it renames
    // the temp on each member, so a folder rename landing in between saw no staged child, moved the
    // temp with the folder, and left the publish renaming a temp that was no longer there: the file
    // stayed a temp under the new folder, which is exactly what recovery deletes at the next mount.
    using var fs = _Engine("""{ "duplication": 1 }""", out var members);
    var content = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
    fs.MakeDir("a");
    var handle = fs.Create("a/x.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, content, 0, WriteMode.Normal);

    var publishing = new ManualResetEventSlim();
    var resume = new ManualResetEventSlim();
    Thread? closer = null;
    foreach (var member in members)
      member.BeforeOperation = (op, path) => {
        if (op == VolumeOp.AtomicReplace && path == "a/x.bin" && Thread.CurrentThread == closer) {
          publishing.Set();
          resume.Wait(TimeSpan.FromSeconds(10));
        }
      };

    Exception? closeFailure = null;
    closer = new(() => {
      try {
        fs.MarkApplicationClosed(handle);
      } catch (Exception e) {
        closeFailure = e;
      }
    });
    closer.Start();
    publishing.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the close must publish the file, or this tests nothing");

    var renamer = new Thread(() => fs.Rename("a", "b", RenameFlags.None));
    renamer.Start();
    renamer.Join(TimeSpan.FromMilliseconds(500)); // lands inside the publish, unless the publish holds it off
    resume.Set();
    closer.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
    renamer.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
    foreach (var member in members)
      member.BeforeOperation = null;
    fs.Close(handle);

    closeFailure.Should().BeNull("closing a file the application has finished with must not fail");
    members.Any(m => m.FileExists("b/x.bin", false)).Should().BeTrue("the file must be published under its folder's new name");
    members.Any(m => m.FileExists("b/x.bin." + DriveBender.DriveBenderConstants.TEMP_EXTENSION, false)).Should().BeFalse(
      "a temp left behind is deleted by recovery at the next mount, and with it the file");
    var reader = fs.Open("b/x.bin", AccessMode.Read, ShareMode.Read);
    var read = new byte[content.Length];
    fs.Read(reader, read, 0);
    fs.Close(reader);
    read.Should().Equal(content);
  }

}
