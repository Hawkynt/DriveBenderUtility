using System;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;

namespace DriveBender.Tests {
  
  [TestFixture]
  public class DriveBenderCoreTests {
    
    [SetUp]
    public void SetUp() {
      // Set up logger for tests
      DriveBender.Logger = message => TestContext.WriteLine($"[LOG] {message}");
    }
    
    [Test]
    public void SizeFormatter_Format_ShouldFormatBytesCorrectly() {
      // Test various size formatting scenarios
      
      // Bytes
      DriveBender.SizeFormatter.Format(512).Should().Be("512B");
      DriveBender.SizeFormatter.Format(1023).Should().Be("1023B");
      
      // KiB
      DriveBender.SizeFormatter.Format(1024).Should().Be("1KiB");
      DriveBender.SizeFormatter.Format(2048).Should().Be("2KiB");
      DriveBender.SizeFormatter.Format(1536).Should().Be("1.5KiB");
      
      // MiB
      DriveBender.SizeFormatter.Format(1024 * 1024).Should().Be("1MiB");
      DriveBender.SizeFormatter.Format(1024 * 1024 * 2).Should().Be("2MiB");
      DriveBender.SizeFormatter.Format(1024 * 1024 + 512 * 1024).Should().Be("1.5MiB");
      
      // GiB
      DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024).Should().Be("1GiB");
      DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024 * 2).Should().Be("2GiB");
      
      // TiB
      DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024 * 1024).Should().Be("1TiB");
      
      // PiB
      DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024 * 1024 * 1024).Should().Be("1PiB");
      
      // EiB
      DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024 * 1024 * 1024 * 1024).Should().Be("1EiB");
    }
    
    [Test]
    public void SizeFormatter_Format_WithZero_ShouldReturn0B() {
      // Act & Assert
      DriveBender.SizeFormatter.Format(0).Should().Be("0B");
    }
    
    [Test]
    public void SizeFormatter_Format_WithLargeNumber_ShouldHandleCorrectly() {
      // Act & Assert
      DriveBender.SizeFormatter.Format(ulong.MaxValue).Should().EndWith("EiB");
    }
    
    [Test]
    public void DriveBenderConstants_ShouldHaveExpectedValues() {
      // Assert
      DriveBender.DriveBenderConstants.TEMP_EXTENSION.Should().Be("TEMP.$DRIVEBENDER");
      DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME.Should().Be("FOLDER.DUPLICATE.$DRIVEBENDER");
      DriveBender.DriveBenderConstants.INFO_EXTENSION.Should().Be("MP.$DRIVEBENDER");
    }
    
    [Test]
    public void DriveBender_Logger_ShouldBeSettable() {
      // Arrange
      var logMessages = new System.Collections.Generic.List<string>();
      Action<string> testLogger = message => logMessages.Add(message);
      
      // Act
      DriveBender.Logger = testLogger;
      DriveBender.Logger("Test message");
      
      // Assert
      logMessages.Should().HaveCount(1);
      logMessages[0].Should().Be("Test message");
    }
    
    [Test]
    public void DriveBender_Logger_WithNullLogger_ShouldUseDefault() {
      // Arrange & Act
      DriveBender.Logger = null;
      var logger = DriveBender.Logger;
      
      // Assert
      logger.Should().NotBeNull();
      // Should not throw when called
      Assert.DoesNotThrow(() => logger("Test message"));
    }
    
    [Test]
    public void DetectedMountPoints_ShouldReturnArray() {
      // Act
      var mountPoints = DriveBender.DetectedMountPoints;
      
      // Assert
      mountPoints.Should().NotBeNull();
      mountPoints.Should().BeOfType<IMountPoint[]>();
      // Note: In a test environment, this will likely be empty
    }
    
    [Test]
    public void DriveBenderExtensions_EnumerateFiles_WithEmptyCollection_ShouldReturnEmpty() {
      // Arrange
      var emptyCollection = Enumerable.Empty<DriveBender.IPhysicalFileSystemItem>();
      
      // Act
      var result = emptyCollection.EnumerateFiles();
      
      // Assert
      result.Should().BeEmpty();
    }
    
    [Test]
    public void DriveBenderExtensions_EnumerateFiles_WithSuppressExceptions_ShouldNotThrow() {
      // Arrange
      var emptyCollection = Enumerable.Empty<DriveBender.IPhysicalFileSystemItem>();
      
      // Act & Assert
      Assert.DoesNotThrow(() => {
        var result = emptyCollection.EnumerateFiles(true).ToArray();
      });
    }
    
    [Test]
    public void DriveBender_Interfaces_ShouldBeAccessible() {
      // This test ensures all public interfaces are accessible
      
      // Act & Assert
      typeof(IMountPoint).Should().NotBeNull();
      typeof(IVolume).Should().NotBeNull();
      typeof(DriveBender.IFile).Should().NotBeNull();
      typeof(DriveBender.IFolder).Should().NotBeNull();
      typeof(DriveBender.IFileSystemItem).Should().NotBeNull();
      typeof(DriveBender.IPhysicalFile).Should().NotBeNull();
      typeof(DriveBender.IPhysicalFolder).Should().NotBeNull();
      typeof(DriveBender.IPhysicalFileSystemItem).Should().NotBeNull();
    }
    
    [Test]
    public void SearchOption_AllDirectories_ShouldBeAccessible() {
      // This test ensures SearchOption enum is properly accessible
      
      // Act & Assert
      SearchOption.AllDirectories.Should().Be(SearchOption.AllDirectories);
      SearchOption.TopDirectoryOnly.Should().Be(SearchOption.TopDirectoryOnly);
    }
  }
}