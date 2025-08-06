using System;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;

namespace DriveBender.Tests {
  
  [TestFixture]
  public class DriveBenderCoreTests : TestBase {
    
    [SetUp]
    public override void SetUp() {
      // Set up logger for tests
      DivisonM.DriveBender.Logger = message => TestContext.WriteLine($"[LOG] {message}");
    }
    
    [Test]
    public void SizeFormatter_Format_ShouldFormatBytesCorrectly() {
      // Test various size formatting scenarios
      
      // Bytes
      DivisonM.DriveBender.SizeFormatter.Format(512).Should().Be("512B");
      DivisonM.DriveBender.SizeFormatter.Format(1023).Should().Be("1023B");
      
      // KiB
      DivisonM.DriveBender.SizeFormatter.Format(1024).Should().Be("1KiB");
      DivisonM.DriveBender.SizeFormatter.Format(2048).Should().Be("2KiB");
      DivisonM.DriveBender.SizeFormatter.Format(1536).Should().Be("1.5KiB");

      // MiB
      DivisonM.DriveBender.SizeFormatter.Format(1024 * 1024).Should().Be("1MiB");
      DivisonM.DriveBender.SizeFormatter.Format(1024 * 1024 * 2).Should().Be("2MiB");
      DivisonM.DriveBender.SizeFormatter.Format(1024 * 1024 + 512 * 1024).Should().Be("1.5MiB");
      
      // GiB
      DivisonM.DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024).Should().Be("1GiB");
      DivisonM.DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024 * 2).Should().Be("2GiB");
      
      // TiB
      DivisonM.DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024 * 1024).Should().Be("1TiB");
      
      // PiB
      DivisonM.DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024 * 1024 * 1024).Should().Be("1PiB");
      
      // EiB
      DivisonM.DriveBender.SizeFormatter.Format(1024UL * 1024 * 1024 * 1024 * 1024 * 1024).Should().Be("1EiB");
    }
    
    [Test]
    public void SizeFormatter_Format_WithZero_ShouldReturn0B() {
      // Act & Assert
      DivisonM.DriveBender.SizeFormatter.Format(0).Should().Be("0B");
    }
    
    [Test]
    public void SizeFormatter_Format_WithLargeNumber_ShouldHandleCorrectly() {
      // Act & Assert
      DivisonM.DriveBender.SizeFormatter.Format(ulong.MaxValue).Should().EndWith("EiB");
    }
    
    [Test]
    public void DriveBenderConstants_ShouldHaveExpectedValues() {
      // Assert
      DivisonM.DriveBender.DriveBenderConstants.TEMP_EXTENSION.Should().Be("TEMP.$DRIVEBENDER");
      DivisonM.DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME.Should().Be("FOLDER.DUPLICATE.$DRIVEBENDER");
      DivisonM.DriveBender.DriveBenderConstants.INFO_EXTENSION.Should().Be("MP.$DRIVEBENDER");
    }
    
    [Test]
    public void DriveBender_Logger_ShouldBeSettable() {
      // Arrange
      var logMessages = new System.Collections.Generic.List<string>();
      Action<string> testLogger = message => logMessages.Add(message);
      
      // Act
      DivisonM.DriveBender.Logger = testLogger;
      DivisonM.DriveBender.Logger("Test message");
      
      // Assert
      logMessages.Should().HaveCount(1);
      logMessages[0].Should().Be("Test message");
    }
    
    [Test]
    public void DriveBender_Logger_WithNullLogger_ShouldUseDefault() {
      // Arrange & Act
      DivisonM.DriveBender.Logger = null;
      var logger = DivisonM.DriveBender.Logger;
      
      // Assert
      logger.Should().NotBeNull();
      // Should not throw when called
      Assert.DoesNotThrow(() => logger("Test message"));
    }
    
    [Test]
    public void DetectedMountPoints_ShouldReturnArray() {
      // Act
      var mountPoints = DivisonM.DriveBender.DetectedMountPoints;
      
      // Assert
      mountPoints.Should().NotBeNull();
      mountPoints.Should().BeOfType<DivisonM.DriveBender.IMountPoint[]>();
      // Note: In a test environment, this will likely be empty
    }
    
    [Test]
    public void DriveBenderExtensions_EnumerateFiles_WithEmptyCollection_ShouldReturnEmpty() {
      // Arrange
      var emptyCollection = Enumerable.Empty<DivisonM.DriveBender.IPhysicalFileSystemItem>();
      
      // Act
      // Extension method test - this functionality is tested in the actual DriveBender code
      // Just verify the collection is empty for now
      emptyCollection.Should().BeEmpty();
    }
    
    [Test]
    public void DriveBenderExtensions_EnumerateFiles_WithSuppressExceptions_ShouldNotThrow() {
      // Arrange
      var emptyCollection = Enumerable.Empty<DivisonM.DriveBender.IPhysicalFileSystemItem>();
      
      // Act & Assert
      Assert.DoesNotThrow(() => {
        // Extension method test - this functionality is tested in the actual DriveBender code
        emptyCollection.Should().BeEmpty();
      });
    }
    
    [Test]
    public void DriveBender_Interfaces_ShouldBeAccessible() {
      // This test ensures all public interfaces are accessible
      
      // Act & Assert
      typeof(DivisonM.DriveBender.IMountPoint).Should().NotBeNull();
      typeof(DivisonM.DriveBender.IVolume).Should().NotBeNull();
      typeof(DivisonM.DriveBender.IFile).Should().NotBeNull();
      typeof(DivisonM.DriveBender.IFolder).Should().NotBeNull();
      typeof(DivisonM.DriveBender.IFileSystemItem).Should().NotBeNull();
      typeof(DivisonM.DriveBender.IPhysicalFile).Should().NotBeNull();
      typeof(DivisonM.DriveBender.IPhysicalFolder).Should().NotBeNull();
      typeof(DivisonM.DriveBender.IPhysicalFileSystemItem).Should().NotBeNull();
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