using System;
using NUnit.Framework;
using DivisonM;

namespace DriveBender.Tests {
  
  [SetUpFixture]
  public class TestSetup {
    
    [OneTimeSetUp]
    public void GlobalSetup() {
      // Configure global test settings
      DriveBender.Logger = message => {
        TestContext.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
      };
      
      TestContext.WriteLine("DriveBender Test Suite initialized");
      TestContext.WriteLine($"Test run started at: {DateTime.Now}");
    }
    
    [OneTimeTearDown]
    public void GlobalTeardown() {
      TestContext.WriteLine($"Test run completed at: {DateTime.Now}");
      
      // Reset logger to prevent issues with other tests
      DriveBender.Logger = null;
    }
  }
  
  /// <summary>
  /// Base class for integration tests that need real file system operations
  /// </summary>
  public abstract class IntegrationTestBase {
    
    protected string TestDirectory { get; private set; }
    
    [SetUp]
    public virtual void SetUp() {
      TestDirectory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), 
        $"DriveBenderIntegrationTest_{Guid.NewGuid():N}"
      );
      
      System.IO.Directory.CreateDirectory(TestDirectory);
      TestContext.WriteLine($"Created test directory: {TestDirectory}");
    }
    
    [TearDown]
    public virtual void TearDown() {
      if (!string.IsNullOrEmpty(TestDirectory) && System.IO.Directory.Exists(TestDirectory)) {
        try {
          System.IO.Directory.Delete(TestDirectory, true);
          TestContext.WriteLine($"Cleaned up test directory: {TestDirectory}");
        } catch (Exception ex) {
          TestContext.WriteLine($"Warning: Could not clean up test directory {TestDirectory}: {ex.Message}");
        }
      }
    }
    
    protected void CreateTestFile(string relativePath, string content = "Test content") {
      var fullPath = System.IO.Path.Combine(TestDirectory, relativePath);
      var directory = System.IO.Path.GetDirectoryName(fullPath);
      
      if (!string.IsNullOrEmpty(directory)) {
        System.IO.Directory.CreateDirectory(directory);
      }
      
      System.IO.File.WriteAllText(fullPath, content);
    }
    
    protected void CreateTestDirectory(string relativePath) {
      var fullPath = System.IO.Path.Combine(TestDirectory, relativePath);
      System.IO.Directory.CreateDirectory(fullPath);
    }
    
    protected string GetTestPath(string relativePath) {
      return System.IO.Path.Combine(TestDirectory, relativePath);
    }
  }
}