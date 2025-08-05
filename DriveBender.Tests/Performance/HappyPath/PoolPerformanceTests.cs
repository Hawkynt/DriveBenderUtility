using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.Performance.HappyPath {
  
  [TestFixture]
  [Category("Performance")]
  [Category("HappyPath")]
  public class PoolPerformanceTests : TestBase {
    
    private Mock<DivisonM.DriveBender.IMountPoint> _mockMountPoint;
    private List<Mock<DivisonM.DriveBender.IVolume>> _mockVolumes;
    private List<Mock<DivisonM.DriveBender.IFile>> _mockFiles;
    
    [SetUp]
    public override void SetUp() {
      _mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      _mockVolumes = new List<Mock<DivisonM.DriveBender.IVolume>>();
      _mockFiles = new List<Mock<DivisonM.DriveBender.IFile>>();
      
      // Create 10 mock volumes
      for (int i = 0; i < 10; i++) {
        var volume = new Mock<DivisonM.DriveBender.IVolume>();
        volume.Setup(v => v.Name).Returns($"Volume{i}");
        volume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(100));
        _mockVolumes.Add(volume);
      }
      
      // Create 1000 mock files
      for (int i = 0; i < 1000; i++) {
        var file = new Mock<DivisonM.DriveBender.IFile>();
        file.Setup(f => f.FullName).Returns($"File{i}.txt");
        file.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(1));
        file.Setup(f => f.Primary).Returns(_mockVolumes[i % 10].Object);
        _mockFiles.Add(file);
      }
      
      _mockMountPoint.Setup(m => m.Volumes).Returns(_mockVolumes.Select(v => v.Object));
      _mockMountPoint.Setup(m => m.GetItems(It.IsAny<System.IO.SearchOption>()))
                   .Returns(_mockFiles.Select(f => f.Object));
    }
    
    [Test]
    [Timeout(5000)] // 5 seconds max
    public void CheckPoolIntegrity_With1000Files_ShouldCompleteWithin5Seconds() {
      // Arrange
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var issues = IntegrityChecker.CheckPoolIntegrity(_mockMountPoint.Object, false, true);
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);
      issues.Should().NotBeNull();
    }
    
    [Test]
    [Timeout(3000)] // 3 seconds max
    public void EnableDuplicationOnMultipleFolders_ShouldCompleteQuickly() {
      // Arrange
      var folders = Enumerable.Range(0, 100)
                             .Select(i => new FolderPath($"Folder{i}"))
                             .ToArray();
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      foreach (var folder in folders) {
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, folder, DuplicationLevel.Double);
      }
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(3000);
    }
    
    [Test]
    [Timeout(2000)] // 2 seconds max
    public void CreateMultipleShadowCopies_ShouldScaleWell() {
      // Arrange
      var file = _mockFiles[0].Object;
      var volumes = _mockVolumes.Take(5).Select(v => v.Object).ToArray();
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      foreach (var volume in volumes) {
        DuplicationManager.CreateAdditionalShadowCopy(file, volume);
      }
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(2000);
    }
    
    [Test]
    [Timeout(10000)] // 10 seconds max
    public void DeepScanIntegrityCheck_ShouldCompleteWithin10Seconds() {
      // Arrange
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var issues = IntegrityChecker.CheckPoolIntegrity(_mockMountPoint.Object, true, true);
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(10000);
      issues.Should().NotBeNull();
    }
    
    [Test]
    [Timeout(1000)] // 1 second max
    public void PoolManager_CreateMultiplePools_ShouldBeFast() {
      // Arrange
      var poolNames = Enumerable.Range(0, 10)
                               .Select(i => $"TestPool{i}")
                               .ToArray();
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      foreach (var poolName in poolNames) {
        PoolManager.CreatePool(poolName, $"C:\\Mount{poolName}", new[] { $"C:\\Drive{poolName}" });
      }
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }
    
    [Test]
    public void ByteSize_ArithmeticOperations_ShouldBeEfficient() {
      // Arrange
      var sizes = Enumerable.Range(0, 10000)
                           .Select(i => ByteSize.FromMegabytes(i))
                           .ToArray();
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var total = ByteSize.FromBytes(0);
      foreach (var size in sizes) {
        total = total + size;
      }
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(100);
      total.Should().BeGreaterThan(ByteSize.FromGigabytes(1));
    }
    
    [Test]
    public void DataTypes_CreationPerformance_ShouldBeOptimal() {
      // Arrange
      var paths = Enumerable.Range(0, 1000)
                           .Select(i => $"Folder{i}/SubFolder{i}")
                           .ToArray();
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var folderPaths = paths.Select(p => new FolderPath(p)).ToArray();
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(500);
      folderPaths.Should().HaveCount(1000);
    }
    
    [Test]
    public void VolumeSpaceCalculation_WithManyVolumes_ShouldBeQuick() {
      // Arrange
      var stopwatch = Stopwatch.StartNew();
      
      // Act
      var totalFreeSpace = _mockVolumes.Select(v => v.Object.BytesFree)
                                      .Aggregate(ByteSize.FromBytes(0), (acc, size) => acc + size);
      stopwatch.Stop();
      
      // Assert
      stopwatch.ElapsedMilliseconds.Should().BeLessThan(50);
      totalFreeSpace.Should().Be(ByteSize.FromGigabytes(1000));
    }
  }
}