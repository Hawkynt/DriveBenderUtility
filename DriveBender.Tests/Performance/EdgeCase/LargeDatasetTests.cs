using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.Performance.EdgeCase {
  
  [TestFixture]
  [Category("Performance")]
  [Category("EdgeCase")]
  public class LargeDatasetTests : TestBase {
    
    [Test]
    [Timeout(30000)] // 30 seconds max
    public void IntegrityCheck_WithLargePool_ShouldHandleGracefully() {
      // Arrange
      var mockMountPoint = new Mock<DriveBender.IMountPoint>();
      var largeFileSet = CreateLargeFileSet(10000); // 10,000 files
      
      mockMountPoint.Setup(m => m.GetItems(It.IsAny<System.IO.SearchOption>()))
                   .Returns(largeFileSet);
      
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var issues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(30000);
      issues.Should().NotBeNull();
    }
    
    [Test]
    [Timeout(15000)] // 15 seconds max
    public void DuplicationManager_WithManyVolumes_ShouldScale() {
      // Arrange
      var mockMountPoint = new Mock<DriveBender.IMountPoint>();
      var volumes = CreateManyVolumes(100); // 100 volumes
      
      mockMountPoint.Setup(m => m.Volumes).Returns(volumes);
      
      var folders = Enumerable.Range(0, 50)
                             .Select(i => new FolderPath($"LargeFolder{i}"))
                             .ToArray();
      
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      foreach (var folder in folders) {
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, folder, DuplicationLevel.Triple);
      }
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(15000);
    }
    
    [Test]
    [Timeout(5000)] // 5 seconds max
    public void ByteSize_WithExtremeValues_ShouldPerformWell() {
      // Arrange
      var extremeSizes = new[] {
        ByteSize.FromBytes(0),
        ByteSize.FromBytes(1),
        ByteSize.FromBytes(ulong.MaxValue / 2),
        ByteSize.FromBytes(ulong.MaxValue - 1),
        ByteSize.FromTerabytes(1000)
      };
      
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      foreach (var size in extremeSizes) {
        var humanReadable = size.ToHumanReadable();
        var comparison = size.CompareTo(ByteSize.FromGigabytes(1));
        var arithmetic = size + ByteSize.FromMegabytes(1);
      }
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
    }
    
    [Test]
    [Timeout(10000)] // 10 seconds max
    public void FolderPath_WithDeepNesting_ShouldHandleEfficiently() {
      // Arrange
      var deepPaths = Enumerable.Range(0, 1000)
                               .Select(CreateDeepPath)
                               .ToArray();
      
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var folderPaths = deepPaths.Select(p => new FolderPath(p)).ToArray();
      var segments = folderPaths.SelectMany(fp => fp.Segments).ToArray();
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(10000);
      segments.Should().NotBeEmpty();
    }
    
    [Test]
    [Timeout(20000)] // 20 seconds max
    public void PoolManager_RemoveDriveFromLargePool_ShouldComplete() {
      // Arrange
      var poolName = "LargeTestPool";
      var drivePath = "C:\\LargeTestDrive";
      
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var result = PoolManager.RemoveDriveFromPool(poolName, drivePath, true);
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(20000);
      // Result may be false due to non-existent pool, but should not timeout
    }
    
    [Test]
    public void DataTypes_MassEquality_ShouldBeEfficient() {
      // Arrange
      var poolNames = Enumerable.Range(0, 5000)
                               .Select(i => new PoolName($"Pool{i}"))
                               .ToArray();
      
      var duplicates = poolNames.Take(2500).ToArray();
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var uniqueNames = poolNames.Concat(duplicates).Distinct().ToArray();
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000);
      uniqueNames.Should().HaveCount(5000);
    }
    
    private IEnumerable<DriveBender.IFile> CreateLargeFileSet(int count) {
      var files = new List<Mock<DriveBender.IFile>>();
      var volume = new Mock<DriveBender.IVolume>();
      volume.Setup(v => v.Name).Returns("LargeVolume");
      
      for (int i = 0; i < count; i++) {
        var file = new Mock<DriveBender.IFile>();
        file.Setup(f => f.FullName).Returns($"LargeFile{i}.dat");
        file.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(i % 100 + 1));
        file.Setup(f => f.Primary).Returns(volume.Object);
        files.Add(file);
      }
      
      return files.Select(f => f.Object);
    }
    
    private IEnumerable<DriveBender.IVolume> CreateManyVolumes(int count) {
      var volumes = new List<Mock<DriveBender.IVolume>>();
      
      for (int i = 0; i < count; i++) {
        var volume = new Mock<DriveBender.IVolume>();
        volume.Setup(v => v.Name).Returns($"Volume{i}");
        volume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(i % 500 + 10));
        volumes.Add(volume);
      }
      
      return volumes.Select(v => v.Object);
    }
    
    private string CreateDeepPath(int index) {
      var depth = index % 20 + 5; // 5-25 levels deep
      var segments = Enumerable.Range(0, depth)
                              .Select(i => $"Level{i}")
                              .ToArray();
      return string.Join("/", segments);
    }
  }
}