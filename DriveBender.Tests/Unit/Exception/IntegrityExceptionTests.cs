using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.Unit.Exception {
  
  [TestFixture]
  [Category("Unit")]
  [Category("Exception")]
  public class IntegrityExceptionTests : TestBase {
    
    [Test]
    public void CheckPoolIntegrity_WithNullMountPoint_ShouldThrowArgumentNullException() {
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        IntegrityChecker.CheckPoolIntegrity(null, false, true));
    }
    
    [Test]
    public void DuplicationManager_EnableDuplication_WithNullMountPoint_ShouldThrowArgumentNullException() {
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(null, new FolderPath("test"), DuplicationLevel.Single));
    }
    
    [Test]
    public void DuplicationManager_EnableDuplication_WithEmptyFolderPath_ShouldThrowArgumentException() {
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      
      // Act & Assert
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, (string)"", 1));
    }
    
    [Test]
    public void DuplicationManager_CreateAdditionalShadowCopy_WithNullFile_ShouldThrowArgumentNullException() {
      // Arrange
      var mockVolume = new Mock<DivisonM.DriveBender.IVolume>();
      
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        DuplicationManager.CreateAdditionalShadowCopy(null, mockVolume.Object));
    }
    
    [Test]
    public void DuplicationManager_CreateAdditionalShadowCopy_WithNullVolume_ShouldThrowArgumentNullException() {
      // Arrange
      var mockFile = new Mock<DivisonM.DriveBender.IFile>();
      
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        DuplicationManager.CreateAdditionalShadowCopy(mockFile.Object, null));
    }
    
    [Test]
    public void PoolManager_CreatePool_WithNullPoolName_ShouldThrowArgumentException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => 
        PoolManager.CreatePool(null, "C:\\MountPoint", new[] { "C:\\Drive1" }));
    }
    
    [Test]
    public void PoolManager_CreatePool_WithEmptyDriveList_ShouldThrowArgumentException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => 
        PoolManager.CreatePool("TestPool", "C:\\MountPoint", new string[0]));
    }
    
    [Test]
    public void PoolManager_CreatePool_WithNullDriveList_ShouldThrowArgumentNullException() {
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        PoolManager.CreatePool("TestPool", "C:\\MountPoint", null));
    }
    
    [Test]
    public void IntegrityChecker_RepairIntegrityIssue_WithNullIssue_ShouldReturnFalse() {
      // Act
      var result = IntegrityChecker.RepairIntegrityIssue(null, true, true);
      
      // Assert
      result.Should().BeFalse();
    }
    
    [Test]
    public void IntegrityChecker_CheckFileIntegrity_WithNullFile_ShouldHandleGracefully() {
      // Act & Assert - Should not throw, but return empty or handle appropriately
      Assert.DoesNotThrow(() => IntegrityChecker.CheckFileIntegrity(null, false));
    }
    
    [Test]
    public void DataTypes_Constructor_ExceptionMessages_ShouldBeDescriptive() {
      // Act & Assert
      var poolNameException = Assert.Throws<ArgumentException>(() => new PoolName(""));
      var drivePathException = Assert.Throws<ArgumentException>(() => new DrivePath(""));
      var folderPathException = Assert.Throws<ArgumentException>(() => new FolderPath(""));
      var duplicationLevelException = Assert.Throws<ArgumentException>(() => new DuplicationLevel(-1));
      
      // Verify exception messages are descriptive
      poolNameException.Message.Should().Contain("Pool name");
      drivePathException.Message.Should().Contain("Drive path");
      folderPathException.Message.Should().Contain("Folder path");
      duplicationLevelException.Message.Should().Contain("Duplication level");
    }
    
    [Test]
    public void DrivePath_WithNonExistentDirectory_ShouldThrowDirectoryNotFoundException() {
      // Arrange
      var nonExistentPath = @"C:\NonExistent\Directory\Path";
      
      // Act & Assert
      var exception = Assert.Throws<System.IO.DirectoryNotFoundException>(() => new DrivePath(nonExistentPath));
      exception.Message.Should().Contain(nonExistentPath);
    }
    
    [Test]
    public void IntegrityChecker_WithCorruptedPoolStructure_ShouldHandleGracefully() {
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      mockMountPoint.Setup(m => m.GetItems(It.IsAny<System.IO.SearchOption>()))
                   .Throws(new System.IO.IOException("Simulated I/O error"));
      
      // Act & Assert
      Assert.DoesNotThrow(() => {
        var issues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
        // Should handle the exception and continue processing
      });
    }
    
    [Test]
    public void DuplicationManager_WithInsufficientSpace_ShouldHandleGracefully() {
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mockVolume = new Mock<DivisonM.DriveBender.IVolume>();
      mockVolume.Setup(v => v.BytesFree).Returns(ByteSize.FromBytes(0)); // No free space
      
      mockMountPoint.Setup(m => m.Volumes).Returns(new[] { mockVolume.Object });
      
      // Act & Assert - Should not throw, but handle gracefully
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, new FolderPath("test"), DuplicationLevel.Single));
    }
    
    [Test]
    public void PoolManager_WithAccessDeniedToDirectory_ShouldHandleGracefully() {
      // This test simulates scenarios where the application doesn't have
      // sufficient permissions to access certain directories
      
      // Act & Assert - Operations should fail gracefully, not crash
      Assert.DoesNotThrow(() => {
        var result = PoolManager.CreatePool("TestPool", "C:\\MountPoint", new[] { "C:\\Windows\\System32" });
        // Should return false rather than throw
        result.Should().BeFalse();
      });
    }
  }
}