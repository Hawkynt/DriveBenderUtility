using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DivisonM {
  public static class DuplicationManager {
    
    public static void EnableDuplicationOnFolder(DriveBender.IMountPoint mountPoint, string folderPath, int duplicationLevel = 1) {
      if (mountPoint == null)
        throw new ArgumentNullException(nameof(mountPoint));
      
      if (string.IsNullOrWhiteSpace(folderPath))
        throw new ArgumentException("Folder path cannot be empty", nameof(folderPath));
      
      if (duplicationLevel < 1 || duplicationLevel > mountPoint.Volumes.Count() - 1)
        throw new ArgumentException("Invalid duplication level", nameof(duplicationLevel));
      
      try {
        foreach (var volume in mountPoint.Volumes) {
          if (volume is DriveBender.Volume vol) {
            var targetPath = Path.Combine(vol.Root.FullName, folderPath, DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);
            Directory.CreateDirectory(targetPath);
            
            for (int i = 1; i < duplicationLevel; i++) {
              var additionalShadowPath = Path.Combine(vol.Root.FullName, folderPath, $"{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}.{i}");
              Directory.CreateDirectory(additionalShadowPath);
            }
          }
        }
        
        DriveBender.Logger?.Invoke($"Duplication enabled on folder '{folderPath}' with level {duplicationLevel}");
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to enable duplication on folder '{folderPath}': {ex.Message}");
        throw;
      }
    }
    
    public static void DisableDuplicationOnFolder(DriveBender.IMountPoint mountPoint, string folderPath) {
      if (mountPoint == null)
        throw new ArgumentNullException(nameof(mountPoint));
      
      if (string.IsNullOrWhiteSpace(folderPath))
        throw new ArgumentException("Folder path cannot be empty", nameof(folderPath));
      
      try {
        var files = mountPoint.GetItems(folderPath, SearchOption.AllDirectories).OfType<DriveBender.IFile>();
        
        foreach (var file in files) {
          var shadowCopies = GetShadowCopies(file).ToArray();
          foreach (var shadow in shadowCopies) {
            DeleteShadowCopy(shadow);
          }
        }
        
        foreach (var volume in mountPoint.Volumes) {
          if (volume is DriveBender.Volume vol) {
            var shadowPath = Path.Combine(vol.Root.FullName, folderPath, DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);
            if (Directory.Exists(shadowPath)) {
              Directory.Delete(shadowPath, true);
            }
            
            var i = 1;
            string additionalShadowPath;
            do {
              additionalShadowPath = Path.Combine(vol.Root.FullName, folderPath, $"{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}.{i}");
              if (Directory.Exists(additionalShadowPath)) {
                Directory.Delete(additionalShadowPath, true);
                i++;
              } else {
                break;
              }
            } while (true);
          }
        }
        
        DriveBender.Logger?.Invoke($"Duplication disabled on folder '{folderPath}'");
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to disable duplication on folder '{folderPath}': {ex.Message}");
        throw;
      }
    }
    
    public static void SetDuplicationLevel(DriveBender.IMountPoint mountPoint, string folderPath, int newLevel) {
      if (newLevel == 0) {
        DisableDuplicationOnFolder(mountPoint, folderPath);
        return;
      }
      
      var currentLevel = GetDuplicationLevel(mountPoint, folderPath);
      
      if (newLevel > currentLevel) {
        IncreaseduplicationLevel(mountPoint, folderPath, newLevel - currentLevel);
      } else if (newLevel < currentLevel) {
        DecreaseDuplicationLevel(mountPoint, folderPath, currentLevel - newLevel);
      }
    }
    
    public static int GetDuplicationLevel(DriveBender.IMountPoint mountPoint, string folderPath) {
      if (mountPoint == null || string.IsNullOrWhiteSpace(folderPath))
        return 0;
      
      var maxLevel = 0;
      foreach (var volume in mountPoint.Volumes) {
        if (volume is DriveBender.Volume vol) {
          var shadowPath = Path.Combine(vol.Root.FullName, folderPath, DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);
          if (Directory.Exists(shadowPath)) {
            maxLevel = Math.Max(maxLevel, 1);
          }
          
          var i = 1;
          string additionalShadowPath;
          do {
            additionalShadowPath = Path.Combine(vol.Root.FullName, folderPath, $"{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}.{i}");
            if (Directory.Exists(additionalShadowPath)) {
              maxLevel = Math.Max(maxLevel, i + 1);
              i++;
            } else {
              break;
            }
          } while (true);
        }
      }
      
      return maxLevel;
    }
    
    public static void CreateAdditionalShadowCopy(DriveBender.IFile file, DriveBender.IVolume targetVolume) {
      if (file == null)
        throw new ArgumentNullException(nameof(file));
      
      if (targetVolume == null)
        throw new ArgumentNullException(nameof(targetVolume));
      
      var primary = file.Primary;
      if (primary == null) {
        DriveBender.Logger?.Invoke($"Cannot create shadow copy - no primary found for file '{file.FullName}'");
        return;
      }
      
      try {
        if (targetVolume is DriveBender.Volume vol) {
          var existingShadowCount = file.ShadowCopies.Count();
          var shadowFolderName = existingShadowCount == 0 
            ? DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME 
            : $"{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}.{existingShadowCount}";
          
          var parentPath = Path.GetDirectoryName(file.FullName) ?? "";
          var targetDirectory = Path.Combine(vol.Root.FullName, parentPath, shadowFolderName);
          Directory.CreateDirectory(targetDirectory);
          
          var targetFilePath = Path.Combine(targetDirectory, file.Name);
          var tempFilePath = targetFilePath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
          
          var primaryFileLocation = GetPrimaryFileLocation(file);
          if (primaryFileLocation.HasValue) {
            File.Copy(primaryFileLocation.Value.file.FullName, tempFilePath);
            File.Move(tempFilePath, targetFilePath);
            
            DriveBender.Logger?.Invoke($"Additional shadow copy created for '{file.FullName}' on volume '{targetVolume.Name}'");
          }
        }
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to create additional shadow copy: {ex.Message}");
        throw;
      }
    }
    
    private static void IncreaseduplicationLevel(DriveBender.IMountPoint mountPoint, string folderPath, int levelsToAdd) {
      var files = mountPoint.GetItems(folderPath, SearchOption.AllDirectories).OfType<DriveBender.IFile>();
      var availableVolumes = mountPoint.Volumes.ToArray();
      
      foreach (var file in files) {
        for (int i = 0; i < levelsToAdd; i++) {
          var volumeWithSpace = availableVolumes
            .Where(v => !file.ShadowCopies.Contains(v) && v != file.Primary)
            .OrderByDescending(v => v.BytesFree)
            .FirstOrDefault(v => v.BytesFree > file.Size);
          
          if (volumeWithSpace != null) {
            CreateAdditionalShadowCopy(file, volumeWithSpace);
          }
        }
      }
    }
    
    private static void DecreaseDuplicationLevel(DriveBender.IMountPoint mountPoint, string folderPath, int levelsToRemove) {
      var files = mountPoint.GetItems(folderPath, SearchOption.AllDirectories).OfType<DriveBender.IFile>();
      
      foreach (var file in files) {
        var shadowCopies = GetShadowCopies(file).Take(levelsToRemove);
        foreach (var shadow in shadowCopies) {
          DeleteShadowCopy(shadow);
        }
      }
    }
    
    private static IEnumerable<(DriveBender.IVolume volume, FileInfo file)> GetShadowCopies(DriveBender.IFile file) {
      if (file is DriveBender.File f) {
        return GetShadowCopyFileLocations(f);
      }
      return Enumerable.Empty<(DriveBender.IVolume, FileInfo)>();
    }
    
    private static void DeleteShadowCopy((DriveBender.IVolume volume, FileInfo file) shadow) {
      try {
        shadow.file.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);
        shadow.file.Delete();
        
        DriveBender.Logger?.Invoke($"Shadow copy deleted: {shadow.file.FullName}");
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to delete shadow copy '{shadow.file.FullName}': {ex.Message}");
      }
    }
    
    private static (DriveBender.Volume volume, FileInfo file)? GetPrimaryFileLocation(DriveBender.IFile file) {
      if (file is DriveBender.File f) {
        var primary = f.Primary;
        if (primary is DriveBender.Volume vol) {
          var fileInfo = new FileInfo(Path.Combine(vol.Root.FullName, f.FullName));
          if (fileInfo.Exists) {
            return (vol, fileInfo);
          }
        }
      }
      return null;
    }
    
    private static IEnumerable<(DriveBender.IVolume volume, FileInfo file)> GetShadowCopyFileLocations(DriveBender.File file) {
      foreach (var volume in file.ShadowCopies) {
        if (volume is DriveBender.Volume vol) {
          var shadowPath = Path.Combine(vol.Root.FullName, file.ShadowCopyFullName);
          var fileInfo = new FileInfo(shadowPath);
          if (fileInfo.Exists) {
            yield return (volume, fileInfo);
          }
        }
      }
    }
  }
}