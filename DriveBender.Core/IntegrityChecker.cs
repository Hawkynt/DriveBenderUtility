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
      
      try {
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
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Error during pool integrity check: {ex.Message}");
        issues.Add(new IntegrityIssue {
          FilePath = "Pool Structure",
          IssueType = IntegrityIssueType.AccessDenied,
          Description = $"Cannot access pool structure: {ex.Message}",
          SuggestedAction = "Check pool mount point and accessibility"
        });
      }
      
      DriveBender.Logger?.Invoke($"Integrity check completed. Found {issues.Count} issues.");
      return issues;
    }
    
    public static IEnumerable<IntegrityIssue> CheckFileIntegrity(DriveBender.IFile file, bool deepScan = false) {
      var issues = new List<IntegrityIssue>();
      
      if (file == null) {
        return issues; // Return empty list for null file
      }
      
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
      if (issue == null) {
        DriveBender.Logger?.Invoke("Cannot repair null integrity issue");
        return false;
      }
      
      if (dryRun) {
        switch (issue.IssueType) {
          case IntegrityIssueType.MissingPrimary:
          case IntegrityIssueType.MissingShadowCopy:
          case IntegrityIssueType.DuplicatePrimary:
          case IntegrityIssueType.DuplicateShadowCopy:
          case IntegrityIssueType.HashMismatch:
          case IntegrityIssueType.CorruptedFile:
          case IntegrityIssueType.OrphanedShadowCopy:
            DriveBender.Logger?.Invoke($"[DRY RUN] Would repair {issue.IssueType} for '{issue.FilePath}'");
            return true;
          default:
            DriveBender.Logger?.Invoke($"[DRY RUN] Cannot repair issue type: {issue.IssueType}");
            return false;
        }
      }
      
      try {
        // a repair that was promised a backup and could not get one must not go ahead without it
        if (createBackup && !CreateBackupBeforeRepair(issue))
          return false;
        
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
    
    private static bool CreateBackupBeforeRepair(IntegrityIssue issue) {
      try {
        var backupDir = Path.Combine(Path.GetTempPath(), "DriveBenderBackups", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(backupDir);
        
        if (issue.FileLocations != null) {
          var index = 0;
          foreach (var location in issue.FileLocations) {
            location.FileInfo.Refresh();
            if (!location.FileInfo.Exists)
              continue;

            // a volume name such as "D:" would make the combined path drive-relative and put the
            // "backup" on the very disk being repaired, so it is reduced to plain characters first
            var volumeName = new string((location.Volume?.Name ?? "volume").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            var backupFile = Path.Combine(backupDir, $"{index++}_{volumeName}_{(location.IsShadowCopy ? "shadow" : "primary")}_{location.FileInfo.Name}");
            DriveBender.CopyDurably(location.FileInfo.FullName, backupFile);
            DriveBender.Logger?.Invoke($"Backup created: {backupFile}");
          }
        }

        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Could not create backup for '{issue.FilePath}', repair skipped: {ex.Message}");
        return false;
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
      
      if (!(shadowCopy.Volume is DriveBender.Volume volume)) {
        DriveBender.Logger?.Invoke($"Cannot repair missing primary for '{issue.FilePath}': unknown volume type");
        return false;
      }

      // the primary belongs at the file's own path inside the pool folder of that volume
      var targetPath = Path.Combine(volume.Root.FullName, issue.FilePath);
      if (File.Exists(targetPath)) {
        DriveBender.Logger?.Invoke($"Missing primary for '{issue.FilePath}' already exists at '{targetPath}'");
        return true;
      }

      var tempPath = targetPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
        if (File.Exists(tempPath))
          File.Delete(tempPath);

        DriveBender.CopyDurably(shadowCopy.FileInfo.FullName, tempPath);
        File.Move(tempPath, targetPath);
      } finally {
        if (File.Exists(tempPath))
          File.Delete(tempPath);
      }

      DriveBender.Logger?.Invoke($"Repaired missing primary for '{issue.FilePath}' by copying the shadow copy to '{targetPath}'");
      return true;
    }
    
    private static bool RepairMissingShadowCopy(IntegrityIssue issue) {
      DriveBender.Logger?.Invoke($"Repair missing shadow copy not implemented for '{issue.FilePath}'");
      return false;
    }
    
    private static bool RepairDuplicatePrimary(IntegrityIssue issue)
      => _RemoveRedundantCopies(issue, issue.FileLocations?.Where(l => !l.IsShadowCopy).ToArray(), "primary");

    private static bool RepairDuplicateShadowCopy(IntegrityIssue issue)
      => _RemoveRedundantCopies(issue, issue.FileLocations?.Where(l => l.IsShadowCopy).ToArray(), "shadow copy");

    /// <summary>
    /// Removes copies that hold exactly the same bytes as the one kept, and nothing else.
    ///
    /// The newest timestamp says nothing about which copy is right: a restored backup keeps its old
    /// time, and a copy can rot without its time changing. A duplicate whose content differs is
    /// therefore a conflict for the user, and both sides are kept. The hashes are taken again here
    /// rather than trusted from the scan, because the files may have changed since.
    /// </summary>
    private static bool _RemoveRedundantCopies(IntegrityIssue issue, FileLocation[] copies, string role) {
      if (copies == null || copies.Length <= 1)
        return true;

      var current = copies
        .Select(c => { c.FileInfo.Refresh(); return c; })
        .Where(c => c.FileInfo.Exists)
        .Select(c => (location: c, hash: CalculateFileHash(c.FileInfo)))
        .ToArray();

      if (current.Length <= 1)
        return true;

      var keep = current.Where(c => c.hash != null).OrderByDescending(c => c.location.FileInfo.LastWriteTimeUtc).FirstOrDefault();
      if (keep.location == null) {
        DriveBender.Logger?.Invoke($"Cannot repair duplicate {role} for '{issue.FilePath}': no copy could be read");
        return false;
      }

      var allRemoved = true;
      foreach (var duplicate in current.Where(c => c.location != keep.location)) {
        if (duplicate.hash == null || !string.Equals(duplicate.hash, keep.hash, StringComparison.Ordinal)) {
          DriveBender.Logger?.Invoke($"Kept duplicate {role} for '{issue.FilePath}' on volume '{duplicate.location.Volume?.Name}': its content differs from the copy on '{keep.location.Volume?.Name}' and needs a manual decision");
          allRemoved = false;
          continue;
        }

        duplicate.location.FileInfo.Delete();
        DriveBender.Logger?.Invoke($"Removed duplicate {role} for '{issue.FilePath}' from volume '{duplicate.location.Volume?.Name}'");
      }

      return allRemoved;
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
      if (corruptedLocation == null || corruptedLocation == sourceLocation || !string.IsNullOrEmpty(corruptedLocation.Hash)) {
        DriveBender.Logger?.Invoke($"Cannot repair corrupted file '{issue.FilePath}': no unreadable copy distinct from a good one");
        return false;
      }

      var tempPath = corruptedLocation.FileInfo.FullName + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
      try {
        // the replacement is complete and on the disk before the damaged copy is given up
        if (File.Exists(tempPath))
          File.Delete(tempPath);

        DriveBender.CopyDurably(sourceLocation.FileInfo.FullName, tempPath);
        corruptedLocation.FileInfo.Delete();
        File.Move(tempPath, corruptedLocation.FileInfo.FullName);
        DriveBender.Logger?.Invoke($"Repaired corrupted file '{issue.FilePath}' by copying from volume '{sourceLocation.Volume.Name}'");
        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to repair corrupted file '{issue.FilePath}': {ex.Message}");
        return false;
      }
    }
    
    /// <summary>
    /// A shadow copy without a primary is the file's only copy, so it is promoted rather than deleted.
    /// </summary>
    private static bool RepairOrphanedShadowCopy(IntegrityIssue issue) {
      var orphanedShadow = issue.FileLocations?.FirstOrDefault(l => l.IsShadowCopy);
      if (orphanedShadow == null)
        return false;

      return RepairMissingPrimary(issue);
    }
  }
}