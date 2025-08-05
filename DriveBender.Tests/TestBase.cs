using System;
using NUnit.Framework;

namespace DriveBender.Tests {
  
  /// <summary>
  /// Base class for all tests providing common setup and utilities
  /// </summary>
  public abstract class TestBase {
    
    [SetUp]
    public virtual void SetUp() {
      // Common setup for all tests
    }
    
    [TearDown]
    public virtual void TearDown() {
      // Common cleanup for all tests
    }
    
    protected static void AssertWithinTimespan(Action action, TimeSpan maxDuration) {
      var startTime = DateTime.Now;
      action();
      var actualDuration = DateTime.Now - startTime;
      
      if (actualDuration > maxDuration) {
        Assert.Fail($"Operation took {actualDuration.TotalMilliseconds}ms, expected less than {maxDuration.TotalMilliseconds}ms");
      }
    }
  }
}