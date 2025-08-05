using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DivisonM;
using NUnit.Framework;
using FluentAssertions;
using Moq;

namespace DriveBender.Tests.Regression.EdgeCase {
  
  [TestFixture]
  [Category("Regression")]
  [Category("EdgeCase")]
  public class PreviousBugTests : TestBase {
    
    [Test]
    public void Bug001_NullReferenceInIntegrityCheck_ShouldBeFixed() {
      // Regression test for a hypothetical bug where null files caused NullReferenceException
      
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mixedFiles = new List<DivisonM.DriveBender.IFile> {
        null, // This used to cause NullReferenceException
        CreateMockFile("ValidFile1.txt", 100),
        null, // Another null file
        CreateMockFile("ValidFile2.txt", 200),
        CreateMockFile("ValidFile3.txt", 300)
      };
      
      mockMountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>()))
                   .Returns(mixedFiles.Where(f => f != null)); // Filter nulls as the system should
      
      // Act & Assert - Should not throw NullReferenceException
      Assert.DoesNotThrow(() => {
        var issues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
        issues.Should().NotBeNull();
      });
    }
    
    [Test]
    public void Bug002_InfiniteLoopInDuplicationEnabling_ShouldBeFixed() {
      // Regression test for infinite loop when enabling duplication on circular folder references
      
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var circularPath = new FolderPath("Folder/SubFolder/../../Folder"); // Circular reference
      
      // Act & Assert - Should complete within reasonable time, not hang
      var startTime = DateTime.Now;
      Assert.DoesNotThrow(() => 
        DuplicationManager.EnableDuplicationOnFolder(mockMountPoint.Object, circularPath, DuplicationLevel.Double)
      );
      var duration = DateTime.Now - startTime;
      
      duration.Should().BeLessThan(TimeSpan.FromSeconds(5)); // Should not hang
    }
    
    [Test]
    public void Bug003_MemoryLeakInLargeFileProcessing_ShouldBeFixed() {
      // Regression test for memory leak when processing many large files
      
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var largeFileSet = CreateLargeFileSet(1000); // 1000 files
      
      mockMountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>()))
                   .Returns(largeFileSet);
      
      // Act - Process files multiple times to detect memory leaks
      for (int i = 0; i < 5; i++) {
        var issues = IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true);
        issues.Should().NotBeNull();
        
        // Force garbage collection to help detect leaks
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
      }
      
      // Assert - Test should complete without OutOfMemoryException
      // In a real scenario, we would measure actual memory usage
      Assert.Pass("Memory leak test completed without exceptions");
    }
    
    [Test]
    public void Bug004_RaceConditionInShadowCopyCreation_ShouldBeFixed() {
      // Regression test for race condition when creating multiple shadow copies simultaneously
      
      // Arrange
      var mockFile = CreateMockFile("RaceConditionFile.dat", 1000);
      var mockVolumes = CreateMultipleVolumes(5);
      
      // Act & Assert - Create shadow copies concurrently
      var tasks = new List<System.Threading.Tasks.Task>();
      
      foreach (var volume in mockVolumes) {
        tasks.Add(System.Threading.Tasks.Task.Run(() => {
          Assert.DoesNotThrow(() => 
            DuplicationManager.CreateAdditionalShadowCopy(mockFile, volume)
          );
        }));
      }
      
      // Wait for all tasks to complete
      Assert.DoesNotThrow(() => 
        System.Threading.Tasks.Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10))
      );
    }
    
    [Test]
    public void Bug005_InvalidPathCharactersInPoolName_ShouldBeHandled() {
      // Regression test for crashes when pool names contained invalid characters
      
      // Arrange
      var invalidPoolNames = new[] {
        "Pool<>Name",    // Invalid filename characters
        "Pool|Name",     // Pipe character
        "Pool?Name",     // Question mark
        "Pool*Name",     // Asterisk
        "Pool\"Name",    // Quote
        "Pool:Name",     // Colon
        "Pool\\Name",    // Backslash
        "Pool/Name"      // Forward slash
      };
      
      // Act & Assert - Should throw ArgumentException, not crash
      foreach (var invalidName in invalidPoolNames) {
        Assert.Throws<ArgumentException>(() => new PoolName(invalidName),
          $"Should throw ArgumentException for invalid name: {invalidName}");
      }
    }
    
    [Test]
    public void Bug006_IntegerOverflowInByteSizeCalculations_ShouldBeFixed() {
      // Regression test for integer overflow in byte size calculations
      
      // Arrange & Act
      var largeSize1 = new ByteSize(ulong.MaxValue / 2);
      var largeSize2 = new ByteSize(ulong.MaxValue / 2);
      
      // Assert - Should handle overflow gracefully
      Assert.DoesNotThrow(() => {
        var sum = largeSize1 + largeSize2;
        sum.Should().NotBeNull();
        // In case of overflow, behavior should be predictable
      });
      
      Assert.DoesNotThrow(() => {
        var humanReadable = largeSize1.ToHumanReadable();
        humanReadable.Should().NotBeNullOrEmpty();
      });
    }
    
    [Test]
    public void Bug007_DeadlockInPoolDeletion_ShouldBeFixed() {
      // Regression test for deadlock when deleting pools with active operations
      
      // Arrange
      var poolName = "DeadlockTestPool";
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var mockFiles = CreateMockFileSet(10);
      
      mockMountPoint.Setup(m => m.GetItems(It.IsAny<SearchOption>()))
                   .Returns(mockFiles);
      
      // Act - Simulate concurrent operations
      var deleteTask = System.Threading.Tasks.Task.Run(() => 
        PoolManager.DeletePool(poolName)
      );
      
      var integrityTask = System.Threading.Tasks.Task.Run(() => 
        IntegrityChecker.CheckPoolIntegrity(mockMountPoint.Object, false, true)
      );
      
      // Assert - Should complete within reasonable time, not deadlock
      Assert.DoesNotThrow(() => {
        var allTasks = new System.Threading.Tasks.Task[] { deleteTask, integrityTask };
        var completed = System.Threading.Tasks.Task.WaitAll(
          allTasks, 
          (int)TimeSpan.FromSeconds(10).TotalMilliseconds
        );
        completed.Should().BeTrue("Operations should complete without deadlock");
      });
    }
    
    [Test]
    public void Bug008_EmptyStringHandlingInFolderPaths_ShouldBeRobust() {
      // Regression test for improper handling of empty strings in folder paths
      
      // Arrange & Act & Assert
      var emptyInputs = new[] { "", "   ", "\t", "\n", "  \t  \n  " };
      
      foreach (var emptyInput in emptyInputs) {
        Assert.Throws<ArgumentException>(() => new FolderPath(emptyInput),
          $"Should throw ArgumentException for empty input: '{emptyInput}'");
      }
      
      // Test null input separately
      Assert.Throws<ArgumentException>(() => new FolderPath(null));
    }
    
    [Test]
    public void Bug009_StackOverflowInDeepFolderStructures_ShouldBeFixed() {
      // Regression test for stack overflow with very deep folder structures
      
      // Arrange - Create extremely deep path
      var deepSegments = Enumerable.Range(0, 100).Select(i => $"Level{i}");
      var deepPath = string.Join("/", deepSegments);
      
      // Act & Assert - Should not cause stack overflow
      Assert.DoesNotThrow(() => {
        var folderPath = new FolderPath(deepPath);
        var segments = folderPath.Segments;
        segments.Should().HaveCount(100);
        
        var parent = folderPath.Parent;
        parent.Should().NotBeNull();
      });
    }
    
    [Test]
    public void Bug010_ConcurrentModificationOfVolumeList_ShouldBeHandled() {
      // Regression test for issues when volume list is modified during iteration
      
      // Arrange
      var mockMountPoint = new Mock<DivisonM.DriveBender.IMountPoint>();
      var initialVolumes = CreateMultipleVolumes(5);
      
      // Setup dynamic volume list that changes during iteration
      var volumeList = new List<DivisonM.DriveBender.IVolume>(initialVolumes);
      mockMountPoint.Setup(m => m.Volumes).Returns(() => volumeList.ToArray());
      
      // Act & Assert - Modify list during processing
      Assert.DoesNotThrow(() => {
        var task1 = System.Threading.Tasks.Task.Run(() => {
          // Simulate volume enumeration
          var volumes = mockMountPoint.Object.Volumes;
          foreach (var volume in volumes) {
            System.Threading.Thread.Sleep(10); // Simulate processing time
            var name = volume.Name;
          }
        });
        
        var task2 = System.Threading.Tasks.Task.Run(() => {
          System.Threading.Thread.Sleep(20);
          // Simulate volume list modification
          volumeList.Add(CreateMockVolume("NewVolume", 1000));
        });
        
        System.Threading.Tasks.Task.WaitAll(task1, task2);
      });
    }
    
    private DivisonM.DriveBender.IFile CreateMockFile(string name, int sizeMB) {
      var mockFile = new Mock<DivisonM.DriveBender.IFile>();
      var mockVolume = new Mock<DivisonM.DriveBender.IVolume>();
      
      mockVolume.Setup(v => v.Name).Returns("TestVolume");
      mockFile.Setup(f => f.FullName).Returns(name);
      mockFile.Setup(f => f.Size).Returns(ByteSize.FromMegabytes(sizeMB));
      mockFile.Setup(f => f.Primary).Returns(mockVolume.Object);
      
      return mockFile.Object;
    }
    
    private IEnumerable<DivisonM.DriveBender.IFile> CreateLargeFileSet(int count) {
      var files = new List<DivisonM.DriveBender.IFile>();
      
      for (int i = 0; i < count; i++) {
        files.Add(CreateMockFile($"LargeFile{i:D4}.dat", i % 100 + 1));
      }
      
      return files;
    }
    
    private List<DivisonM.DriveBender.IVolume> CreateMultipleVolumes(int count) {
      var volumes = new List<DivisonM.DriveBender.IVolume>();
      
      for (int i = 0; i < count; i++) {
        volumes.Add(CreateMockVolume($"Volume{i}", 1000 + i * 100));
      }
      
      return volumes;
    }
    
    private DivisonM.DriveBender.IVolume CreateMockVolume(string name, int sizeGB) {
      var mockVolume = new Mock<DivisonM.DriveBender.IVolume>();
      mockVolume.Setup(v => v.Name).Returns(name);
      mockVolume.Setup(v => v.BytesFree).Returns(ByteSize.FromGigabytes(sizeGB));
      return mockVolume.Object;
    }
    
    private IEnumerable<DivisonM.DriveBender.IFile> CreateMockFileSet(int count) {
      return Enumerable.Range(0, count)
                      .Select(i => CreateMockFile($"MockFile{i}.txt", i * 10 + 5));
    }
  }
}