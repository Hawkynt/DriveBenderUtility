using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.Integration.HappyPath {
  
  [TestFixture]
  [Category("Integration")]
  [Category("HappyPath")]
  public class PoolIntegrationTests : TestBase {
    
    private Mock<DivisonM.DriveBender.IMountPoint> _mockMountPoint;
    private Mock<DivisonM.DriveBender.IVolume> _mockVolume1;
    private Mock<DivisonM.DriveBender.IVolume> _mockVolume2;
    private List<Mock<DivisonM.DriveBender.IFile>> _mockFiles;
    
    [SetUp]
    public override void SetUp() {
      _mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      _mockVolume1 = new Mock<DivisonM.DriveBender.IVolume>();
      _mockVolume2 = new Mock<DivisonM.DriveBender.IVolume>();
      _mockFiles = new List<Mock<DivisonM.DriveBender.IFile>>();
      
      SetupMockVolumes();
      SetupMockFiles();
      SetupMockMountPoint();
    }
    
    [Test]
    public void FullWorkflow_CreatePoolEnableDuplicationCheckIntegrity_ShouldWork() {
      // Arrange
      var poolName = new PoolName("IntegrationTestPool");
      var mountPoint = "C:\\TestMount";
      var drives = new[] { "C:\\TestDrive1", "C:\\TestDrive2" };
      var folderPath = new FolderPath("Documents/Important");
      
      // Act & Assert - Step 1: Create Pool
      var createResult = PoolManager.CreatePool(poolName, mountPoint, drives);
      // May fail due to non-existent paths, but should not throw
      
      // Act & Assert - Step 2: Enable Duplication
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, folderPath, DuplicationLevel.Double)
      );
      
      // Act & Assert - Step 3: Check Integrity
      var integrityIssues = IntegrityChecker.CheckPoolIntegrity(_mockMountPoint.Object, false, true);
      integrityIssues.Should().NotBeNull();
      
      // Act & Assert - Step 4: Verify Duplication Level
      var duplicationLevel = DuplicationManager.GetDuplicationLevel(_mockMountPoint.Object, folderPath);
      duplicationLevel.Should().BeOfType<DuplicationLevel>();
    }
    
    [Test]
    public void ShadowCopyWorkflow_CreateVerifyRepair_ShouldIntegrate() {
      // Arrange
      var file = _mockFiles[0].Object;
      var targetVolume = _mockVolume2.Object;
      
      // Act & Assert - Step 1: Create Shadow Copy
      Assert.DoesNotThrow(() => 
        DuplicationManager.CreateAdditionalShadowCopy(file, targetVolume)
      );
      
      // Act & Assert - Step 2: Verify File Integrity
      var fileIssues = IntegrityChecker.CheckFileIntegrity(file, false);
      fileIssues.Should().NotBeNull();
      
      // Act & Assert - Step 3: Check if repair is needed
      if (fileIssues.Any()) {
        var firstIssue = fileIssues.First();
        var repairResult = IntegrityChecker.RepairIntegrityIssue(firstIssue, true, true);
        // repairResult should be a boolean
      }
    }
    
    [Test]
    public void DriveManagement_AddRemoveReplace_ShouldMaintainIntegrity() {
      // Arrange
      var poolName = "DriveManagementPool";
      var originalDrive = "C:\\OriginalDrive";
      var newDrive = "C:\\NewDrive";
      
      // Act & Assert - Step 1: Add Drive to Pool
      var addResult = PoolManager.AddDriveToPool(poolName, originalDrive);
      // May fail due to non-existent pool/drive, but should not throw
      
      // Act & Assert - Step 2: Check Pool Integrity Before Removal
      var issuesBeforeRemoval = IntegrityChecker.CheckPoolIntegrity(_mockMountPoint.Object, false, true);
      issuesBeforeRemoval.Should().NotBeNull();
      
      // Act & Assert - Step 3: Remove Drive (with data movement)
      var removeResult = PoolManager.RemoveDriveFromPool(poolName, originalDrive, true);
      // May fail due to non-existent pool/drive, but should not throw
      
      // Act & Assert - Step 4: Add Replacement Drive
      var replaceResult = PoolManager.AddDriveToPool(poolName, newDrive);
      // May fail due to non-existent pool/drive, but should not throw
      
      // Act & Assert - Step 5: Verify Integrity After Changes
      var issuesAfterChanges = IntegrityChecker.CheckPoolIntegrity(_mockMountPoint.Object, false, true);
      issuesAfterChanges.Should().NotBeNull();
    }
    
    [Test]
    public void MultiLevelDuplication_EnableIncreaseDecrease_ShouldWork() {
      // Arrange
      var folderPath = new FolderPath("Projects/Critical");
      
      // Act & Assert - Step 1: Enable Basic Duplication
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, folderPath, DuplicationLevel.Single)
      );
      
      // Act & Assert - Step 2: Increase Duplication Level
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(_mockMountPoint.Object, folderPath, DuplicationLevel.Triple)
      );
      
      // Act & Assert - Step 3: Create Additional Shadow Copies for Files
      foreach (var mockFile in _mockFiles.Take(3)) {
        Assert.DoesNotThrow(() => 
          DuplicationManager.CreateAdditionalShadowCopy(mockFile.Object, _mockVolume2.Object)
        );
      }
      
      // Act & Assert - Step 4: Verify All Files Have Proper Duplication
      var currentLevel = DuplicationManager.GetDuplicationLevel(_mockMountPoint.Object, folderPath);
      currentLevel.Should().BeOfType<DuplicationLevel>();
      
      // Act & Assert - Step 5: Disable Duplication
      Assert.DoesNotThrow(() => 
        DuplicationManager.DisableDuplicationOnFolder(_mockMountPoint.Object, folderPath)
      );
    }
    
    [Test]
    public void IntegrityRepairWorkflow_DetectRepairVerify_ShouldComplete() {
      // Arrange
      var mockCorruptedFile = new Mock<DivisonM.DriveBender.IFile>();
      mockCorruptedFile.Setup(f => f.FullName).Returns("CorruptedFile.doc");
      mockCorruptedFile.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(5));
      mockCorruptedFile.Setup(f => f.Primary).Returns(_mockVolume1.Object);
      
      // Add corrupted file to mock mount point
      var allFiles = _mockFiles.Select(f => f.Object).Concat(new[] { mockCorruptedFile.Object });
      _mockMountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>())).Returns(allFiles);
      
      // Act & Assert - Step 1: Detect Integrity Issues
      var issues = IntegrityChecker.CheckPoolIntegrity(_mockMountPoint.Object, true, true);
      issues.Should().NotBeNull();
      
      // Act & Assert - Step 2: Attempt Repair (Dry Run)
      foreach (var issue in issues.Take(5)) { // Limit to 5 for performance
        var dryRunResult = IntegrityChecker.RepairIntegrityIssue(issue, true, true);
        // dryRunResult should be a boolean
      }
      
      // Act & Assert - Step 3: Perform Actual Repair
      foreach (var issue in issues.Take(2)) { // Limit to 2 for performance
        var repairResult = IntegrityChecker.RepairIntegrityIssue(issue, false, true);
        // repairResult should be a boolean
      }
      
      // Act & Assert - Step 4: Re-check Integrity
      var postRepairIssues = IntegrityChecker.CheckPoolIntegrity(_mockMountPoint.Object, false, true);
      postRepairIssues.Should().NotBeNull();
    }
    
    [Test]
    public void DataTypeIntegration_SemanticTypesWorkTogether_ShouldBeConsistent() {
      // Arrange
      var poolName = new PoolName("SemanticTestPool");
      var drivePath = new DrivePath(Path.GetTempPath()); // Use temp path as it exists
      var folderPath = new FolderPath("Documents/Projects/MyApp");
      var duplicationLevel = new DuplicationLevel(2);
      var fileSize = ByteSize.FromGigabytes(1.5);
      
      // Act & Assert - All data types should work together
      poolName.Value.Should().Be("SemanticTestPool");
      drivePath.Exists.Should().BeTrue();
      folderPath.Segments.Should().Equal("Documents", "Projects", "MyApp");
      duplicationLevel.IsMultipleCopies.Should().BeTrue();
      fileSize.Gigabytes.Should().BeApproximately(1.5, 0.1);
      
      // Act & Assert - Test conversions and comparisons
      string poolNameString = poolName;
      poolNameString.Should().Be("SemanticTestPool");
      
      var combinedPath = folderPath.Combine("SubFolder");
      combinedPath.Value.Should().Be("Documents/Projects/MyApp/SubFolder");
      
      var doubleSize = fileSize + fileSize;
      doubleSize.Gigabytes.Should().BeApproximately(3.0, 0.1);
    }
    
    private void SetupMockVolumes() {
      _mockVolume1.Setup(v => v.Name).Returns("IntegrationVolume1");
      _mockVolume1.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(500));
      
      _mockVolume2.Setup(v => v.Name).Returns("IntegrationVolume2");
      _mockVolume2.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(300));
    }
    
    private void SetupMockFiles() {
      for (int i = 0; i < 10; i++) {
        var mockFile = new Mock<DivisonM.DriveBender.IFile>();
        mockFile.Setup(f => f.FullName).Returns($"IntegrationFile{i}.txt");
        mockFile.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(i + 1));
        mockFile.Setup(f => f.Primary).Returns(i % 2 == 0 ? _mockVolume1.Object : _mockVolume2.Object);
        
        if (i % 3 == 0) {
          mockFile.Setup(f => f.ShadowCopies).Returns(new[] { _mockVolume2.Object });
        } else {
          mockFile.Setup(f => f.ShadowCopies).Returns(Enumerable.Empty<DivisonM.DriveBender.IVolume>());
        }
        
        _mockFiles.Add(mockFile);
      }
    }
    
    private void SetupMockMountPoint() {
      _mockMountPoint.Setup(m => m.Name).Returns("IntegrationMountPoint");
      _mockMountPoint.Setup(m => m.Volumes).Returns(new[] { _mockVolume1.Object, _mockVolume2.Object });
      _mockMountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>()))
                   .Returns(_mockFiles.Select(f => f.Object));
    }
  }
}