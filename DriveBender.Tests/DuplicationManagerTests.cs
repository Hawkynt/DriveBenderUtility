using System;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;
using IMountPoint = DivisonM.DriveBender.IMountPoint;
using IVolume = DivisonM.DriveBender.IVolume;

namespace DriveBender.Tests {
  
  [TestFixture]
  public class DuplicationManagerTests {
    
    private Mock<DriveBender.IMountPoint> _mockMountPoint;
    private Mock<DriveBender.IVolume> _mockVolume1;
    private Mock<DriveBender.IVolume> _mockVolume2;
    private string _testDirectory;
    
    [SetUp]
    public void SetUp() {
      _testDirectory = Path.Combine(Path.GetTempPath(), $"DuplicationTest_{Guid.NewGuid():N}");
      Directory.CreateDirectory(_testDirectory);
      
      _mockMountPoint = new Mock<DriveBender.IMountPoint>();
      _mockVolume1 = new Mock<DriveBender.IVolume>();
      _mockVolume2 = new Mock<DriveBender.IVolume>();
      
      _mockMountPoint.Setup(m => m.Volumes).Returns(new[] { _mockVolume1.Object, _mockVolume2.Object });
      _mockMountPoint.Setup(m => m.Name).Returns("TestPool");
      
      // Set up logger
      DriveBender.Logger = message => TestContext.WriteLine($"[LOG] {message}");
    }
    
    [TearDown]
    public void TearDown() {
      try {
        if (Directory.Exists(_testDirectory)) {
          Directory.Delete(_testDirectory, true);
        }
      } catch {
        // Ignore cleanup errors
      }
    }
    
    [Test]
    public void EnableDuplicationOnFolder_WithNullMountPoint_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(null, "TestFolder", 1));
    }
    
    [Test]
    public void EnableDuplicationOnFolder_WithEmptyFolderPath_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, "", 1));
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, null, 1));
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, "   ", 1));
    }
    
    [Test]
    public void EnableDuplicationOnFolder_WithInvalidDuplicationLevel_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, "TestFolder", 0));
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, "TestFolder", -1));
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, "TestFolder", 10)); // More than volumes
    }
    
    [Test]
    public void DisableDuplicationOnFolder_WithNullMountPoint_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        DuplicationManager.DisableDuplicationOnFolder(null, "TestFolder"));
    }
    
    [Test]
    public void DisableDuplicationOnFolder_WithEmptyFolderPath_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.DisableDuplicationOnFolder(_mockMountPoint.Object, ""));
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.DisableDuplicationOnFolder(_mockMountPoint.Object, null));
      Assert.Throws<ArgumentException>(() => 
        DuplicationManager.DisableDuplicationOnFolder(_mockMountPoint.Object, "   "));
    }
    
    [Test]
    public void SetDuplicationLevel_WithZeroLevel_ShouldCallDisableDuplication() {
      // This test verifies the logic flow but will fail on actual folder operations
      // since we're using mocks
      
      // Arrange
      var folderPath = "TestFolder";
      
      // Act & Assert - Should not throw an exception, but will fail on folder operations
      Assert.DoesNotThrow(() => DuplicationManager.SetDuplicationLevel(_mockMountPoint.Object, folderPath, 0));
    }
    
    [Test]
    public void GetDuplicationLevel_WithNullMountPoint_ShouldReturnZero() {
      // Act
      var result = DuplicationManager.GetDuplicationLevel(null, "TestFolder");
      
      // Assert
      result.Should().Be(0);
    }
    
    [Test]
    public void GetDuplicationLevel_WithEmptyFolderPath_ShouldReturnZero() {
      // Act
      var result1 = DuplicationManager.GetDuplicationLevel(_mockMountPoint.Object, "");
      var result2 = DuplicationManager.GetDuplicationLevel(_mockMountPoint.Object, null);
      var result3 = DuplicationManager.GetDuplicationLevel(_mockMountPoint.Object, "   ");
      
      // Assert
      result1.Should().Be(0);
      result2.Should().Be(0);
      result3.Should().Be(0);
    }
    
    [Test]
    public void CreateAdditionalShadowCopy_WithNullFile_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        DuplicationManager.CreateAdditionalShadowCopy(null, _mockVolume1.Object));
    }
    
    [Test]
    public void CreateAdditionalShadowCopy_WithNullVolume_ShouldThrowException() {
      // Arrange
      var mockFile = new Mock<DriveBender.IFile>();
      
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        DuplicationManager.CreateAdditionalShadowCopy(mockFile.Object, null));
    }
  }
}