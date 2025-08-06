using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.Regression.HappyPath {
  
  [TestFixture]
  [Category("Regression")]
  [Category("HappyPath")]
  public class BackwardsCompatibilityTests : TestBase {
    
    [Test]
    public void LegacyStringAPI_ShouldStillWorkWithNewSemanticTypes() {
      // Ensure old string-based API calls still work alongside new semantic types
      
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mockVolume1 = new Mock<DivisonM.DriveBender.IVolume>();
      var mockVolume2 = new Mock<DivisonM.DriveBender.IVolume>();
      
      mockMountPoint.Setup(m => m.Name).Returns("LegacyPool");
      mockVolume1.Setup(v => v.Name).Returns("LegacyVolume1");
      mockVolume1.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(100));
      mockVolume2.Setup(v => v.Name).Returns("LegacyVolume2");
      mockVolume2.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(100));
      mockMountPoint.Setup(m => m.Volumes).Returns(new[] { mockVolume1.Object, mockVolume2.Object });
      
      // Act & Assert - Old string-based calls
      var legacyPoolName = "LegacyTestPool";
      var legacyMountPoint = "C:\\LegacyMount";
      var legacyDrives = new[] { "C:\\LegacyDrive1", "C:\\LegacyDrive2" };
      var legacyFolderPath = "Documents/Legacy";
      
      // These should not throw and should work with implicit conversions
      Assert.DoesNotThrow(() => {
        var result = PoolManager.CreatePool(legacyPoolName, legacyMountPoint, legacyDrives);
        // Result may be false due to non-existent paths, but API should work
      });
      
      Assert.DoesNotThrow(() => {
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, legacyFolderPath, 2);
      });
      
      // Act & Assert - Mixed usage (strings and semantic types)
      var semanticPoolName = new PoolName("SemanticPool");
      var semanticFolderPath = new FolderPath("Documents/Semantic");
      
      Assert.DoesNotThrow(() => {
        PoolManager.CreatePool(semanticPoolName, legacyMountPoint, legacyDrives);
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, semanticFolderPath, DuplicationLevel.Double);
      });
    }
    
    [Test]
    public void OriginalDriveBenderInterfaces_ShouldRemainUnchanged() {
      // Verify that original DriveBender interfaces haven't been broken
      
      // Arrange & Act - Test original interface methods still exist and work
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mockVolume = new Mock<DivisonM.DriveBender.IVolume>();
      var mockFile = new Mock<DivisonM.DriveBender.IFile>();
      
      // Setup original properties
      mockMountPoint.Setup(m => m.Name).Returns("OriginalPool");
      mockMountPoint.Setup(m => m.Volumes).Returns(new[] { mockVolume.Object });
      
      mockVolume.Setup(v => v.Name).Returns("OriginalVolume");
      mockVolume.Setup(v => v.BytesFree).Returns(1000000000UL); // Original ulong format
      
      mockFile.Setup(f => f.FullName).Returns("OriginalFile.txt");
      mockFile.Setup(f => f.Size).Returns(5000000UL); // Original ulong format
      mockFile.Setup(f => f.Primary).Returns(mockVolume.Object);
      mockFile.Setup(f => f.Primaries).Returns(new[] { mockVolume.Object });
      mockFile.Setup(f => f.ShadowCopies).Returns(Enumerable.Empty<DivisonM.DriveBender.IVolume>());
      
      // Assert - All original interface members should be accessible
      mockMountPoint.Object.Name.Should().Be("OriginalPool");
      mockMountPoint.Object.Volumes.Should().HaveCount(1);
      
      mockVolume.Object.Name.Should().Be("OriginalVolume");
      mockVolume.Object.BytesFree.Should().Be(1000000000UL);
      
      mockFile.Object.FullName.Should().Be("OriginalFile.txt");
      mockFile.Object.Size.Should().Be(5000000UL);
      mockFile.Object.Primary.Should().Be(mockVolume.Object);
      mockFile.Object.Primaries.Should().HaveCount(1);
      mockFile.Object.ShadowCopies.Should().BeEmpty();
    }
    
    [Test]
    public void OriginalPoolManagerMethods_ShouldMaintainSignatures() {
      // Verify that original PoolManager method signatures are preserved
      
      // Act & Assert - Original method signatures should still exist
      Assert.DoesNotThrow(() => {
        var result1 = PoolManager.CreatePool("TestPool", "C:\\Mount", new[] { "C:\\Drive1" });
        var result2 = PoolManager.AddDriveToPool("TestPool", "C:\\Drive2");
        var result3 = PoolManager.RemoveDriveFromPool("TestPool", "C:\\Drive1", true);
        var result4 = PoolManager.DeletePool("TestPool");
        
        // Results may be false due to non-existent resources, but methods should exist
        // Just verify that the methods exist and return bool values - no need for BeOfType on bool
      });
    }
    
    [Test]
    public void OriginalDuplicationManagerMethods_ShouldPreserveAPI() {
      // Verify that original DuplicationManager methods work as before
      
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mockVolume1 = new Mock<DivisonM.DriveBender.IVolume>();
      var mockVolume2 = new Mock<DivisonM.DriveBender.IVolume>();
      var mockVolume3 = new Mock<DivisonM.DriveBender.IVolume>();
      var mockFile = new Mock<DivisonM.DriveBender.IFile>();
      
      mockVolume1.Setup(v => v.Name).Returns("TestVolume1");
      mockVolume2.Setup(v => v.Name).Returns("TestVolume2");
      mockVolume3.Setup(v => v.Name).Returns("TestVolume3");
      mockFile.Setup(f => f.Primary).Returns(mockVolume1.Object);
      
      // Set up volumes for the mount point to allow duplication levels up to 3
      mockMountPoint.Setup(m => m.Volumes).Returns(new[] { mockVolume1.Object, mockVolume2.Object, mockVolume3.Object });
      
      // Act & Assert - Original methods should work with old int-based duplication
      Assert.DoesNotThrow(() => {
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, "TestFolder", 1);
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, "TestFolder", 2);
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, "TestFolder", 3);
        
        DuplicationManager.CreateAdditionalShadowCopy(mockFile.Object, mockVolume1.Object);
        DuplicationManager.DisableDuplicationOnFolder(mockMountPoint.Object, "TestFolder");
        
        var level = DuplicationManager.GetDuplicationLevel(mockMountPoint.Object, "TestFolder");
        // level should be an integer representing duplication level
      });
    }
    
    [Test]
    public void OriginalIntegrityCheckerMethods_ShouldMaintainBehavior() {
      // Verify that original IntegrityChecker methods work as expected
      
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mockFile = new Mock<DivisonM.DriveBender.IFile>();
      
      mockFile.Setup(f => f.FullName).Returns("TestFile.txt");
      mockFile.Setup(f => f.Size).Returns(1000000UL);
      
      // Act & Assert - Original methods should return expected types
      Assert.DoesNotThrow(() => {
        var poolIssues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
        var fileIssues = IntegrityChecker.CheckFileIntegrity(mockFile.Object, false);
        
        poolIssues.Should().NotBeNull();
        fileIssues.Should().NotBeNull();
        
        // If there are issues, repair method should work
        foreach (var issue in poolIssues.Take(1)) {
          var repairResult = IntegrityChecker.RepairIntegrityIssue(issue, true, true);
          // repairResult is already a bool, no need for BeOfType check
        }
      });
    }
    
    [Test]
    public void ByteSizeImplicitConversions_ShouldWorkWithOriginalUlongUsage() {
      // Verify that ByteSize works seamlessly with original ulong-based code
      
      // Arrange & Act
      ulong originalBytes = 1073741824; // 1 GB in bytes
      ByteSize semanticSize = originalBytes; // Implicit conversion
      ulong convertedBack = semanticSize; // Implicit conversion back
      
      // Assert
      convertedBack.Should().Be(originalBytes);
      semanticSize.Gigabytes.Should().BeApproximately(1.0, 0.01);
      
      // Original code patterns should still work
      var volumes = new List<Mock<DivisonM.DriveBender.IVolume>>();
      for (int i = 0; i < 3; i++) {
        var volume = new Mock<DivisonM.DriveBender.IVolume>();
        volume.Setup(v => v.BytesFree).Returns((ulong)(1000000000 + i * 500000000));
        volumes.Add(volume);
      }
      
      // Calculate total as done in original code
      ulong totalBytes = 0;
      foreach (var volume in volumes) {
        totalBytes += volume.Object.BytesFree;
      }
      
      // Should work with new ByteSize too
      var totalSize = ByteSize.FromBytes(totalBytes);
      totalSize.Gigabytes.Should().BeGreaterThan(2.0);
    }
    
    [Test]
    public void MixedAPIUsage_OldAndNew_ShouldCoexistProperly() {
      // Test that old and new API patterns can be used together
      
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mockVolume1 = new Mock<DivisonM.DriveBender.IVolume>();
      var mockVolume2 = new Mock<DivisonM.DriveBender.IVolume>();
      var mockVolume3 = new Mock<DivisonM.DriveBender.IVolume>();
      
      mockVolume1.Setup(v => v.Name).Returns("MixedVolume1");
      mockVolume1.Setup(v => v.BytesFree).Returns(2000000000UL); // Old format
      mockVolume2.Setup(v => v.Name).Returns("MixedVolume2");
      mockVolume2.Setup(v => v.BytesFree).Returns(2000000000UL);
      mockVolume3.Setup(v => v.Name).Returns("MixedVolume3");
      mockVolume3.Setup(v => v.BytesFree).Returns(2000000000UL);
      mockMountPoint.Setup(m => m.Volumes).Returns(new[] { mockVolume1.Object, mockVolume2.Object, mockVolume3.Object });
      
      // Act & Assert - Mix old strings with new semantic types
      var oldStylePath = "Documents/OldStyle";
      var newStylePath = new FolderPath("Documents/NewStyle");
      var oldStyleLevel = 2;
      var newStyleLevel = new DuplicationLevel(3);
      
      Assert.DoesNotThrow(() => {
        // Old API call
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, oldStylePath, oldStyleLevel);
        
        // New API call
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, newStylePath, newStyleLevel);
        
        // Mixed API call
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, new FolderPath(oldStylePath), newStyleLevel);
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, newStylePath, new DuplicationLevel(oldStyleLevel));
      });
      
      // Verify volume free space works with both old and new patterns
      ulong oldStyleFree = mockVolume1.Object.BytesFree;
      ByteSize newStyleFree = mockVolume1.Object.BytesFree;
      
      oldStyleFree.Should().Be(2000000000UL);
      newStyleFree.Bytes.Should().Be(2000000000UL);
      newStyleFree.Gigabytes.Should().BeApproximately(1.86, 0.1);
    }
    
    [Test]
    public void PoolConfiguration_LegacySettings_ShouldStillWork() {
      // Verify that pool configurations from older versions still work
      
      // Arrange - Simulate legacy pool configuration patterns
      var legacyPoolConfigs = new[] {
        new { Name = "Pool1", Mount = "C:\\Pool1", Drives = new[] { "D:\\", "E:\\" } },
        new { Name = "Pool2", Mount = "C:\\Pool2", Drives = new[] { "F:\\Data", "G:\\Backup" } },
        new { Name = "Pool3", Mount = "C:\\Pool3", Drives = new[] { "H:\\Primary" } }
      };
      
      // Act & Assert - Legacy configurations should be processed without issues
      foreach (var config in legacyPoolConfigs) {
        Assert.DoesNotThrow(() => {
          var result = PoolManager.CreatePool(config.Name, config.Mount, config.Drives);
          // Result may be false due to non-existent paths, but should not throw
          // result is already a bool, no need for BeOfType check
        });
      }
      
      // Verify legacy duplication patterns
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mockVol1 = new Mock<DivisonM.DriveBender.IVolume>();
      var mockVol2 = new Mock<DivisonM.DriveBender.IVolume>();
      mockMountPoint.Setup(m => m.Volumes).Returns(new[] { mockVol1.Object, mockVol2.Object });
      
      var legacyFolders = new[] { "Documents", "Pictures", "Videos", "Music" };
      var legacyLevels = new[] { 1, 2, 1, 2 };
      
      for (int i = 0; i < legacyFolders.Length; i++) {
        Assert.DoesNotThrow(() => 
          DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, legacyFolders[i], legacyLevels[i])
        );
      }
    }
  }
}