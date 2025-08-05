using System;
using System.IO;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;

namespace DriveBender.Tests.Unit.EdgeCase {
  
  [TestFixture]
  [Category("Unit")]
  [Category("EdgeCase")]
  public class DataTypeValidationTests : TestBase {
    
    [Test]
    public void PoolName_WithEmptyString_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => new PoolName(""));
      Assert.Throws<ArgumentException>(() => new PoolName("   "));
      Assert.Throws<ArgumentException>(() => new PoolName(null));
    }
    
    [Test]
    public void PoolName_WithInvalidCharacters_ShouldThrowException() {
      // Arrange
      var invalidNames = new[] { "Pool<>Name", "Pool|Name", "Pool?Name", "Pool*Name" };
      
      // Act & Assert
      foreach (var invalidName in invalidNames) {
        Assert.Throws<ArgumentException>(() => new PoolName(invalidName), 
          $"Should throw for invalid name: {invalidName}");
      }
    }
    
    [Test]
    public void PoolName_WithVeryLongName_ShouldThrowException() {
      // Arrange
      var longName = new string('A', 256); // Exceeds 255 character limit
      
      // Act & Assert
      Assert.Throws<ArgumentException>(() => new PoolName(longName));
    }
    
    [Test]
    public void PoolName_WithMaximumLength_ShouldSucceed() {
      // Arrange
      var maxLengthName = new string('A', 255);
      
      // Act & Assert
      Assert.DoesNotThrow(() => new PoolName(maxLengthName));
    }
    
    [Test]
    public void DrivePath_WithNonExistentPath_ShouldThrowException() {
      // Arrange
      var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
      
      // Act & Assert
      Assert.Throws<DirectoryNotFoundException>(() => new DrivePath(nonExistentPath));
    }
    
    [Test]
    public void DrivePath_WithEmptyPath_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => new DrivePath(""));
      Assert.Throws<ArgumentException>(() => new DrivePath("   "));
      Assert.Throws<ArgumentException>(() => new DrivePath(null));
    }
    
    [Test]
    public void FolderPath_WithInvalidCharacters_ShouldThrowException() {
      // Arrange
      var invalidPaths = new[] { "Folder<>Name", "Folder|Name", "Folder?Name" };
      
      // Act & Assert
      foreach (var invalidPath in invalidPaths) {
        Assert.Throws<ArgumentException>(() => new FolderPath(invalidPath),
          $"Should throw for invalid path: {invalidPath}");
      }
    }
    
    [Test]
    public void FolderPath_WithEmptyPath_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => new FolderPath(""));
      Assert.Throws<ArgumentException>(() => new FolderPath("   "));
      Assert.Throws<ArgumentException>(() => new FolderPath(null));
    }
    
    [Test]
    public void FolderPath_WithVariousSeparators_ShouldNormalize() {
      // Arrange & Act
      var windowsPath = new FolderPath("Documents\\Projects\\MyProject");
      var unixPath = new FolderPath("Documents/Projects/MyProject");
      var mixedPath = new FolderPath("Documents\\Projects/MyProject");
      
      // Assert
      windowsPath.Value.Should().Be("Documents/Projects/MyProject");
      unixPath.Value.Should().Be("Documents/Projects/MyProject");
      mixedPath.Value.Should().Be("Documents/Projects/MyProject");
    }
    
    [Test]
    public void FolderPath_WithLeadingTrailingSeparators_ShouldTrim() {
      // Arrange & Act
      var pathWithSeparators = new FolderPath("/Documents/Projects/");
      
      // Assert
      pathWithSeparators.Value.Should().Be("Documents/Projects");
    }
    
    [Test]
    public void DuplicationLevel_WithNegativeValue_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => new DuplicationLevel(-1));
      Assert.Throws<ArgumentException>(() => new DuplicationLevel(-10));
    }
    
    [Test]
    public void DuplicationLevel_WithExcessiveValue_ShouldThrowException() {
      // Act & Assert
      Assert.Throws<ArgumentException>(() => new DuplicationLevel(11));
      Assert.Throws<ArgumentException>(() => new DuplicationLevel(100));
    }
    
    [Test]
    public void DuplicationLevel_WithBoundaryValues_ShouldSucceed() {
      // Act & Assert
      Assert.DoesNotThrow(() => new DuplicationLevel(0)); // Minimum
      Assert.DoesNotThrow(() => new DuplicationLevel(10)); // Maximum
    }
    
    [Test]
    public void ByteSize_WithZeroBytes_ShouldBeValid() {
      // Arrange & Act
      var zeroSize = new ByteSize(0);
      
      // Assert
      zeroSize.Bytes.Should().Be(0);
      zeroSize.ToHumanReadable().Should().Be("0B");
    }
    
    [Test]
    public void ByteSize_WithMaxValue_ShouldHandleCorrectly() {
      // Arrange & Act
      var maxSize = new ByteSize(ulong.MaxValue);
      
      // Assert
      maxSize.Bytes.Should().Be(ulong.MaxValue);
      maxSize.ToHumanReadable().Should().NotBeNullOrEmpty();
    }
    
    [Test]
    public void ByteSize_ArithmeticOperations_EdgeCases() {
      // Arrange
      var size1 = new ByteSize(ulong.MaxValue);
      var size2 = new ByteSize(1);
      
      // Act & Assert - Addition overflow should wrap around
      var overflowResult = size1 + size2;
      overflowResult.Bytes.Should().Be(0); // Overflow wraps to 0
      
      // Subtraction underflow
      var underflowResult = size2 - size1;
      underflowResult.Bytes.Should().Be(2); // Underflow wraps around
    }
    
    [Test]
    public void FolderPath_EmptySegments_ShouldBeHandledCorrectly() {
      // Arrange & Act
      var pathWithEmptySegments = new FolderPath("Documents//Projects///MyProject");
      
      // Assert
      pathWithEmptySegments.Segments.Should().Equal("Documents", "Projects", "MyProject");
      pathWithEmptySegments.Segments.Should().NotContain("");
    }
    
    [Test]
    public void DataTypes_EqualityComparisons_EdgeCases() {
      // Arrange
      var poolName1 = new PoolName("TestPool");
      var poolName2 = new PoolName("TESTPOOL"); // Different case
      var poolName3 = new PoolName("  TestPool  "); // With whitespace
      
      // Act & Assert
      poolName1.Should().Be(poolName2); // Case insensitive
      poolName1.Should().Be(poolName3); // Whitespace trimmed
    }
  }
}