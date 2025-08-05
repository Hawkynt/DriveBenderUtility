using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace DivisonM {
  public static class IntegrityChecker {
    
    public class IntegrityIssue {
      public string FilePath { get; set; }
      public IntegrityIssueType IssueType { get; set; }
      public string Description { get; set; }
      public IEnumerable<FileLocation> FileLocations { get; set; }
      public string SuggestedAction { get; set; }
    }
    
    public class FileLocation {
      public DriveBender.IVolume Volume { get; set; }
      public FileInfo FileInfo { get; set; }
      public bool IsShadowCopy { get; set; }
      public string Hash { get; set; }
    }
    
    public enum IntegrityIssueType {
      MissingPrimary,
      MissingShadowCopy,
      CorruptedFile,
      HashMismatch,
      DuplicatePrimary,
      DuplicateShadowCopy,
      OrphanedShadowCopy,
      AccessDenied
    }
    
    public static IEnumerable<IntegrityIssue> CheckPoolIntegrity(DriveBender.IMountPoint mountPoint, bool deepScan = false, bool dryRun = true) {
      if (mountPoint == null)
        throw new ArgumentNullException(nameof(mountPoint));
      
      DriveBender.Logger?.Invoke($"Starting integrity check for pool '{mountPoint.Name}' (Deep scan: {deepScan}, Dry run: {dryRun})");
      
      var issues = new List<IntegrityIssue>();
      var files = mountPoint.GetItems(SearchOption.AllDirectories).OfType<DriveBender.IFile>();
      
      foreach (var file in files) {
        try {
          var fileIssues = CheckFileIntegrity(file, deepScan);
          issues.AddRange(fileIssues);
        } catch (Exception ex) {
          issues.Add(new IntegrityIssue {
            FilePath = file.FullName,
            IssueType = IntegrityIssueType.AccessDenied,
            Description = $"Cannot access file: {ex.Message}",
            SuggestedAction = "Check file permissions and accessibility"
          });
        }
      }
      
      DriveBender.Logger?.Invoke($"Integrity check completed. Found {issues.Count} issues.");
      return issues;
    }
    
    public static IEnumerable<IntegrityIssue> CheckFileIntegrity(DriveBender.IFile file, bool deepScan = false) {
      var issues = new List<IntegrityIssue>();
      var locations = GetAllFileLocations(file).ToArray();
      
      if (locations.Length == 0) {
        issues.Add(new IntegrityIssue {
          FilePath = file.FullName,
          IssueType = IntegrityIssueType.MissingPrimary,
          Description = "File has no primary or shadow copies",
          SuggestedAction = "File appears to be completely missing - remove from pool index"
        });
        return issues;
      }
      
      var primaries = locations.Where(l => !l.IsShadowCopy).ToArray();
      var shadows = locations.Where(l => l.IsShadowCopy).ToArray();
      
      if (primaries.Length == 0) {
        issues.Add(new IntegrityIssue {
          FilePath = file.FullName,
          IssueType = IntegrityIssueType.MissingPrimary,
          Description = "File has no primary copy",
          FileLocations = shadows,
          SuggestedAction = "Promote one shadow copy to primary"
        });
      } else if (primaries.Length > 1) {
        issues.Add(new IntegrityIssue {
          FilePath = file.FullName,
          IssueType = IntegrityIssueType.DuplicatePrimary,
          Description = $"File has {primaries.Length} primary copies",
          FileLocations = primaries,
          SuggestedAction = "Keep one primary copy and remove duplicates"
        });
      }
      
      if (deepScan && locations.Length > 1) {
        var hashGroups = locations.GroupBy(l => l.Hash).ToArray();
        
        if (hashGroups.Length > 1) {
          issues.Add(new IntegrityIssue {
            FilePath = file.FullName,
            IssueType = IntegrityIssueType.HashMismatch,
            Description = "File copies have different content",
            FileLocations = locations,
            SuggestedAction = "Compare file contents and keep the correct version"
          });
        }
        
        foreach (var location in locations) {
          if (string.IsNullOrEmpty(location.Hash)) {
            issues.Add(new IntegrityIssue {
              FilePath = file.FullName,
              IssueType = IntegrityIssueType.CorruptedFile,
              Description = $"Cannot read file on volume {location.Volume.Name}",
              FileLocations = new[] { location },
              SuggestedAction = "File may be corrupted - restore from other copies"
            });
          }
        }
      }
      
      return issues;
    }
    
    public static bool RepairIntegrityIssue(IntegrityIssue issue, bool dryRun = true, bool createBackup = true) {
      if (dryRun) {
        DriveBender.Logger?.Invoke($"[DRY RUN] Would repair {issue.IssueType} for '{issue.FilePath}'");
        return true;
      }
      
      try {
        if (createBackup) {
          CreateBackupBeforeRepair(issue);
        }
        
        switch (issue.IssueType) {
          case IntegrityIssueType.MissingPrimary:
            return RepairMissingPrimary(issue);
          
          case IntegrityIssueType.MissingShadowCopy:
            return RepairMissingShadowCopy(issue);
          
          case IntegrityIssueType.DuplicatePrimary:
            return RepairDuplicatePrimary(issue);
          
          case IntegrityIssueType.DuplicateShadowCopy:
            return RepairDuplicateShadowCopy(issue);
          
          case IntegrityIssueType.HashMismatch:
            return RepairHashMismatch(issue);
          
          case IntegrityIssueType.CorruptedFile:
            return RepairCorruptedFile(issue);
          
          case IntegrityIssueType.OrphanedShadowCopy:
            return RepairOrphanedShadowCopy(issue);
          
          default:
            DriveBender.Logger?.Invoke($"Cannot auto-repair issue type: {issue.IssueType}");
            return false;
        }
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to repair integrity issue for '{issue.FilePath}': {ex.Message}");
        return false;
      }
    }
    
    private static void CreateBackupBeforeRepair(IntegrityIssue issue) {
      try {
        var backupDir = Path.Combine(Path.GetTempPath(), "DriveBenderBackups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupDir);
        
        if (issue.FileLocations != null) {
          foreach (var location in issue.FileLocations) {
            if (location.FileInfo.Exists) {
              var backupFile = Path.Combine(backupDir, $"{location.Volume.Name}_{location.FileInfo.Name}");
              location.FileInfo.CopyTo(backupFile);
              DriveBender.Logger?.Invoke($"Backup created: {backupFile}");
            }
          }
        }
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Warning: Could not create backup for '{issue.FilePath}': {ex.Message}");
      }
    }
    
    private static IEnumerable<FileLocation> GetAllFileLocations(DriveBender.IFile file) {
      var locations = new List<FileLocation>();
      
      if (file is DriveBender.File f) {
        var primaries = f.Primaries.OfType<DriveBender.Volume>();
        foreach (var volume in primaries) {
          var fileInfo = new FileInfo(Path.Combine(volume.Root.FullName, f.FullName));
          if (fileInfo.Exists) {
            locations.Add(new FileLocation {
              Volume = volume,
              FileInfo = fileInfo,
              IsShadowCopy = false,
              Hash = CalculateFileHash(fileInfo)
            });
          }
        }
        
        var shadows = f.ShadowCopies.OfType<DriveBender.Volume>();
        foreach (var volume in shadows) {
          var fileInfo = new FileInfo(Path.Combine(volume.Root.FullName, f.ShadowCopyFullName));
          if (fileInfo.Exists) {
            locations.Add(new FileLocation {
              Volume = volume,
              FileInfo = fileInfo,
              IsShadowCopy = true,
              Hash = CalculateFileHash(fileInfo)
            });
          }
        }
      }
      
      return locations;
    }
    
    private static string CalculateFileHash(FileInfo fileInfo) {
      try {
        using (var stream = fileInfo.OpenRead())
        using (var sha256 = SHA256.Create()) {
          var hash = sha256.ComputeHash(stream);
          return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to calculate hash for '{fileInfo.FullName}': {ex.Message}");
        return null;
      }
    }
    
    private static bool RepairMissingPrimary(IntegrityIssue issue) {
      var shadowCopy = issue.FileLocations?.FirstOrDefault(l => l.IsShadowCopy);
      if (shadowCopy == null) {
        DriveBender.Logger?.Invoke($"Cannot repair missing primary - no shadow copy available for '{issue.FilePath}'");
        return false;
      }
      
      var parentDir = Path.GetDirectoryName(issue.FilePath);
      var fileName = Path.GetFileName(issue.FilePath);
      var targetPath = Path.Combine(shadowCopy.Volume.Name, parentDir ?? "", fileName);
      
      File.Copy(shadowCopy.FileInfo.FullName, targetPath);
      DriveBender.Logger?.Invoke($"Repaired missing primary for '{issue.FilePath}' by promoting shadow copy");
      return true;
    }
    
    private static bool RepairMissingShadowCopy(IntegrityIssue issue) {
      DriveBender.Logger?.Invoke($"Repair missing shadow copy not implemented for '{issue.FilePath}'");
      return false;
    }
    
    private static bool RepairDuplicatePrimary(IntegrityIssue issue) {
      var primaries = issue.FileLocations?.Where(l => !l.IsShadowCopy).ToArray();
      if (primaries?.Length <= 1) return true;
      
      var bestPrimary = primaries.OrderByDescending(p => p.FileInfo.LastWriteTime).First();
      
      foreach (var duplicate in primaries.Where(p => p != bestPrimary)) {
        duplicate.FileInfo.Delete();
        DriveBender.Logger?.Invoke($"Removed duplicate primary for '{issue.FilePath}' from volume '{duplicate.Volume.Name}'");
      }
      
      return true;
    }
    
    private static bool RepairDuplicateShadowCopy(IntegrityIssue issue) {
      var shadows = issue.FileLocations?.Where(l => l.IsShadowCopy).ToArray();
      if (shadows?.Length <= 1) return true;
      
      var bestShadow = shadows.OrderByDescending(s => s.FileInfo.LastWriteTime).First();
      
      foreach (var duplicate in shadows.Where(s => s != bestShadow)) {
        duplicate.FileInfo.Delete();
        DriveBender.Logger?.Invoke($"Removed duplicate shadow copy for '{issue.FilePath}' from volume '{duplicate.Volume.Name}'");
      }
      
      return true;
    }
    
    private static bool RepairHashMismatch(IntegrityIssue issue) {
      DriveBender.Logger?.Invoke($"Hash mismatch repair requires manual intervention for '{issue.FilePath}'");
      return false;
    }
    
    private static bool RepairCorruptedFile(IntegrityIssue issue) {
      var corruptedLocation = issue.FileLocations?.FirstOrDefault();
      var goodLocations = issue.FileLocations?
        .Where(l => !string.IsNullOrEmpty(l.Hash))
        .ToArray();
      
      if (goodLocations?.Length == 0) {
        DriveBender.Logger?.Invoke($"Cannot repair corrupted file - no good copies available for '{issue.FilePath}'");
        return false;
      }
      
      var sourceLocation = goodLocations.First();
      try {
        corruptedLocation.FileInfo.Delete();
        File.Copy(sourceLocation.FileInfo.FullName, corruptedLocation.FileInfo.FullName);
        DriveBender.Logger?.Invoke($"Repaired corrupted file '{issue.FilePath}' by copying from volume '{sourceLocation.Volume.Name}'");
        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to repair corrupted file '{issue.FilePath}': {ex.Message}");
        return false;
      }
    }
    
    private static bool RepairOrphanedShadowCopy(IntegrityIssue issue) {
      var orphanedShadow = issue.FileLocations?.FirstOrDefault();
      if (orphanedShadow != null) {
        orphanedShadow.FileInfo.Delete();
        DriveBender.Logger?.Invoke($"Removed orphaned shadow copy '{issue.FilePath}' from volume '{orphanedShadow.Volume.Name}'");
        return true;
      }
      return false;
    }
  }
}