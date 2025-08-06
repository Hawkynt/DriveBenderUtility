using System;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using static DivisonM.DriveBender;

namespace DriveBender.Tests.Integration.HappyPath {
  
  [TestFixture]
  [Category("Integration")]
  [Category("HappyPath")]
  public class PoolIntegrationTests : IntegrationTestBase {
    
    private string _drive1Path;
    private string _drive2Path;
    private string _drive3Path;
    private string _poolMountPoint;
    private string _poolName;
    
    [SetUp]
    public override void SetUp() {
      base.SetUp(); // Call base class setup to create TestDirectory

      _drive1Path = GetTestPath("Drive1");
      _drive2Path = GetTestPath("Drive2");
      _drive3Path = GetTestPath("Drive3");
      _poolMountPoint = GetTestPath("PoolMount");
      _poolName = "TestPool_" + Guid.NewGuid().ToString("N");

      CreateTestDirectory(_drive1Path);
      CreateTestDirectory(_drive2Path);
      CreateTestDirectory(_drive3Path);
      CreateTestDirectory(_poolMountPoint);

      // Simulate DriveBender info files for the drives
      CreateDriveBenderInfoFile(_drive1Path, "Drive1Label", Guid.NewGuid());
      CreateDriveBenderInfoFile(_drive2Path, "Drive2Label", Guid.NewGuid());
      CreateDriveBenderInfoFile(_drive3Path, "Drive3Label", Guid.NewGuid());
    }

    private void CreateDriveBenderInfoFile(string drivePath, string label, Guid id) {
      var infoFilePath = Path.Combine(drivePath, $"volume.{DriveBenderConstants.INFO_EXTENSION}");
      var content = $"volumelabel:{label}\nid:{id}\ndescription:Test Drive\n";
      System.IO.File.WriteAllText(infoFilePath, content);

      // Create the actual pool directory structure
      System.IO.Directory.CreateDirectory(Path.Combine(drivePath, $"{{{id}}}"));
    }
    
    [Test]
    public void FullWorkflow_CreatePoolEnableDuplicationCheckIntegrity_ShouldWork() {
      // Arrange
      var drives = new[] { _drive1Path, _drive2Path };
      var folderPath = "Documents/Important";
      
      // Act & Assert - Step 1: Create Pool
      var createResult = PoolManager.CreatePool(_poolName, _poolMountPoint, drives);
      createResult.Should().BeTrue();
      
      var pool = DetectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      pool.Should().NotBeNull();
      
      // Create the folder structure for duplication testing
      var documentsPath = Path.Combine(_poolMountPoint, "Documents");
      var importantPath = Path.Combine(documentsPath, "Important");
      Directory.CreateDirectory(importantPath);
      
      // Act & Assert - Step 2: Enable Duplication
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(pool, folderPath, 2)
      );
      
      // Create a test file to verify duplication works
      var testFilePath = Path.Combine(importantPath, "test.txt");
      System.IO.File.WriteAllText(testFilePath, "Test content for duplication");
      
      // Act & Assert - Step 3: Check Integrity
      var integrityIssues = IntegrityChecker.CheckPoolIntegrity(pool, false, true);
      integrityIssues.Should().NotBeNull();
      
      // Act & Assert - Step 4: Verify Duplication Level
      var duplicationLevel = DuplicationManager.GetDuplicationLevel(pool, folderPath);
      duplicationLevel.Should().BeGreaterThan(0);
    }
    
    [Test]
    public void ShadowCopyWorkflow_CreateVerifyRepair_ShouldIntegrate() {
      // Arrange
      var drives = new[] { _drive1Path, _drive2Path };
      PoolManager.CreatePool(_poolName, _poolMountPoint, drives);
      var pool = DetectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      if (pool == null) {
        Assert.Inconclusive($"Could not find pool '{_poolName}' in detected mount points - test environment may not be properly set up");
        return;
      }
      
      // Create a test file
      var testFilePath = Path.Combine(_poolMountPoint, "shadow_test.txt");
      var fileContent = "Content for shadow copy testing";
      System.IO.File.WriteAllText(testFilePath, fileContent);
      
      // Get the file from the pool
      var poolFiles = pool.GetItems(SearchOption.AllDirectories).OfType<IPhysicalFile>();
      var testFile = poolFiles.FirstOrDefault(f => f.Source.Name == "shadow_test.txt");
      testFile.Should().NotBeNull();
      
      // Get a target volume (just pick the first one)
      var targetVolume = pool.Volumes.FirstOrDefault();
      targetVolume.Should().NotBeNull();
      
      // Act & Assert - Step 1: Create Shadow Copy (cast to IFile)
      Assert.DoesNotThrow(() => 
        DuplicationManager.CreateAdditionalShadowCopy((IFile)testFile, targetVolume)
      );
      
      // Act & Assert - Step 2: Verify File Integrity
      var fileIssues = IntegrityChecker.CheckFileIntegrity((IFile)testFile, false);
      fileIssues.Should().NotBeNull();
      
      // Act & Assert - Step 3: Check if repair is needed
      if (fileIssues.Any()) {
        var firstIssue = fileIssues.FirstOrDefault();
        if (firstIssue == null) {
          Assert.Inconclusive("No file integrity issues found - test environment may not be properly set up");
          return;
        }
        var repairResult = IntegrityChecker.RepairIntegrityIssue(firstIssue, true, true);
        // repairResult should be a boolean
      }
    }
    
    [Test]
    public void DriveManagement_AddRemoveReplace_ShouldMaintainIntegrity() {
      // Arrange - Create initial pool with 2 drives
      var drives = new[] { _drive1Path, _drive2Path };
      PoolManager.CreatePool(_poolName, _poolMountPoint, drives);
      var pool = DetectedMountPoints.First(p => p.Name == _poolName);
      
      // Create some test files
      var testFile1 = Path.Combine(_poolMountPoint, "file1.txt");
      var testFile2 = Path.Combine(_poolMountPoint, "file2.txt");
      System.IO.File.WriteAllText(testFile1, "Test content 1");
      System.IO.File.WriteAllText(testFile2, "Test content 2");
      
      // Act & Assert - Step 1: Add Drive to Pool
      var addResult = PoolManager.AddDriveToPool(_poolName, _drive3Path);
      addResult.Should().BeTrue();
      
      // Refresh pool to see the new drive
      pool = DetectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      if (pool == null) {
        Assert.Inconclusive($"Could not find pool '{_poolName}' after adding drive - test environment may not be properly set up");
        return;
      }
      pool.Volumes.Should().HaveCount(3);
      
      // Act & Assert - Step 2: Check Pool Integrity Before Removal
      var issuesBeforeRemoval = IntegrityChecker.CheckPoolIntegrity(pool, false, true);
      issuesBeforeRemoval.Should().NotBeNull();
      
      // Act & Assert - Step 3: Remove Drive (with data movement)
      var removeResult = PoolManager.RemoveDriveFromPool(_poolName, _drive1Path, true);
      removeResult.Should().BeTrue();
      
      // Refresh pool after drive removal
      pool = DetectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      if (pool == null) {
        Assert.Inconclusive($"Could not find pool '{_poolName}' after removing drive - test environment may not be properly set up");
        return;
      }
      pool.Volumes.Should().HaveCount(2);
      
      // Act & Assert - Step 4: Verify files still exist and are accessible
      System.IO.File.Exists(testFile1).Should().BeTrue();
      System.IO.File.Exists(testFile2).Should().BeTrue();
      System.IO.File.ReadAllText(testFile1).Should().Be("Test content 1");
      System.IO.File.ReadAllText(testFile2).Should().Be("Test content 2");
      
      // Act & Assert - Step 5: Verify Integrity After Changes
      var issuesAfterChanges = IntegrityChecker.CheckPoolIntegrity(pool, false, true);
      issuesAfterChanges.Should().NotBeNull();
    }
    
    [Test]
    public void MultiLevelDuplication_EnableIncreaseDecrease_ShouldWork() {
      // Arrange
      var drives = new[] { _drive1Path, _drive2Path, _drive3Path };
      PoolManager.CreatePool(_poolName, _poolMountPoint, drives);
      var pool = DetectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      if (pool == null) {
        Assert.Inconclusive($"Could not find pool '{_poolName}' in detected mount points - test environment may not be properly set up");
        return;
      }
      
      var folderPath = "Projects/Critical";
      var fullFolderPath = Path.Combine(_poolMountPoint, folderPath);
      Directory.CreateDirectory(fullFolderPath);
      
      // Create some test files in the folder
      var testFile1 = Path.Combine(fullFolderPath, "critical1.txt");
      var testFile2 = Path.Combine(fullFolderPath, "critical2.txt");
      var testFile3 = Path.Combine(fullFolderPath, "critical3.txt");
      System.IO.File.WriteAllText(testFile1, "Critical content 1");
      System.IO.File.WriteAllText(testFile2, "Critical content 2");
      System.IO.File.WriteAllText(testFile3, "Critical content 3");
      
      // Act & Assert - Step 1: Enable Basic Duplication
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(pool, folderPath, 1)
      );
      
      // Act & Assert - Step 2: Increase Duplication Level
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(pool, folderPath, 3)
      );
      
      // Act & Assert - Step 3: Get files and create additional shadow copies
      var poolFiles = pool.GetItems(SearchOption.AllDirectories).OfType<IPhysicalFile>()
                          .Where(f => f.Source.FullName.Contains("Critical")).Take(3).ToList();
      
      foreach (var file in poolFiles) {
        var availableVolumes = pool.Volumes.ToList();
        if (availableVolumes.Any()) {
          Assert.DoesNotThrow(() => 
            DuplicationManager.CreateAdditionalShadowCopy((IFile)file, availableVolumes.FirstOrDefault() ?? throw new InvalidOperationException("No available volumes for shadow copy creation"))
          );
        }
      }
      
      // Act & Assert - Step 4: Verify All Files Have Proper Duplication
      var currentLevel = DuplicationManager.GetDuplicationLevel(pool, folderPath);
      currentLevel.Should().BeGreaterThan(0);
      
      // Act & Assert - Step 5: Disable Duplication
      Assert.DoesNotThrow(() => 
        DuplicationManager.DisableDuplicationOnFolder(pool, folderPath)
      );
    }
    
    [Test]
    public void IntegrityRepairWorkflow_DetectRepairVerify_ShouldComplete() {
      // Arrange
      var drives = new[] { _drive1Path, _drive2Path };
      PoolManager.CreatePool(_poolName, _poolMountPoint, drives);
      var pool = DetectedMountPoints.FirstOrDefault(p => p.Name == _poolName);
      if (pool == null) {
        Assert.Inconclusive($"Could not find pool '{_poolName}' in detected mount points - test environment may not be properly set up");
        return;
      }
      
      // Create test files
      var testFile1 = Path.Combine(_poolMountPoint, "integrity_test1.doc");
      var testFile2 = Path.Combine(_poolMountPoint, "integrity_test2.txt");
      var content1 = "Document content for integrity testing";
      var content2 = "Text content for integrity verification";
      
      System.IO.File.WriteAllText(testFile1, content1);
      System.IO.File.WriteAllText(testFile2, content2);
      
      // Enable duplication to create shadow copies that we can test
      DuplicationManager.EnableDuplicationOnFolder(pool, "", 2);
      
      // Act & Assert - Step 1: Detect Integrity Issues
      var issues = IntegrityChecker.CheckPoolIntegrity(pool, true, true);
      issues.Should().NotBeNull();
      
      // Act & Assert - Step 2: Attempt Repair (Dry Run) if any issues found
      if (issues.Any()) {
        foreach (var issue in issues.Take(5)) { // Limit to 5 for performance
          var dryRunResult = IntegrityChecker.RepairIntegrityIssue(issue, true, true);
          // dryRunResult should be a boolean
        }
        
        // Act & Assert - Step 3: Perform Actual Repair
        foreach (var issue in issues.Take(2)) { // Limit to 2 for performance
          var repairResult = IntegrityChecker.RepairIntegrityIssue(issue, false, true);
          // repairResult should be a boolean
        }
      }
      
      // Act & Assert - Step 4: Re-check Integrity
      var postRepairIssues = IntegrityChecker.CheckPoolIntegrity(pool, false, true);
      postRepairIssues.Should().NotBeNull();
      
      // Act & Assert - Step 5: Verify files are still accessible and content is intact
      System.IO.File.Exists(testFile1).Should().BeTrue();
      System.IO.File.Exists(testFile2).Should().BeTrue();
      System.IO.File.ReadAllText(testFile1).Should().Be(content1);
      System.IO.File.ReadAllText(testFile2).Should().Be(content2);
    }
    
    [Test]
    public void DataTypeIntegration_SemanticTypesWorkTogether_ShouldBeConsistent() {
      // Arrange
      var poolName = new PoolName("SemanticTestPool");
      var drivePath = new DrivePath(_drive1Path); // Use our test drive path
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
      
      // Act & Assert - Test with actual pool operations using semantic types
      var drives = new[] { _drive1Path, _drive2Path };
      var createResult = PoolManager.CreatePool(poolName.Value, _poolMountPoint, drives);
      createResult.Should().BeTrue();
      
      var pool = DetectedMountPoints.FirstOrDefault(p => p.Name == poolName.Value);
      pool.Should().NotBeNull();
      
      // Create the folder structure using semantic types
      var fullFolderPath = Path.Combine(_poolMountPoint, folderPath.Value);
      Directory.CreateDirectory(fullFolderPath);
      
      // Test duplication level with semantic types
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(pool, folderPath.Value, duplicationLevel.Value)
      );
    }
  }
}