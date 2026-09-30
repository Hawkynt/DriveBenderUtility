using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Moving a file off the landing zone must be invisible to whoever is using it: a file opened and
/// written while the drainer moves it keeps working, and ends up holding what was written.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DrainTransparencyTests {

  private static readonly Guid _pool = Guid.Parse("d7a10000-0000-0000-0000-000000000001");

  [Test]
  [Category("EdgeCase")]
  public void Drain_GivenTheFileIsOpenedAndWrittenTheMomentItsStorageCopyIsPublished_ThenEveryWriteSucceedsAndLands() {
    var ssd = new FakeVolumeIO(Guid.NewGuid(), "ssd", "PHYS-SSD", capacity: 1L << 22);
    var hdd = new FakeVolumeIO(Guid.NewGuid(), "hdd", "PHYS-HDD", capacity: 1L << 22);
    var cache = new CacheInstance("dt" + Guid.NewGuid().ToString("N"), new() { Size = "1048576", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    using var fs = new PoolFileSystem(_pool, [new(ssd, MemberRole.Landing), new(hdd)], cache,
      ConfigResolver.ResolveEffective(null, """{ "duplication": 1, "readAhead": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));

    var original = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
    var handle = fs.Create("moving.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, original, 0, WriteMode.Normal);
    fs.Close(handle);
    ssd.FileExists("moving.bin", false).Should().BeTrue("it landed first");

    // The moment the storage copy appears under the real name, an application — on its own thread,
    // as applications are — opens the file, writes to it and reads it back. The read matters: it
    // resolves the file's copies AFTER the write and leaves that list cached, which is the list the
    // application's next write goes by.
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    NodeHandle? writer = null;
    Thread? application = null;
    var fired = 0;
    hdd.AfterOperation = (op, path) => {
      if (op != VolumeOp.AtomicReplace || path != "moving.bin" || Interlocked.Exchange(ref fired, 1) == 1)
        return;

      application = new Thread(() => {
        try {
          writer = fs.Open("moving.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
          fs.Write(writer.Value, [0xAA], 0, WriteMode.Normal);
          fs.Read(writer.Value, new byte[16], 0);
        } catch (PoolFsException e) {
          failures.Add($"during the move: {e.Error} {e.Message}");
        }
      });
      application.Start();
      application.Join(TimeSpan.FromMilliseconds(300)); // let it get in first, but never wait on it
    };

    fs.DrainOneLandingFile();
    hdd.AfterOperation = null;
    application?.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("the application's first steps finish");

    // the application carries on writing after the drainer has made its decision
    try {
      if (writer is { } open) {
        fs.Write(open, [0xBB], 1, WriteMode.Normal);
        fs.Close(open);
      }
    } catch (PoolFsException e) {
      failures.Add($"after the move: {e.Error} {e.Message}");
    }

    failures.Should().BeEmpty("tiering must be invisible to whoever is using the file");
    var expected = (byte[])original.Clone();
    expected[0] = 0xAA;
    expected[1] = 0xBB;

    var reader = fs.Open("moving.bin", AccessMode.Read, ShareMode.Read);
    var back = new byte[expected.Length];
    fs.Read(reader, back, 0);
    fs.Close(reader);
    back.Should().Equal(expected, "every write the application made is in the file");

    foreach (var volume in new[] { ssd, hdd })
      if (volume.GetContent("moving.bin", false) is { } copy)
        copy.Should().Equal(expected, $"the copy on '{volume.DisplayName}' is the file, not an older image of it");
  }

}
