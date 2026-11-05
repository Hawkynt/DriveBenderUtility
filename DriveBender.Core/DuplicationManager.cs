using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DivisonM {
  public static class DuplicationManager {
    
    public static void EnableDuplicationOnFolder(DriveBender.IMountPoint mountPoint, string folderPath, int duplicationLevel = 1) {
      EnableDuplicationOnFolder(mountPoint, new FolderPath(folderPath), new DuplicationLevel(duplicationLevel));
    }
    
    public static void EnableDuplicationOnFolder(DriveBender.IMountPoint mountPoint, FolderPath folderPath, DuplicationLevel duplicationLevel) {
      if (mountPoint == null)
        throw new ArgumentNullException(nameof(mountPoint));
      
      if (duplicationLevel.Value < 1 || duplicationLevel.Value > mountPoint.Volumes.Count())
        throw new ArgumentException("Invalid duplication level", nameof(duplicationLevel));
      
      try {
        foreach (var volume in mountPoint.Volumes) {
          if (volume is DriveBender.Volume vol) {
            var targetPath = Path.Combine(vol.Root.FullName, folderPath.Value, DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);
            Directory.CreateDirectory(targetPath);
            
            for (int i = 1; i < duplicationLevel.Value; i++) {
              var additionalShadowPath = Path.Combine(vol.Root.FullName, folderPath.Value, $"{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}.{i}");
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
      DisableDuplicationOnFolder(mountPoint, new FolderPath(folderPath));
    }
    
    public static void DisableDuplicationOnFolder(DriveBender.IMountPoint mountPoint, FolderPath folderPath) {
      if (mountPoint == null)
        throw new ArgumentNullException(nameof(mountPoint));
      
      try {
        var volumes = mountPoint.Volumes.OfType<DriveBender.Volume>().ToArray();
        foreach (var volume in volumes) {
          var top = new DirectoryInfo(Path.Combine(volume.Root.FullName, folderPath.Value));
          if (!top.Exists)
            continue;

          var folders = new[] { top }.Concat(top.EnumerateDirectories("*", SearchOption.AllDirectories)).Where(d => !_IsShadowFolder(d.Name)).ToArray();
          foreach (var folder in folders)
          foreach (var shadowFolder in folder.EnumerateDirectories().Where(d => _IsShadowFolder(d.Name)).ToArray()) {
            var logicalFolder = _RelativePath(volume.Root.FullName, folder.FullName);
            foreach (var shadow in shadowFolder.EnumerateFiles().ToArray()) {
              if (_HasEqualPrimary(volumes, Path.Combine(logicalFolder, shadow.Name), shadow))
                DeleteShadowCopy((volume, shadow));
              else
                DriveBender.Logger?.Invoke($"[Warning]Kept shadow copy '{shadow.FullName}': no primary with the same content exists, so it may be the only good copy");
            }

            // never recursive: whatever is still inside is a copy that was just judged worth keeping
            try {
              shadowFolder.Delete(false);
            } catch (IOException) {
              DriveBender.Logger?.Invoke($"[Warning]Kept '{shadowFolder.FullName}': it still holds shadow copies without a matching primary");
            }
          }
        }
        
        DriveBender.Logger?.Invoke($"Duplication disabled on folder '{folderPath}'");
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to disable duplication on folder '{folderPath}': {ex.Message}");
        throw;
      }
    }
    
    public static void SetDuplicationLevel(DriveBender.IMountPoint mountPoint, string folderPath, int newLevel) {
      SetDuplicationLevel(mountPoint, new FolderPath(folderPath), new DuplicationLevel(newLevel));
    }
    
    public static void SetDuplicationLevel(DriveBender.IMountPoint mountPoint, FolderPath folderPath, DuplicationLevel newLevel) {
      if (newLevel.IsDisabled) {
        DisableDuplicationOnFolder(mountPoint, folderPath);
        return;
      }
      
      var currentLevel = GetDuplicationLevel(mountPoint, folderPath);
      
      if (newLevel.Value > currentLevel.Value) {
        IncreaseduplicationLevel(mountPoint, folderPath, newLevel.Value - currentLevel.Value);
      } else if (newLevel.Value < currentLevel.Value) {
        DecreaseDuplicationLevel(mountPoint, folderPath, currentLevel.Value - newLevel.Value);
      }
    }
    
    public static int GetDuplicationLevel(DriveBender.IMountPoint mountPoint, string folderPath) {
      if (string.IsNullOrWhiteSpace(folderPath))
        return 0;
        
      return GetDuplicationLevel(mountPoint, new FolderPath(folderPath)).Value;
    }
    
    public static DuplicationLevel GetDuplicationLevel(DriveBender.IMountPoint mountPoint, FolderPath folderPath) {
      if (mountPoint == null)
        return DuplicationLevel.Disabled;
      
      var maxLevel = 0;
      foreach (var volume in mountPoint.Volumes) {
        if (volume is DriveBender.Volume vol) {
          var shadowPath = Path.Combine(vol.Root.FullName, folderPath.Value, DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);
          if (Directory.Exists(shadowPath)) {
            maxLevel = Math.Max(maxLevel, 1);
          }
          
          var i = 1;
          string additionalShadowPath;
          do {
            additionalShadowPath = Path.Combine(vol.Root.FullName, folderPath.Value, $"{DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME}.{i}");
            if (Directory.Exists(additionalShadowPath)) {
              maxLevel = Math.Max(maxLevel, i + 1);
              i++;
            } else {
              break;
            }
          } while (true);
        }
      }
      
      return new DuplicationLevel(maxLevel);
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
    
    private static void IncreaseduplicationLevel(DriveBender.IMountPoint mountPoint, FolderPath folderPath, int levelsToAdd) {
      var files = mountPoint.GetItems(folderPath.Value, SearchOption.AllDirectories).OfType<DriveBender.IFile>();
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
    
    private static void DecreaseDuplicationLevel(DriveBender.IMountPoint mountPoint, FolderPath folderPath, int levelsToRemove) {
      var files = mountPoint.GetItems(folderPath.Value, SearchOption.AllDirectories).OfType<DriveBender.IFile>();
      
      var volumes = mountPoint.Volumes.OfType<DriveBender.Volume>().ToArray();
      foreach (var file in files) {
        var removed = 0;
        foreach (var shadow in GetShadowCopies(file).ToArray()) {
          if (removed >= levelsToRemove)
            break;

          // a shadow is only redundant while a primary holds the same bytes; without one it is the file
          if (!_HasEqualPrimary(volumes, file.FullName, shadow.file)) {
            DriveBender.Logger?.Invoke($"[Warning]Kept shadow copy '{shadow.file.FullName}': no primary with the same content exists, so it may be the only good copy");
            continue;
          }

          DeleteShadowCopy(shadow);
          ++removed;
        }
      }
    }

    private static bool _IsShadowFolder(string name)
      => string.Equals(name, DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase)
         || name.StartsWith(DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME + ".", StringComparison.OrdinalIgnoreCase);

    private static string _RelativePath(string root, string path) {
      var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      return path.Length <= trimmedRoot.Length ? string.Empty : path.Substring(trimmedRoot.Length + 1);
    }

    /// <summary>Whether some volume holds a primary of <paramref name="logicalPath"/> with exactly the shadow's content.</summary>
    private static bool _HasEqualPrimary(IEnumerable<DriveBender.Volume> volumes, string logicalPath, FileInfo shadow) {
      foreach (var volume in volumes) {
        var primary = new FileInfo(Path.Combine(volume.Root.FullName, logicalPath));
        if (!primary.Exists || primary.FullName.Equals(shadow.FullName, StringComparison.OrdinalIgnoreCase))
          continue;

        try {
          if (DriveBender.ContentEquals(primary, shadow))
            return true;
        } catch (IOException e) {
          DriveBender.Logger?.Invoke($"[Warning]Could not compare '{primary.FullName}' with '{shadow.FullName}': {e.Message}");
        } catch (UnauthorizedAccessException e) {
          DriveBender.Logger?.Invoke($"[Warning]Could not compare '{primary.FullName}' with '{shadow.FullName}': {e.Message}");
        }
      }

      return false;
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