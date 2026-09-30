using DivisonM.Vfs;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The space-saving primitives of a local member, against the real filesystem of the temp folder
/// (NTFS on Windows, usually ext4 on Linux). A capability the filesystem lacks is skipped, not
/// failed — but one it claims must work.
/// </summary>
[TestFixture]
[Category("Unit")]
public class LocalSpaceSavingTests {

  private string _root = "";
  private LocalVolumeIO _volume = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "dbspace-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
    this._volume = new(Guid.NewGuid(), "local", this._root, "PHYS-LOCAL");
  }

  [TearDown]
  public void TearDown() {
    this._volume.ReleaseIdleResources();
    try {
      Directory.Delete(this._root, true);
    } catch (IOException) {
    }
  }

  private void _Require(BackendCaps capability) {
    if ((this._volume.Caps & capability) == 0)
      Assert.Ignore($"The temp folder's filesystem does not support {capability}");
  }

  private void _Write(string relative, byte[] content) {
    using var stream = this._volume.OpenWrite(relative, false, true);
    stream.Write(content);
    stream.Flush();
  }

  private byte[] _Read(string relative) {
    using var stream = this._volume.OpenRead(relative, false);
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  [Test]
  [Category("HappyPath")]
  public void Capabilities_OnWindows_ThenNtfsReportsHardLinksAndSparse() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("Windows only");

    var fileSystem = new DriveInfo(Path.GetPathRoot(this._root)!).DriveFormat;
    if (!fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
      Assert.Ignore($"The temp folder is on {fileSystem}, not NTFS");

    this._volume.Caps.Should().HaveFlag(BackendCaps.HardLinks).And.HaveFlag(BackendCaps.Sparse);
    this._volume.Caps.Should().NotHaveFlag(BackendCaps.BlockClone, "NTFS has no block cloning — that is ReFS");
  }

  [Test]
  [Category("HappyPath")]
  public void HardLink_GivenAFile_ThenBothNamesShareItsContentAndCountTwo() {
    this._Require(BackendCaps.HardLinks);
    this._Write("a.bin", [1, 2, 3]);

    this._volume.TryHardLink("a.bin", false, "sub/b.bin", false).Should().BeTrue();

    this._Read("sub/b.bin").Should().Equal(1, 2, 3);
    this._volume.LinkCount("a.bin", false).Should().Be(2);
    this._volume.LinkCount("sub/b.bin", false).Should().Be(2);
  }

  [Test]
  [Category("EdgeCase")]
  public void LinkCount_GivenAnOrdinaryFile_ThenItIsOne() {
    this._Write("single.bin", [9]);
    this._volume.LinkCount("single.bin", false).Should().Be(1);
  }

  [Test]
  [Category("HappyPath")]
  public void PunchHole_GivenARunOfZeros_ThenTheContentIsKeptAndLessIsAllocated() {
    this._Require(BackendCaps.Sparse);
    var content = new byte[4 * 1024 * 1024];
    content[0] = 7;
    content[^1] = 9;
    this._Write("zeros.bin", content);
    var before = this._volume.AllocatedBytes("zeros.bin", false);

    this._volume.TryPunchHole("zeros.bin", false, 1024 * 1024, 2 * 1024 * 1024).Should().BeTrue();

    this._Read("zeros.bin").Should().Equal(content, "a hole reads as the zeros it replaced");
    this._volume.Stat("zeros.bin", false)!.Value.Length.Should().Be(content.Length, "the size is unchanged");
    var after = this._volume.AllocatedBytes("zeros.bin", false);
    if (before > 0 && after >= 0)
      after.Should().BeLessThan(before, "the released range no longer occupies the disk");
  }

  [Test]
  [Category("EdgeCase")]
  public void Clone_WhereTheFilesystemCannot_ThenItDeclinesAndLeavesNothingBehind() {
    if ((this._volume.Caps & BackendCaps.BlockClone) != 0)
      Assert.Ignore("This filesystem clones; the declining path is not reachable here");

    this._Write("src.bin", [4, 5]);
    this._volume.TryClone("src.bin", false, "dst.bin", false).Should().BeFalse();
    this._volume.FileExists("dst.bin", false).Should().BeFalse();
  }

  [Test]
  [Category("HappyPath")]
  public void Clone_WhereTheFilesystemCan_ThenTheCloneHoldsTheSameBytesAndIsIndependent() {
    this._Require(BackendCaps.BlockClone);
    var content = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
    this._Write("src.bin", content);

    this._volume.TryClone("src.bin", false, "dst.bin", false).Should().BeTrue();
    this._Read("dst.bin").Should().Equal(content);

    this._Write("dst.bin", [0xFF]);
    this._Read("src.bin")[0].Should().Be(0, "writing the clone leaves the source alone");
  }

}
