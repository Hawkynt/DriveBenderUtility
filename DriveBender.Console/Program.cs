using System;
using System.Linq;
using CommandLine;
using DivisonM;
using IMountPoint = DivisonM.DriveBender.IMountPoint;

namespace DriveBender.Console {
  class Program {
    static int Main(string[] args) {
      DriveBender.Logger = System.Console.WriteLine;
      
      return Parser.Default.ParseArguments<
        ListPoolsOptions,
        CreatePoolOptions,
        DeletePoolOptions,
        AddDriveOptions,
        RemoveDriveOptions,
        EnableDuplicationOptions,
        DisableDuplicationOptions,
        SetDuplicationLevelOptions,
        CheckIntegrityOptions,
        RepairOptions,
        RebalanceOptions,
        InfoOptions
      >(args)
      .MapResult(
        (ListPoolsOptions opts) => ListPools(opts),
        (CreatePoolOptions opts) => CreatePool(opts),
        (DeletePoolOptions opts) => DeletePool(opts),
        (AddDriveOptions opts) => AddDrive(opts),
        (RemoveDriveOptions opts) => RemoveDrive(opts),
        (EnableDuplicationOptions opts) => EnableDuplication(opts),
        (DisableDuplicationOptions opts) => DisableDuplication(opts),
        (SetDuplicationLevelOptions opts) => SetDuplicationLevel(opts),
        (CheckIntegrityOptions opts) => CheckIntegrity(opts),
        (RepairOptions opts) => Repair(opts),
        (RebalanceOptions opts) => Rebalance(opts),
        (InfoOptions opts) => ShowInfo(opts),
        errs => 1
      );
    }
    
    static int ListPools(ListPoolsOptions opts) {
      var pools = DriveBender.DetectedMountPoints;
      
      if (pools.Length == 0) {
        System.Console.WriteLine("No Drive Bender pools found.");
        return 0;
      }
      
      System.Console.WriteLine($"Found {pools.Length} pool(s):\n");
      
      foreach (var pool in pools) {
        System.Console.WriteLine($"Pool: {pool.Name} ({pool.Description})");
        System.Console.WriteLine($"  ID: {pool.Id}");
        System.Console.WriteLine($"  Total Size: {DriveBender.SizeFormatter.Format(pool.BytesTotal)}");
        System.Console.WriteLine($"  Used: {DriveBender.SizeFormatter.Format(pool.BytesUsed)} ({pool.BytesUsed * 100.0 / pool.BytesTotal:F1}%)");
        System.Console.WriteLine($"  Free: {DriveBender.SizeFormatter.Format(pool.BytesFree)}");
        System.Console.WriteLine($"  Volumes ({pool.Volumes.Count()}):");
        
        foreach (var volume in pool.Volumes.OrderBy(v => v.Name)) {
          System.Console.WriteLine($"    - {volume.Name}: {DriveBender.SizeFormatter.Format(volume.BytesFree)} free / {DriveBender.SizeFormatter.Format(volume.BytesTotal)} total");
        }
        System.Console.WriteLine();
      }
      
      return 0;
    }
    
    static int CreatePool(CreatePoolOptions opts) {
      try {
        var success = PoolManager.CreatePool(opts.Name, opts.MountPoint, opts.Drives);
        return success ? 0 : 1;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error creating pool: {ex.Message}");
        return 1;
      }
    }
    
    static int DeletePool(DeletePoolOptions opts) {
      try {
        var success = PoolManager.DeletePool(opts.Name, opts.RemoveData);
        return success ? 0 : 1;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error deleting pool: {ex.Message}");
        return 1;
      }
    }
    
    static int AddDrive(AddDriveOptions opts) {
      try {
        var success = PoolManager.AddDriveToPool(opts.PoolName, opts.DrivePath);
        return success ? 0 : 1;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error adding drive: {ex.Message}");
        return 1;
      }
    }
    
    static int RemoveDrive(RemoveDriveOptions opts) {
      try {
        var success = PoolManager.RemoveDriveFromPool(opts.PoolName, opts.DrivePath, opts.MoveData);
        return success ? 0 : 1;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error removing drive: {ex.Message}");
        return 1;
      }
    }
    
    static int EnableDuplication(EnableDuplicationOptions opts) {
      try {
        var pool = GetPool(opts.PoolName);
        if (pool == null) return 1;
        
        DuplicationManager.EnableDuplicationOnFolder(pool, opts.FolderPath, opts.Level);
        return 0;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error enabling duplication: {ex.Message}");
        return 1;
      }
    }
    
    static int DisableDuplication(DisableDuplicationOptions opts) {
      try {
        var pool = GetPool(opts.PoolName);
        if (pool == null) return 1;
        
        DuplicationManager.DisableDuplicationOnFolder(pool, opts.FolderPath);
        return 0;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error disabling duplication: {ex.Message}");
        return 1;
      }
    }
    
    static int SetDuplicationLevel(SetDuplicationLevelOptions opts) {
      try {
        var pool = GetPool(opts.PoolName);
        if (pool == null) return 1;
        
        DuplicationManager.SetDuplicationLevel(pool, opts.FolderPath, opts.Level);
        return 0;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error setting duplication level: {ex.Message}");
        return 1;
      }
    }
    
    static int CheckIntegrity(CheckIntegrityOptions opts) {
      try {
        var pool = GetPool(opts.PoolName);
        if (pool == null) return 1;
        
        var issues = IntegrityChecker.CheckPoolIntegrity(pool, opts.DeepScan, true);
        var issuesList = issues.ToList();
        
        if (issuesList.Count == 0) {
          System.Console.WriteLine("No integrity issues found.");
          return 0;
        }
        
        System.Console.WriteLine($"Found {issuesList.Count} integrity issue(s):\n");
        
        foreach (var issue in issuesList) {
          System.Console.WriteLine($"File: {issue.FilePath}");
          System.Console.WriteLine($"Issue: {issue.IssueType}");
          System.Console.WriteLine($"Description: {issue.Description}");
          System.Console.WriteLine($"Suggested Action: {issue.SuggestedAction}");
          
          if (issue.FileLocations?.Any() == true) {
            System.Console.WriteLine("File Locations:");
            foreach (var location in issue.FileLocations) {
              System.Console.WriteLine($"  - Volume: {location.Volume.Name}, Shadow: {location.IsShadowCopy}, Hash: {location.Hash ?? "N/A"}");
            }
          }
          System.Console.WriteLine();
        }
        
        return issuesList.Count;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error checking integrity: {ex.Message}");
        return 1;
      }
    }
    
    static int Repair(RepairOptions opts) {
      try {
        var pool = GetPool(opts.PoolName);
        if (pool == null) return 1;
        
        if (opts.All) {
          pool.FixMissingDuplicationOnAllFolders();
          pool.FixDuplicatePrimaries();
          pool.FixDuplicateShadowCopies();
          pool.FixMissingPrimaries();
          pool.FixMissingShadowCopies();
          System.Console.WriteLine("All repair operations completed.");
        } else {
          var issues = IntegrityChecker.CheckPoolIntegrity(pool, opts.DeepScan, false);
          var repairedCount = 0;
          
          System.Console.WriteLine($"Running repair with: DryRun={opts.DryRun}, CreateBackup={!opts.NoBackup}");
          
          foreach (var issue in issues) {
            if (IntegrityChecker.RepairIntegrityIssue(issue, opts.DryRun, !opts.NoBackup)) {
              repairedCount++;
            }
          }
          
          if (opts.DryRun) {
            System.Console.WriteLine($"[DRY RUN] Would repair {repairedCount} integrity issues.");
            System.Console.WriteLine("Use --dry-run false to actually perform repairs.");
          } else {
            System.Console.WriteLine($"Repaired {repairedCount} integrity issues.");
          }
        }
        
        return 0;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error during repair: {ex.Message}");
        return 1;
      }
    }
    
    static int Rebalance(RebalanceOptions opts) {
      try {
        var pool = GetPool(opts.PoolName);
        if (pool == null) return 1;
        
        pool.Rebalance();
        return 0;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error during rebalance: {ex.Message}");
        return 1;
      }
    }
    
    static int ShowInfo(InfoOptions opts) {
      try {
        var pool = GetPool(opts.PoolName);
        if (pool == null) return 1;
        
        System.Console.WriteLine($"Pool Information: {pool.Name}");
        System.Console.WriteLine($"Description: {pool.Description}");
        System.Console.WriteLine($"ID: {pool.Id}");
        System.Console.WriteLine($"Total Capacity: {DriveBender.SizeFormatter.Format(pool.BytesTotal)}");
        System.Console.WriteLine($"Used Space: {DriveBender.SizeFormatter.Format(pool.BytesUsed)} ({pool.BytesUsed * 100.0 / pool.BytesTotal:F1}%)");
        System.Console.WriteLine($"Free Space: {DriveBender.SizeFormatter.Format(pool.BytesFree)}");
        System.Console.WriteLine();
        
        System.Console.WriteLine($"Volumes ({pool.Volumes.Count()}):");
        foreach (var volume in pool.Volumes.OrderBy(v => v.Name)) {
          System.Console.WriteLine($"  {volume.Name} ({volume.Label}):");
          System.Console.WriteLine($"    Total: {DriveBender.SizeFormatter.Format(volume.BytesTotal)}");
          System.Console.WriteLine($"    Used: {DriveBender.SizeFormatter.Format(volume.BytesUsed)} ({volume.BytesUsed * 100.0 / volume.BytesTotal:F1}%)");
          System.Console.WriteLine($"    Free: {DriveBender.SizeFormatter.Format(volume.BytesFree)}");
          System.Console.WriteLine();
        }
        
        if (!string.IsNullOrEmpty(opts.FolderPath)) {
          var level = DuplicationManager.GetDuplicationLevel(pool, opts.FolderPath);
          System.Console.WriteLine($"Duplication level for '{opts.FolderPath}': {level}");
        }
        
        return 0;
      } catch (Exception ex) {
        System.Console.WriteLine($"Error getting info: {ex.Message}");
        return 1;
      }
    }
    
    private static DriveBender.IMountPoint GetPool(string poolName) {
      var pools = DriveBender.DetectedMountPoints;
      var pool = pools.FirstOrDefault(p => string.Equals(p.Name, poolName, StringComparison.OrdinalIgnoreCase));
      
      if (pool == null) {
        System.Console.WriteLine($"Pool '{poolName}' not found.");
        System.Console.WriteLine("Available pools:");
        foreach (var p in pools) {
          System.Console.WriteLine($"  - {p.Name}");
        }
      }
      
      return pool;
    }
  }
}