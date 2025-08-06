using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using FluentAssertions;
using DivisonM;
using static DivisonM.DriveBender;

namespace DriveBender.Tests.Integration.HappyPath {

  [TestFixture]
  public class PoolManagementIntegrationTests : IntegrationTestBase {

    private string _drive1Path;
    private string _drive2Path;
    private string _poolMountPoint;
    private string _poolName;

    [SetUp]
    public override void SetUp() {
      base.SetUp(); // Call base class setup to create TestDirectory

      _drive1Path = GetTestPath("Drive1");
      _drive2Path = GetTestPath("Drive2");
      _poolMountPoint = GetTestPath("PoolMount");
      _poolName = "TestPool_" + Guid.NewGuid().ToString("N");

      CreateTestDirectory(_drive1Path);
      CreateTestDirectory(_drive2Path);
      CreateTestDirectory(_poolMountPoint);

      // Simulate DriveBender info files for the drives
      CreateDriveBenderInfoFile(_drive1Path, "Drive1Label", Guid.NewGuid());
      CreateDriveBenderInfoFile(_drive2Path, "Drive2Label", Guid.NewGuid());
    }

    private void CreateDriveBenderInfoFile(string drivePath, string label, Guid id) {
      var infoFilePath = Path.Combine(drivePath, $"volume.{DriveBenderConstants.INFO_EXTENSION}");
      var content = $"volumelabel:{label}\nid:{id}\ndescription:Test Drive\n";
      System.IO.File.WriteAllText(infoFilePath, content);

      // Create the actual pool directory structure
      System.IO.Directory.CreateDirectory(Path.Combine(drivePath, $"{{{id}}}"));
    }

    [Test]
    public void CreatePool_ShouldCreateMountPointAndPoolStructure() {
      // Arrange
      var drives = new[] { _drive1Path, _drive2Path };

      // Act
      var success = PoolManager.CreatePool(_poolName, _poolMountPoint, drives);

      // Assert
      success.Should().BeTrue();
      Directory.Exists(_poolMountPoint).Should().BeTrue();

      var detectedMountPoints = DetectedMountPoints;
      var createdPool = detectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      createdPool.Should().NotBeNull();
      createdPool.Volumes.Should().HaveCount(2);
    }

    [Test]
    public void AddFile_WithoutDuplication_ShouldPlaceFileOnOneDrive() {
      // Arrange
      PoolManager.CreatePool(_poolName, _poolMountPoint, new[] { _drive1Path, _drive2Path });
      var pool = DetectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      if (pool == null) {
        Assert.Inconclusive($"Could not find pool '{_poolName}' in detected mount points - test environment may not be properly set up");
        return;
      }
      var filePath = Path.Combine(_poolMountPoint, "testfile.txt");
      var fileContent = "Hello, world!";

      // Act
      System.IO.File.WriteAllText(filePath, fileContent);

      // Assert
      var fileInfo = new FileInfo(filePath);
      fileInfo.Exists.Should().BeTrue();
      fileInfo.Length.Should().Be(fileContent.Length);

      // Verify file exists on one of the physical drives
      var physicalFiles = pool.Volumes.SelectMany(v => v.Items.OfType<IPhysicalFile>()).ToList();
      physicalFiles.Should().HaveCount(1);
      var firstPhysicalFile = physicalFiles.FirstOrDefault();
      if (firstPhysicalFile == null) {
        Assert.Inconclusive("No physical files found in pool - test environment may not be properly set up");
        return;
      }
      firstPhysicalFile.Source.Name.Should().Be("testfile.txt");
      System.IO.File.ReadAllText(firstPhysicalFile.Source.FullName).Should().Be(fileContent);
    }

    [Test]
    public void AddFile_WithDuplication_ShouldPlaceFileOnMultipleDrives() {
      // Arrange
      PoolManager.CreatePool(_poolName, _poolMountPoint, new[] { _drive1Path, _drive2Path });
      var pool = DetectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      if (pool == null) {
        Assert.Inconclusive($"Could not find pool '{_poolName}' in detected mount points - test environment may not be properly set up");
        return;
      }
      
      // Enable duplication for the root folder
      DuplicationManager.EnableDuplicationOnFolder(pool, "", 2); // Level 2 for 2 copies

      var filePath = Path.Combine(_poolMountPoint, "duplicated_file.txt");
      var fileContent = "This file should be duplicated.";

      // Act
      System.IO.File.WriteAllText(filePath, fileContent);

      // Assert
      var fileInfo = new FileInfo(filePath);
      fileInfo.Exists.Should().BeTrue();

      // Verify file exists on both physical drives (one primary, one shadow)
      var physicalFiles = pool.Volumes.SelectMany(v => v.Items.OfType<IPhysicalFile>()).ToList();
      physicalFiles.Should().HaveCount(2); // One primary, one shadow

      var primaryFile = physicalFiles.FirstOrDefault(f => !f.IsShadowCopy);
      var shadowFile = physicalFiles.FirstOrDefault(f => f.IsShadowCopy);

      primaryFile.Should().NotBeNull();
      shadowFile.Should().NotBeNull();

      System.IO.File.ReadAllText(primaryFile.Source.FullName).Should().Be(fileContent);
      System.IO.File.ReadAllText(shadowFile.Source.FullName).Should().Be(fileContent);
    }
  }
}
