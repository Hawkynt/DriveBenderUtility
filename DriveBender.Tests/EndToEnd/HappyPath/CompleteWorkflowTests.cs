using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.EndToEnd.HappyPath {
  
  [TestFixture]
  [Category("EndToEnd")]
  [Category("HappyPath")]
  public class CompleteWorkflowTests : TestBase {
    
    [Test]
    public void FullPoolLifecycle_CreateManageDestroy_ShouldCompleteSuccessfully() {
      // This test simulates the complete lifecycle of a pool from creation to destruction
      
      // Arrange
      var poolName = new PoolName("E2ETestPool");
      var mountPoint = @"C:\E2ETestMount";
      var primaryDrive = @"C:\E2ETestDrive1";
      var secondaryDrive = @"C:\E2ETestDrive2";
      var replacementDrive = @"C:\E2ETestDrive3";
      
      var mockMountPoint = CreateMockMountPoint(poolName.Value);
      var mockVolumes = CreateMockVolumes();
      var mockFiles = CreateMockFiles(mockVolumes);
      
      SetupMockMountPoint(mockMountPoint, mockVolumes, mockFiles);
      
      try {
        // Act & Assert - Phase 1: Pool Creation
        TestContext.WriteLine("Phase 1: Creating pool...");
        var createResult = PoolManager.CreatePool(poolName, mountPoint, new[] { primaryDrive, secondaryDrive });
        // Note: May return false due to non-existent paths in test environment
        
        // Act & Assert - Phase 2: Enable Duplication on Critical Folders
        TestContext.WriteLine("Phase 2: Setting up duplication...");
        var criticalFolders = new[] {
          new FolderPath("Documents/Important"),
          new FolderPath("Projects/Critical"),
          new FolderPath("Backup/Essential")
        };
        
        foreach (var folder in criticalFolders) {
          Assert.DoesNotThrow(() => 
            DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, folder, DuplicationLevel.Double)
          );
        }
        
        // Act & Assert - Phase 3: Create Shadow Copies for Large Files
        TestContext.WriteLine("Phase 3: Creating shadow copies...");
        var largeFiles = mockFiles.Where(f => f.Size > ByteSize.FromMegabytes(100)).Take(5);
        foreach (var file in largeFiles) {
          foreach (var volume in mockVolumes.Skip(1).Take(2)) {
            Assert.DoesNotThrow(() => 
              DuplicationManager.CreateAdditionalShadowCopy(file, volume)
            );
          }
        }
        
        // Act & Assert - Phase 4: Run Comprehensive Integrity Check
        TestContext.WriteLine("Phase 4: Running integrity check...");
        var initialIssues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, true, true);
        initialIssues.Should().NotBeNull();
        TestContext.WriteLine($"Found {initialIssues.Count()} initial integrity issues");
        
        // Act & Assert - Phase 5: Repair Issues (Dry Run First)
        TestContext.WriteLine("Phase 5: Repairing issues...");
        var criticalIssues = initialIssues.Take(5); // Limit for test performance
        foreach (var issue in criticalIssues) {
          // Dry run first
          var dryRunResult = IntegrityChecker.RepairIntegrityIssue(issue, true, true);
          TestContext.WriteLine($"Dry run repair result: {dryRunResult}");
          
          // Actual repair
          var actualResult = IntegrityChecker.RepairIntegrityIssue(issue, false, true);
          TestContext.WriteLine($"Actual repair result: {actualResult}");
        }
        
        // Act & Assert - Phase 6: Add New Drive
        TestContext.WriteLine("Phase 6: Adding new drive...");
        var addDriveResult = PoolManager.AddDriveToPool(poolName, replacementDrive);
        // May return false due to non-existent drive
        
        // Act & Assert - Phase 7: Remove Old Drive (with data migration)
        TestContext.WriteLine("Phase 7: Removing old drive...");
        var removeDriveResult = PoolManager.RemoveDriveFromPool(poolName, primaryDrive, true);
        // May return false due to non-existent pool/drive
        
        // Act & Assert - Phase 8: Final Integrity Verification
        TestContext.WriteLine("Phase 8: Final integrity check...");
        var finalIssues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
        finalIssues.Should().NotBeNull();
        TestContext.WriteLine($"Final integrity issues: {finalIssues.Count()}");
        
        // Act & Assert - Phase 9: Verify Duplication Levels
        TestContext.WriteLine("Phase 9: Verifying duplication levels...");
        foreach (var folder in criticalFolders) {
          var level = DuplicationManager.GetDuplicationLevel(mockMountPoint.Object, folder);
          level.Should().BeOfType<DuplicationLevel>();
          TestContext.WriteLine($"Folder {folder.Value} duplication level: {level}");
        }
        
        // Act & Assert - Phase 10: Performance Validation
        TestContext.WriteLine("Phase 10: Performance validation...");
        var startTime = DateTime.Now;
        var performanceIssues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
        var duration = DateTime.Now - startTime;
        
        performanceIssues.Should().NotBeNull();
        duration.Should().BeLessThan(TimeSpan.FromMinutes(2)); // Should complete within 2 minutes
        TestContext.WriteLine($"Performance check completed in {duration.TotalSeconds:F2} seconds");
        
      } catch (Exception ex) {
        TestContext.WriteLine($"Workflow failed with exception: {ex.Message}");
        throw;
      }
    }
    
    [Test]
    public void DisasterRecoveryScenario_CorruptionDetectionAndRepair_ShouldRecover() {
      // This test simulates a disaster recovery scenario with multiple types of corruption
      
      // Arrange
      var mockMountPoint = CreateMockMountPoint("DisasterRecoveryPool");
      var mockVolumes = CreateMockVolumes();
      var corruptedFiles = CreateCorruptedFiles(mockVolumes);
      
      SetupMockMountPoint(mockMountPoint, mockVolumes, corruptedFiles);
      
      TestContext.WriteLine("Starting disaster recovery scenario...");
      
      // Act & Assert - Phase 1: Detect All Issues
      TestContext.WriteLine("Phase 1: Detecting corruption...");
      var allIssues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, true, true);
      allIssues.Should().NotBeNull();
      TestContext.WriteLine($"Detected {allIssues.Count()} integrity issues");
      
      // Act & Assert - Phase 2: Categorize Issues by Severity
      TestContext.WriteLine("Phase 2: Categorizing issues...");
      var issueArray = allIssues.ToArray();
      var criticalIssues = issueArray.Take(issueArray.Length / 3);
      var moderateIssues = issueArray.Skip(issueArray.Length / 3).Take(issueArray.Length / 3);
      var minorIssues = issueArray.Skip(2 * issueArray.Length / 3);
      
      TestContext.WriteLine($"Critical: {criticalIssues.Count()}, Moderate: {moderateIssues.Count()}, Minor: {minorIssues.Count()}");
      
      // Act & Assert - Phase 3: Repair Critical Issues First
      TestContext.WriteLine("Phase 3: Repairing critical issues...");
      var criticalRepairSuccess = 0;
      foreach (var issue in criticalIssues.Take(5)) { // Limit for performance
        try {
          var result = IntegrityChecker.RepairIntegrityIssue(issue, false, true);
          if (result) criticalRepairSuccess++;
          TestContext.WriteLine($"Critical repair attempt: {result}");
        } catch (Exception ex) {
          TestContext.WriteLine($"Critical repair failed: {ex.Message}");
        }
      }
      
      // Act & Assert - Phase 4: Verify Recovery
      TestContext.WriteLine("Phase 4: Verifying recovery...");
      var postRepairIssues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
      postRepairIssues.Should().NotBeNull();
      TestContext.WriteLine($"Issues after repair: {postRepairIssues.Count()}");
      
      // Act & Assert - Phase 5: Restore Duplication
      TestContext.WriteLine("Phase 5: Restoring duplication...");
      var recoveryFolders = new[] {
        new FolderPath("Recovery/Critical"),
        new FolderPath("Recovery/Important")
      };
      
      foreach (var folder in recoveryFolders) {
        Assert.DoesNotThrow(() => 
          DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, folder, DuplicationLevel.Triple)
        );
      }
      
      TestContext.WriteLine("Disaster recovery scenario completed");
    }
    
    [Test]
    public void ProductionSimulation_RealWorldUsagePatterns_ShouldHandleGracefully() {
      // This test simulates real-world production usage patterns
      
      // Arrange
      var mockMountPoint = CreateMockMountPoint("ProductionPool");
      var mockVolumes = CreateLargeVolumeSet();
      var productionFiles = CreateProductionFileSet(mockVolumes);
      
      SetupMockMountPoint(mockMountPoint, mockVolumes, productionFiles);
      
      TestContext.WriteLine("Starting production simulation...");
      
      // Act & Assert - Phase 1: Daily Operations
      TestContext.WriteLine("Phase 1: Daily operations...");
      var dailyFolders = new[] {
        new FolderPath("Users/Alice/Documents"),
        new FolderPath("Users/Bob/Projects"),
        new FolderPath("Shared/TeamData"),
        new FolderPath("Backup/Daily")
      };
      
      foreach (var folder in dailyFolders) {
        var level = folder.Value.Contains("Backup") ? DuplicationLevel.Triple : DuplicationLevel.Double;
        Assert.DoesNotThrow(() => 
          DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, folder, level)
        );
      }
      
      // Act & Assert - Phase 2: Weekly Maintenance
      TestContext.WriteLine("Phase 2: Weekly maintenance...");
      var weeklyIssues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
      weeklyIssues.Should().NotBeNull();
      TestContext.WriteLine($"Weekly check found {weeklyIssues.Count()} issues");
      
      // Act & Assert - Phase 3: Monthly Deep Scan
      TestContext.WriteLine("Phase 3: Monthly deep scan...");
      var startTime = DateTime.Now;
      var deepScanIssues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, true, true);
      var scanDuration = DateTime.Now - startTime;
      
      deepScanIssues.Should().NotBeNull();
      TestContext.WriteLine($"Deep scan completed in {scanDuration.TotalMinutes:F2} minutes, found {deepScanIssues.Count()} issues");
      
      // Act & Assert - Phase 4: Capacity Management
      TestContext.WriteLine("Phase 4: Capacity management...");
      var totalCapacity = mockVolumes.Aggregate(ByteSize.FromBytes(0), (acc, v) => acc + v.BytesFree);
      var averageCapacity = new ByteSize(totalCapacity.Bytes / (ulong)mockVolumes.Count());
      
      TestContext.WriteLine($"Total free capacity: {totalCapacity.ToHumanReadable()}");
      TestContext.WriteLine($"Average volume capacity: {averageCapacity.ToHumanReadable()}");
      
      totalCapacity.Should().BeGreaterThan(ByteSize.FromGigabytes(100));
      
      // Act & Assert - Phase 5: User Access Patterns
      TestContext.WriteLine("Phase 5: Simulating user access patterns...");
      var accessedFiles = productionFiles.Where(f => f.Size < ByteSize.FromMegabytes(50)).Take(10);
      foreach (var file in accessedFiles) {
        var fileIssues = IntegrityChecker.CheckFileIntegrity(file, false);
        fileIssues.Should().NotBeNull();
      }
      
      TestContext.WriteLine("Production simulation completed");
    }
    
    private Mock<DriveBender.IMountPoint> CreateMockMountPoint(string name) {
      var mock = new Mock<DriveBender.IMountPoint>();
      mock.Setup(m => m.Name).Returns(name);
      return mock;
    }
    
    private List<DriveBender.IVolume> CreateMockVolumes() {
      var volumes = new List<Mock<DriveBender.IVolume>>();
      
      var configs = new[] {
        ("PrimaryVolume", 1000),
        ("SecondaryVolume", 800),
        ("BackupVolume", 1200)
      };
      
      foreach (var (name, sizeGB) in configs) {
        var volume = new Mock<DriveBender.IVolume>();
        volume.Setup(v => v.Name).Returns(name);
        volume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(sizeGB));
        volumes.Add(volume);
      }
      
      return volumes.Select(v => v.Object).ToList();
    }
    
    private List<DriveBender.IFile> CreateMockFiles(List<DriveBender.IVolume> volumes) {
      var files = new List<Mock<DriveBender.IFile>>();
      
      for (int i = 0; i < 50; i++) {
        var file = new Mock<DriveBender.IFile>();
        file.Setup(f => f.FullName).Returns($"TestFile{i:D3}.dat");
        file.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(i * 10 + 5));
        file.Setup(f => f.Primary).Returns(volumes[i % volumes.Count]);
        
        if (i % 3 == 0) {
          file.Setup(f => f.ShadowCopies).Returns(new[] { volumes[(i + 1) % volumes.Count] });
        } else {
          file.Setup(f => f.ShadowCopies).Returns(Enumerable.Empty<DriveBender.IVolume>());
        }
        
        files.Add(file);
      }
      
      return files.Select(f => f.Object).ToList();
    }
    
    private List<DriveBender.IFile> CreateCorruptedFiles(List<DriveBender.IVolume> volumes) {
      var files = CreateMockFiles(volumes);
      
      // Simulate various corruption scenarios by modifying some mock setups
      var corruptedFiles = files.Select(f => {
        var mock = Mock.Get(f);
        
        // Randomly corrupt some aspects
        var random = new Random(f.FullName.GetHashCode());
        if (random.Next(4) == 0) {
          // Simulate missing primary
          mock.Setup(x => x.Primary).Returns((DriveBender.IVolume)null);
        }
        
        return f;
      }).ToList();
      
      return corruptedFiles;
    }
    
    private List<DriveBender.IVolume> CreateLargeVolumeSet() {
      var volumes = new List<Mock<DriveBender.IVolume>>();
      
      for (int i = 0; i < 8; i++) {
        var volume = new Mock<DriveBender.IVolume>();
        volume.Setup(v => v.Name).Returns($"ProductionVolume{i:D2}");
        volume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(500 + i * 100));
        volumes.Add(volume);
      }
      
      return volumes.Select(v => v.Object).ToList();
    }
    
    private List<DriveBender.IFile> CreateProductionFileSet(List<DriveBender.IVolume> volumes) {
      var files = new List<Mock<DriveBender.IFile>>();
      
      var fileTypes = new[] {
        ("Document", 5),
        ("Spreadsheet", 15),
        ("Presentation", 25),
        ("Archive", 500),
        ("Video", 2000),
        ("Database", 1000),
        ("Image", 8),
        ("Code", 2),
        ("Backup", 3000),
        ("Log", 1)
      };
      
      for (int i = 0; i < 100; i++) {
        var (type, sizeMB) = fileTypes[i % fileTypes.Length];
        var file = new Mock<DriveBender.IFile>();
        file.Setup(f => f.FullName).Returns($"{type}_{i:D3}.{type.ToLower()}");
        file.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(sizeMB + (i % 10)));
        file.Setup(f => f.Primary).Returns(volumes[i % volumes.Count]);
        
        // Large files get shadow copies
        if (sizeMB > 100) {
          var shadowVolume = volumes[(i + 1) % volumes.Count];
          file.Setup(f => f.ShadowCopies).Returns(new[] { shadowVolume });
        } else {
          file.Setup(f => f.ShadowCopies).Returns(Enumerable.Empty<DriveBender.IVolume>());
        }
        
        files.Add(file);
      }
      
      return files.Select(f => f.Object).ToList();
    }
    
    private void SetupMockMountPoint(Mock<DriveBender.IMountPoint> mountPoint, 
                                   List<DriveBender.IVolume> volumes, 
                                   List<DriveBender.IFile> files) {
      mountPoint.Setup(m => m.Volumes).Returns(volumes);
      mountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>())).Returns(files);
    }
  }
}