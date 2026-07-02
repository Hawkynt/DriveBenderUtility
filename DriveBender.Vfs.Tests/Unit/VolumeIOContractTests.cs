using System.Text;
using DivisonM.Vfs;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Behavioural contract every <see cref="IVolumeIO"/> backend must satisfy, run against
/// both the in-memory fake and the real local-disk backend so the fake stays truthful
/// (TST-FAKE).
/// </summary>
public abstract class VolumeIOContractTests {

  protected abstract IVolumeIO CreateVolume();

  protected IVolumeIO Volume = null!;

  [SetUp]
  public void SetUpVolume() => this.Volume = this.CreateVolume();

  private void _WriteAll(string path, bool shadow, byte[] content) {
    using var stream = this.Volume.OpenWrite(path, shadow, true);
    stream.Write(content, 0, content.Length);
    stream.Flush();
  }

  private byte[] _ReadAll(string path, bool shadow) {
    using var stream = this.Volume.OpenRead(path, shadow);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  [Test]
  [Category("Unit")]
  [Category("HappyPath")]
  public void WriteRead_GivenNestedFile_WhenRoundTripped_ThenContentIdentical() {
    var content = Encoding.UTF8.GetBytes("hello pool");
    this._WriteAll("Documents/hello.txt", false, content);
    this._ReadAll("Documents/hello.txt", false).Should().Equal(content);
  }

  [Test]
  [Category("Unit")]
  [Category("HappyPath")]
  public void WriteRead_GivenShadowCopy_WhenRoundTripped_ThenIndependentOfPrimary() {
    this._WriteAll("Documents/f.txt", false, [1, 2, 3]);
    this._WriteAll("Documents/f.txt", true, [9, 9]);

    this._ReadAll("Documents/f.txt", false).Should().Equal(1, 2, 3);
    this._ReadAll("Documents/f.txt", true).Should().Equal(9, 9);
  }

  [Test]
  [Category("Unit")]
  [Category("Exception")]
  public void OpenRead_GivenMissingFile_WhenOpened_ThenNotFound() {
    var act = () => this.Volume.OpenRead("missing.bin", false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("Unit")]
  [Category("Exception")]
  public void OpenWrite_GivenMissingFileWithoutCreate_WhenOpened_ThenNotFound() {
    var act = () => this.Volume.OpenWrite("missing.bin", false, false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("Unit")]
  [Category("HappyPath")]
  public void AtomicReplace_GivenStagedTemp_WhenReplaced_ThenOnlyFinalNameRemains() {
    var temp = "movie.mkv." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
    this._WriteAll(temp, false, [7, 7, 7]);

    this.Volume.AtomicReplace(temp, "movie.mkv", false);

    this.Volume.FileExists("movie.mkv", false).Should().BeTrue();
    this.Volume.FileExists(temp, false).Should().BeFalse();
    this._ReadAll("movie.mkv", false).Should().Equal(7, 7, 7);
  }

  [Test]
  [Category("Unit")]
  [Category("EdgeCase")]
  public void AtomicReplace_GivenExistingTarget_WhenReplaced_ThenContentSwapped() {
    this._WriteAll("doc.txt", false, [1]);
    var temp = "doc.txt." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
    this._WriteAll(temp, false, [2]);

    this.Volume.AtomicReplace(temp, "doc.txt", false);

    this._ReadAll("doc.txt", false).Should().Equal(2);
  }

  [Test]
  [Category("Unit")]
  [Category("Exception")]
  public void AtomicReplace_GivenMissingTemp_WhenReplaced_ThenNotFound() {
    var act = () => this.Volume.AtomicReplace("nope.tmp", "target.txt", false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("Unit")]
  [Category("HappyPath")]
  public void Truncate_GivenGrowAndShrink_WhenApplied_ThenLengthAndContentCorrect() {
    this._WriteAll("data.bin", false, [1, 2, 3, 4]);

    this.Volume.Truncate("data.bin", false, 2);
    this._ReadAll("data.bin", false).Should().Equal(1, 2);

    this.Volume.Truncate("data.bin", false, 4);
    this._ReadAll("data.bin", false).Should().Equal(1, 2, 0, 0);
  }

  [Test]
  [Category("Unit")]
  [Category("HappyPath")]
  public void Delete_GivenExistingFile_WhenDeleted_ThenGone() {
    this._WriteAll("gone.txt", false, [1]);
    this.Volume.Delete("gone.txt", false);
    this.Volume.FileExists("gone.txt", false).Should().BeFalse();
  }

  [Test]
  [Category("Unit")]
  [Category("Exception")]
  public void Delete_GivenMissingFile_WhenDeleted_ThenNotFound() {
    var act = () => this.Volume.Delete("missing.txt", false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("Unit")]
  [Category("HappyPath")]
  public void Stat_GivenFile_WhenQueried_ThenLengthMatches() {
    this._WriteAll("stat.bin", false, new byte[42]);
    var meta = this.Volume.Stat("stat.bin", false);
    meta.Should().NotBeNull();
    meta!.Value.Length.Should().Be(42);
    meta.Value.IsDirectory.Should().BeFalse();
  }

  [Test]
  [Category("Unit")]
  [Category("EdgeCase")]
  public void Stat_GivenMissingPath_WhenQueried_ThenNull()
    => this.Volume.Stat("nothing.bin", false).Should().BeNull();

  [Test]
  [Category("Unit")]
  [Category("HappyPath")]
  public void List_GivenFilesAndFolders_WhenListed_ThenAllEntriesReturned() {
    this._WriteAll("dir/a.txt", false, [1]);
    this._WriteAll("dir/b.txt", false, [1, 2]);
    this.Volume.EnsureFolder("dir/sub", false);

    var entries = this.Volume.List("dir", false).ToArray();

    entries.Select(e => e.Name).Should().BeEquivalentTo("a.txt", "b.txt", "sub");
    entries.Single(e => e.Name == "sub").IsDirectory.Should().BeTrue();
    entries.Single(e => e.Name == "b.txt").Length.Should().Be(2);
  }

  [Test]
  [Category("Unit")]
  [Category("Exception")]
  public void List_GivenMissingFolder_WhenListed_ThenNotFound() {
    var act = () => this.Volume.List("missing-dir", false).ToArray();
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("Unit")]
  [Category("HappyPath")]
  public void EnsureFolder_GivenShadow_WhenCreated_ThenShadowFolderExists() {
    this.Volume.EnsureFolder("Documents", true);
    this.Volume.FolderExists("Documents", true).Should().BeTrue();
  }

}

[TestFixture]
public sealed class FakeVolumeIOContractTests : VolumeIOContractTests {
  protected override IVolumeIO CreateVolume() => new FakeVolumeIO(Guid.NewGuid(), "fake", "VOL-FAKE");
}

[TestFixture]
public sealed class LocalVolumeIOContractTests : VolumeIOContractTests {

  private string _root = null!;

  protected override IVolumeIO CreateVolume() {
    this._root = Path.Combine(Path.GetTempPath(), "dbvfs-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
    return new LocalVolumeIO(Guid.NewGuid(), "local", this._root, "VOL-TEMP");
  }

  [TearDown]
  public void Cleanup() {
    if (Directory.Exists(this._root))
      Directory.Delete(this._root, true);
  }

}
