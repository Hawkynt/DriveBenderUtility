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
  public class IntegrityCheckerTests : TestBase {
    
    private Mock<DivisonM.DriveBender.IMountPoint> _mockMountPoint;
    private Mock<DivisonM.DriveBender.IFile> _mockFile;
    private Mock<DivisonM.DriveBender.IVolume> _mockVolume;
    
    [SetUp]
    public override void SetUp() {
      _mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      _mockFile = new Mock<DivisonM.DriveBender.IFile>();
      _mockVolume = new Mock<DivisonM.DriveBender.IVolume>();
      
      _mockMountPoint.Setup(m => m.Name).Returns("TestPool");
      _mockFile.Setup(f => f.FullName).Returns("TestFile.txt");
      _mockFile.Setup(f => f.Size).Returns(1024);
      
      // Set up logger
      DivisonM.DriveBender.Logger = message => TestContext.WriteLine($"[LOG] {message}");
    }
    
    [Test]
    public void CheckPoolIntegrity_WithNullMountPoint_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentNullException>(() => 
        IntegrityChecker.CheckPoolIntegrity(null, false));
    }
    
    [Test]
    public void CheckPoolIntegrity_WithEmptyPool_ShouldReturnEmptyList() {
      // Arrange
      _mockMountPoint.Setup(m => m.GetItems(System.IO.SearchOption.AllDirectories))
                    .Returns(Enumerable.Empty<DivisonM.DriveBender.IFileSystemItem>());
      
      // Act
      var result = IntegrityChecker.CheckPoolIntegrity(_mockMountPoint.Object, false);
      
      // Assert
      result.Should().BeEmpty();
    }
    
    [Test]
    public void IntegrityIssue_Properties_ShouldBeSetCorrectly() {
      // Arrange & Act
      var issue = new IntegrityChecker.IntegrityIssue {
        FilePath = "test.txt",
        IssueType = IntegrityChecker.IntegrityIssueType.MissingPrimary,
        Description = "Test description",
        SuggestedAction = "Test action"
      };
      
      // Assert
      issue.FilePath.Should().Be("test.txt");
      issue.IssueType.Should().Be(IntegrityChecker.IntegrityIssueType.MissingPrimary);
      issue.Description.Should().Be("Test description");
      issue.SuggestedAction.Should().Be("Test action");
    }
    
    [Test]
    public void FileLocation_Properties_ShouldBeSetCorrectly() {
      // Arrange
      var testFile = new FileInfo(Path.GetTempFileName());
      
      // Act
      var location = new IntegrityChecker.FileLocation {
        Volume = _mockVolume.Object,
        FileInfo = testFile,
        IsShadowCopy = true,
        Hash = "testhash123"
      };
      
      // Assert
      location.Volume.Should().Be(_mockVolume.Object);
      location.FileInfo.Should().Be(testFile);
      location.IsShadowCopy.Should().BeTrue();
      location.Hash.Should().Be("testhash123");
      
      // Cleanup
      try { testFile.Delete(); } catch { }
    }
    
    [Test]
    public void IntegrityIssueType_ShouldHaveExpectedValues() {
      // Arrange & Act & Assert
      Enum.IsDefined(typeof(IntegrityChecker.IntegrityIssueType), IntegrityChecker.IntegrityIssueType.MissingPrimary).Should().BeTrue();
      Enum.IsDefined(typeof(IntegrityChecker.IntegrityIssueType), IntegrityChecker.IntegrityIssueType.MissingShadowCopy).Should().BeTrue();
      Enum.IsDefined(typeof(IntegrityChecker.IntegrityIssueType), IntegrityChecker.IntegrityIssueType.CorruptedFile).Should().BeTrue();
      Enum.IsDefined(typeof(IntegrityChecker.IntegrityIssueType), IntegrityChecker.IntegrityIssueType.HashMismatch).Should().BeTrue();
      Enum.IsDefined(typeof(IntegrityChecker.IntegrityIssueType), IntegrityChecker.IntegrityIssueType.DuplicatePrimary).Should().BeTrue();
      Enum.IsDefined(typeof(IntegrityChecker.IntegrityIssueType), IntegrityChecker.IntegrityIssueType.DuplicateShadowCopy).Should().BeTrue();
      Enum.IsDefined(typeof(IntegrityChecker.IntegrityIssueType), IntegrityChecker.IntegrityIssueType.OrphanedShadowCopy).Should().BeTrue();
      Enum.IsDefined(typeof(IntegrityChecker.IntegrityIssueType), IntegrityChecker.IntegrityIssueType.AccessDenied).Should().BeTrue();
    }
    
    [Test]
    public void RepairIntegrityIssue_WithNullIssue_ShouldReturnFalse() {
      // Act
      var result = IntegrityChecker.RepairIntegrityIssue(null);
      
      // Assert
      result.Should().BeFalse();
    }
    
    [Test]
    public void RepairIntegrityIssue_WithUnsupportedIssueType_ShouldReturnFalse() {
      // Arrange
      var issue = new IntegrityChecker.IntegrityIssue {
        FilePath = "test.txt",
        IssueType = IntegrityChecker.IntegrityIssueType.AccessDenied,
        Description = "Access denied"
      };
      
      // Act
      var result = IntegrityChecker.RepairIntegrityIssue(issue);
      
      // Assert
      result.Should().BeFalse();
    }
    
    [Test]
    public void CheckFileIntegrity_Integration_ShouldHandleRealFile() {
      // This would be an integration test that requires actual file system setup
      // For now, we'll just verify the method signature and basic null handling
      
      // Act & Assert - Should not throw for null input (though it may not behave as expected)
      Assert.DoesNotThrow(() => IntegrityChecker.CheckFileIntegrity(null, false));
    }
    
    [Test]
    public void IntegrityChecker_Constants_ShouldBeAccessible() {
      // This test ensures that the IntegrityChecker class and its nested types are properly accessible
      
      // Arrange & Act
      var issueTypes = Enum.GetValues(typeof(IntegrityChecker.IntegrityIssueType));
      
      // Assert
      issueTypes.Should().NotBeNull();
      issueTypes.Cast<object>().Should().NotBeEmpty();
      issueTypes.Length.Should().Be(8); // Expected number of issue types
    }
  }
}