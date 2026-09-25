using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Stamping a FOLDER's times and mode.
///
/// Reported from real use: copying a tree into the pool with Explorer failed over and over with
/// "directory does not exist". Explorer, robocopy, <c>cp -a</c> and <c>rsync -a</c> all create a
/// folder and then stamp it with the source folder's times — and <see cref="PoolFileSystem.SetAttributes"/>
/// looked the path up among FILE copies only, found none for a folder, and answered NotFound about a
/// folder that had just been created. robocopy reproduced it on its first folder.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FolderAttributesTests {

  private FakeVolumeIO _volume1 = null!;
  private FakeVolumeIO _volume2 = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._volume1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._volume2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    var cache = new CacheInstance("test", new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "1m" });
    this._fs = new(Guid.NewGuid(), [new(this._volume1), new(this._volume2)], cache, ConfigResolver.ResolveEffective(null, null));
    this._fs.Mount(new(@"X:\"));
  }

  [Test]
  [Category("HappyPath")]
  public void SetAttributes_GivenAFolderJustCreated_WhenStamped_ThenItSucceedsAndTheFolderCarriesTheTimes() {
    this._fs.MakeDir("copied");
    var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var modified = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    var act = () => this._fs.SetAttributes("copied", new() { CreationTimeUtc = created, LastWriteTimeUtc = modified });

    act.Should().NotThrow("a copy engine stamps every folder it creates, and refusing that fails the whole copy");
    var stamped = new[] { this._volume1, this._volume2 }.Where(v => v.FolderExists("copied", false)).ToArray();
    stamped.Should().NotBeEmpty();
    foreach (var volume in stamped)
      volume.FolderStamp("copied").Should().Be((created, modified), $"the folder on '{volume.DisplayName}' must carry the stamp");
  }

  [Test]
  [Category("EdgeCase")]
  public void SetAttributes_GivenAFolderPresentOnEveryMember_WhenStamped_ThenEveryCopyOfTheFolderIsStamped() {
    // a folder lives on every member that holds something under it; stamping one leaves the listing
    // reporting whichever member it happens to enumerate first
    this._volume1.EnsureFolder("shared", false);
    this._volume2.EnsureFolder("shared", false);
    var modified = new DateTime(2022, 2, 2, 2, 2, 2, DateTimeKind.Utc);

    this._fs.SetAttributes("shared", new() { LastWriteTimeUtc = modified });

    this._volume1.FolderStamp("shared").modified.Should().Be(modified);
    this._volume2.FolderStamp("shared").modified.Should().Be(modified);
  }

  [Test]
  [Category("Exception")]
  public void SetAttributes_GivenNeitherAFileNorAFolder_WhenStamped_ThenItIsStillNotFound() {
    // the fallback must not turn "does not exist" into a silent success
    var act = () => this._fs.SetAttributes("nowhere", new() { LastWriteTimeUtc = DateTime.UtcNow });

    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("HappyPath")]
  public void SetAttributes_GivenAFile_WhenStamped_ThenTheFilePathIsUnchanged() {
    this._volume1.Seed("docs/a.txt", false, [1, 2, 3]);
    var modified = new DateTime(2023, 3, 3, 3, 3, 3, DateTimeKind.Utc);

    this._fs.SetAttributes("docs/a.txt", new() { LastWriteTimeUtc = modified });

    this._fs.GetAttributes("docs/a.txt").LastWriteTimeUtc.Should().Be(modified);
    this._volume1.FolderStamp("docs").modified.Should().BeNull("stamping a file must not stamp its parent folder");
  }

}
