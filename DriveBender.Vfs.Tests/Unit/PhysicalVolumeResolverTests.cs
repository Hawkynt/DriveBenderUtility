using DivisonM.Vfs;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// SAFE-PHYS on Linux: a path must resolve to its PHYSICAL DISK, not its partition, so two
/// partitions of one spindle share a failure domain — otherwise duplication happily places both
/// copies on one disk that a single failure takes out. Also covers the /proc/mounts parsing
/// gotchas (octal-escaped spaces, path-boundary false matches).
/// </summary>
[TestFixture]
[Category("Unit")]
public class PhysicalVolumeResolverTests {

  [Test]
  [Category("HappyPath")]
  public void MountSourceFor_GivenNestedMounts_WhenResolved_ThenLongestContainingMountWins() {
    var mounts = new[] {
      "/dev/sda1 / ext4 rw 0 0",
      "/dev/sdb1 /mnt/data ext4 rw 0 0",
      "/dev/sdc1 /mnt/database ext4 rw 0 0",
    };

    PhysicalVolumeResolver.MountSourceFor("/mnt/data/pool/file", mounts).Should().Be("/dev/sdb1");
    PhysicalVolumeResolver.MountSourceFor("/mnt/database/pool", mounts).Should().Be("/dev/sdc1");
  }

  [Test]
  [Category("EdgeCase")]
  public void MountSourceFor_GivenSiblingPrefix_WhenResolved_ThenNoBoundaryFalseMatch() {
    var mounts = new[] {
      "/dev/sda1 / ext4 rw 0 0",
      "/dev/sdb1 /mnt/data ext4 rw 0 0",
    };

    // '/mnt/database' must NOT match the '/mnt/data' mount — it falls back to root
    PhysicalVolumeResolver.MountSourceFor("/mnt/database/x", mounts).Should().Be("/dev/sda1");
  }

  [Test]
  [Category("EdgeCase")]
  public void MountSourceFor_GivenSpaceInMountTarget_WhenResolved_ThenOctalEscapeHandled() {
    var mounts = new[] { @"/dev/sdb1 /mnt/my\040disk ext4 rw 0 0", "/dev/sda1 / ext4 rw 0 0" };

    PhysicalVolumeResolver.MountSourceFor("/mnt/my disk/pool", mounts).Should().Be("/dev/sdb1");
  }

  [Test]
  [Category("HappyPath")]
  public void UnescapeProcField_GivenOctalEscapes_WhenUnescaped_ThenRealChars() {
    PhysicalVolumeResolver.UnescapeProcField(@"a\040b").Should().Be("a b");
    PhysicalVolumeResolver.UnescapeProcField(@"tab\011here").Should().Be("tab\there");
    PhysicalVolumeResolver.UnescapeProcField("nospecials").Should().Be("nospecials");
  }

  [Test]
  [Category("HappyPath")]
  public void StripPartitionSuffix_GivenVariousDevices_WhenStripped_ThenWholeDiskName() {
    PhysicalVolumeResolver.StripPartitionSuffix("sda1").Should().Be("sda");
    PhysicalVolumeResolver.StripPartitionSuffix("sdb15").Should().Be("sdb");
    PhysicalVolumeResolver.StripPartitionSuffix("vdc3").Should().Be("vdc");
    PhysicalVolumeResolver.StripPartitionSuffix("nvme0n1p2").Should().Be("nvme0n1");
    PhysicalVolumeResolver.StripPartitionSuffix("mmcblk0p1").Should().Be("mmcblk0");
    PhysicalVolumeResolver.StripPartitionSuffix("sda").Should().Be("sda", "a whole disk is returned unchanged");
    PhysicalVolumeResolver.StripPartitionSuffix("nvme0n1").Should().Be("nvme0n1");
  }

  [Test]
  [Category("HappyPath")]
  public void WholeDiskName_GivenPartitionViaSysfs_WhenResolved_ThenParentDiskFromSysfs() {
    // sysfs says sda1 is a partition and its parent disk is 'sda'
    bool Exists(string p) => p == "/sys/class/block/sda1/partition" || p == "/sys/class/block/sda1";
    string? Parent(string dev) => dev == "sda1" ? "sda" : null;

    PhysicalVolumeResolver.WholeDiskName("sda1", Exists, Parent).Should().Be("sda");
  }

  [Test]
  [Category("EdgeCase")]
  public void WholeDiskName_GivenNoSysfs_WhenResolved_ThenFallsBackToNamingConvention() {
    PhysicalVolumeResolver.WholeDiskName("nvme0n1p3", _ => false, _ => null).Should().Be("nvme0n1");
  }

  [Test]
  [Category("EdgeCase")]
  public void WholeDiskName_GivenWholeDiskViaSysfs_WhenResolved_ThenReturnedUnchanged() {
    // sysfs knows 'sda' but it has no 'partition' attribute → it IS the whole disk
    bool Exists(string p) => p == "/sys/class/block/sda";
    PhysicalVolumeResolver.WholeDiskName("sda", Exists, _ => "WRONG").Should().Be("sda");
  }
}
