using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.Integration.EdgeCase {
  
  [TestFixture]
  [Category("Integration")]
  [Category("EdgeCase")]
  public class SystemIntegrationTests : TestBase {
    
    [Test]
    public void PoolManager_WithNonExistentPaths_ShouldHandleGracefully() {
      // Arrange
      var poolName = "NonExistentPool";
      var nonExistentMount = @"Z:\NonExistent\Mount";
      var nonExistentDrives = new[] { @"Z:\NonExistent\Drive1", @"Z:\NonExistent\Drive2" };
      
      // Act & Assert - Should not throw exceptions
      Assert.DoesNotThrow(() => {
        var result = PoolManager.CreatePool(poolName, nonExistentMount, nonExistentDrives);
        result.Should().BeFalse(); // Should fail gracefully
      });
      
      Assert.DoesNotThrow(() => {
        var result = PoolManager.RemoveDriveFromPool(poolName, nonExistentDrives[0], true);
        result.Should().BeFalse(); // Should fail gracefully
      });
    }
    
    [Test]
    public void IntegrityChecker_WithMixedValidInvalidFiles_ShouldContinueProcessing() {
      // Arrange
      var mockMountPoint = new Mock<DriveBender.IMountPoint>();
      var mixedFiles = CreateMixedValidityFileSet();
      
      mockMountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>()))
                   .Returns(mixedFiles);
      
      // Act & Assert - Should process all files despite some being invalid
      Assert.DoesNotThrow(() => {
        var issues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
        issues.Should().NotBeNull();
        // Should contain issues for invalid files but not crash
      });
    }
    
    [Test]
    public void DuplicationManager_WithInsufficientSpaceScenario_ShouldHandleGracefully() {
      // Arrange
      var mockMountPoint = new Mock<DriveBender.IMountPoint>();
      var mockVolumeNoSpace = new Mock<DriveBender.IVolume>();
      var mockVolumeLowSpace = new Mock<DriveBender.IVolume>();
      
      mockVolumeNoSpace.Setup(v => v.Name).Returns("NoSpaceVolume");
      mockVolumeNoSpace.Setup(v => v.BytesFree).Returns(ByteSize.FromBytes(0));
      
      mockVolumeLowSpace.Setup(v => v.Name).Returns("LowSpaceVolume");
      mockVolumeLowSpace.Setup(v => v.BytesFree).Returns(ByteSize.FromMegabytes(10));
      
      mockMountPoint.Setup(m => m.Volumes).Returns(new[] { mockVolumeNoSpace.Object, mockVolumeLowSpace.Object });
      
      var largeFile = new Mock<DriveBender.IFile>();
      largeFile.Setup(f => f.Size).Returns(ByteSize.FromGigabytes(100)); // Larger than available space
      largeFile.Setup(f => f.Primary).Returns(mockVolumeNoSpace.Object);
      
      // Act & Assert - Should handle gracefully without crashing
      Assert.DoesNotThrow(() => 
        DuplicationManager.CreateAdditionalShadowCopy(largeFile.Object, mockVolumeLowSpace.Object)
      );
    }
    
    [Test]
    public void DataTypes_WithSystemLimits_ShouldBehaveCorrectly() {
      // Arrange & Act & Assert - Test extreme values
      Assert.DoesNotThrow(() => {
        var maxByteSize = new ByteSize(ulong.MaxValue);
        var humanReadable = maxByteSize.ToHumanReadable();
        humanReadable.Should().NotBeNullOrWhiteSpace();
      });
      
      Assert.DoesNotThrow(() => {
        var maxLengthPool = new PoolName(new string('A', 255));
        maxLengthPool.Value.Should().HaveLength(255);
      });
      
      Assert.DoesNotThrow(() => {
        var existingTempPath = new DrivePath(Path.GetTempPath());
        existingTempPath.Exists.Should().BeTrue();
      });
    }
    
    [Test]
    public void CrossComponentIntegration_RealWorldScenario_ShouldWork() {
      // Arrange - Simulate real-world pool setup
      var poolName = new PoolName("ProductionPool");
      var folderPath = new FolderPath("Users/Documents/ImportantData");
      var duplicationLevel = new DuplicationLevel(3);
      
      var mockMountPoint = new Mock<DriveBender.IMountPoint>();
      var mockVolumes = CreateRealisticVolumeSet();
      var mockFiles = CreateRealisticFileSet(mockVolumes);
      
      mockMountPoint.Setup(m => m.Name).Returns(poolName.Value);
      mockMountPoint.Setup(m => m.Volumes).Returns(mockVolumes);
      mockMountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>())).Returns(mockFiles);
      
      // Act & Assert - Full workflow integration
      
      // Step 1: Enable duplication
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, folderPath, duplicationLevel)
      );
      
      // Step 2: Create shadow copies for critical files
      var criticalFiles = mockFiles.Where(f => f.Size > ByteSize.FromMegabytes(50)).Take(5);
      foreach (var file in criticalFiles) {
        Assert.DoesNotThrow(() => 
          DuplicationManager.CreateAdditionalShadowCopy(file, mockVolumes.Skip(1).First())
        );
      }
      
      // Step 3: Run integrity check
      var issues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
      issues.Should().NotBeNull();
      
      // Step 4: Attempt repairs on first few issues
      foreach (var issue in issues.Take(3)) {
        Assert.DoesNotThrow(() => 
          IntegrityChecker.RepairIntegrityIssue(issue, true, true) // Dry run
        );
      }
      
      // Step 5: Verify duplication level
      var currentLevel = DuplicationManager.GetDuplicationLevel(mockMountPoint.Object, folderPath);
      currentLevel.Should().BeOfType<DuplicationLevel>();
    }
    
    [Test]
    public void ErrorRecovery_WithPartialFailures_ShouldContinue() {
      // Arrange
      var mockMountPoint = new Mock<DriveBender.IMountPoint>();
      var problematicVolume = new Mock<DriveBender.IVolume>();
      var workingVolume = new Mock<DriveBender.IVolume>();
      
      // Setup problematic volume that throws exceptions
      problematicVolume.Setup(v => v.Name).Returns("ProblematicVolume");
      problematicVolume.Setup(v => v.BytesFree).Throws<IOException>("Disk error");
      
      workingVolume.Setup(v => v.Name).Returns("WorkingVolume");
      workingVolume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(100));
      
      mockMountPoint.Setup(m => m.Volumes).Returns(new[] { problematicVolume.Object, workingVolume.Object });
      
      var mixedFiles = new List<Mock<DriveBender.IFile>>();
      
      // Create files on both volumes
      for (int i = 0; i < 10; i++) {
        var file = new Mock<DriveBender.IFile>();
        file.Setup(f => f.FullName).Returns($"File{i}.txt");
        file.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(i + 1));
        file.Setup(f => f.Primary).Returns(i % 2 == 0 ? problematicVolume.Object : workingVolume.Object);
        mixedFiles.Add(file);
      }
      
      mockMountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>()))
                   .Returns(mixedFiles.Select(f => f.Object));
      
      // Act & Assert - Should continue processing despite volume errors
      Assert.DoesNotThrow(() => {
        var issues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
        issues.Should().NotBeNull();
        // Should process files on working volume despite problematic volume
      });
    }
    
    private IEnumerable<DriveBender.IFile> CreateMixedValidityFileSet() {
      var files = new List<Mock<DriveBender.IFile>>();
      var validVolume = new Mock<DriveBender.IVolume>();
      validVolume.Setup(v => v.Name).Returns("ValidVolume");
      
      var invalidVolume = new Mock<DriveBender.IVolume>();
      invalidVolume.Setup(v => v.Name).Throws<InvalidOperationException>("Volume corrupted");
      
      // Mix of valid and invalid files
      for (int i = 0; i < 20; i++) {
        var file = new Mock<DriveBender.IFile>();
        file.Setup(f => f.FullName).Returns($"MixedFile{i}.txt");
        file.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(i + 1));
        
        if (i % 4 == 0) {
          // Some files throw exceptions
          file.Setup(f => f.Primary).Throws<IOException>("File access error");
        } else {
          file.Setup(f => f.Primary).Returns(validVolume.Object);
        }
        
        files.Add(file);
      }
      
      return files.Select(f => f.Object);
    }
    
    private IEnumerable<DriveBender.IVolume> CreateRealisticVolumeSet() {
      var volumes = new List<Mock<DriveBender.IVolume>>();
      
      var volumeConfigs = new[] {
        ("PrimaryVolume", 2000), // 2TB
        ("BackupVolume", 1500),  // 1.5TB
        ("ArchiveVolume", 4000), // 4TB
        ("FastVolume", 500)      // 500GB SSD
      };
      
      foreach (var (name, sizeGB) in volumeConfigs) {
        var volume = new Mock<DriveBender.IVolume>();
        volume.Setup(v => v.Name).Returns(name);
        volume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(sizeGB * 0.7)); // 70% free
        volumes.Add(volume);
      }
      
      return volumes.Select(v => v.Object);
    }
    
    private IEnumerable<DriveBender.IFile> CreateRealisticFileSet(IEnumerable<DriveBender.IVolume> volumes) {
      var files = new List<Mock<DriveBender.IFile>>();
      var volumeArray = volumes.ToArray();
      
      var fileConfigs = new[] {
        ("Document1.docx", 5),
        ("Presentation.pptx", 25),
        ("Spreadsheet.xlsx", 15),
        ("Video.mp4", 1500),
        ("Archive.zip", 800),
        ("Database.db", 2000),
        ("Photo1.jpg", 8),
        ("Photo2.jpg", 12),
        ("Code.zip", 50),
        ("Backup.tar", 3000)
      };
      
      for (int i = 0; i < fileConfigs.Length; i++) {
        var (name, sizeMB) = fileConfigs[i];
        var file = new Mock<DriveBender.IFile>();
        file.Setup(f => f.FullName).Returns(name);
        file.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(sizeMB));
        file.Setup(f => f.Primary).Returns(volumeArray[i % volumeArray.Length]);
        
        // Some files have shadow copies
        if (sizeMB > 100) {
          var shadowVolume = volumeArray[(i + 1) % volumeArray.Length];
          file.Setup(f => f.ShadowCopies).Returns(new[] { shadowVolume });
        } else {
          file.Setup(f => f.ShadowCopies).Returns(Enumerable.Empty<DriveBender.IVolume>());
        }
        
        files.Add(file);
      }
      
      return files.Select(f => f.Object);
    }
  }
}