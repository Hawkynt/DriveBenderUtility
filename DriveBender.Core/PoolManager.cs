using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DivisonM {
  public static class PoolManager {
    
    public static bool CreatePool(string poolName, string mountPointPath, IEnumerable<string> drivePaths) {
      try {
        if (drivePaths == null)
          throw new ArgumentNullException(nameof(drivePaths));
        
        var drivePathList = drivePaths.ToList();
        if (drivePathList.Count == 0)
          throw new ArgumentException("At least one drive path is required", nameof(drivePaths));
        
        var validatedDrives = drivePathList.Select(p => new DrivePath(p));
        return CreatePool(new PoolName(poolName), mountPointPath, validatedDrives);
      } catch (DirectoryNotFoundException) {
        DriveBender.Logger?.Invoke($"Failed to create pool '{poolName}': One or more drive paths do not exist");
        return false;
      } catch (ArgumentNullException) {
        throw;
      } catch (ArgumentException) {
        throw;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to create pool '{poolName}': {ex.Message}");
        return false;
      }
    }
    
    public static bool CreatePool(PoolName poolName, string mountPointPath, IEnumerable<DrivePath> drivePaths) {
      if (string.IsNullOrWhiteSpace(mountPointPath))
        throw new ArgumentException("Mount point path cannot be empty", nameof(mountPointPath));
      
      var drives = drivePaths?.ToArray() ?? throw new ArgumentNullException(nameof(drivePaths));
      if (drives.Length == 0)
        throw new ArgumentException("At least one drive path is required", nameof(drivePaths));
      
      try {
        var poolId = Guid.NewGuid();
        
        foreach (var drivePath in drives) {
          if (!drivePath.Exists)
            throw new DirectoryNotFoundException($"Drive path not found: {drivePath}");
          
          _CreatePoolStructureOnDrive(drivePath.Value, poolName.Value, poolId);
        }
        
        DriveBender.Logger?.Invoke($"Pool '{poolName}' created successfully with ID {poolId}");
        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to create pool '{poolName}': {ex.Message}");
        return false;
      }
    }
    
    public static bool DeletePool(string poolName, bool removeData = false) {
      return DeletePool(new PoolName(poolName), removeData);
    }
    
    public static bool DeletePool(PoolName poolName, bool removeData = false) {
      var mountPoints = DriveBender.DetectedMountPoints;
      var pool = mountPoints.FirstOrDefault(mp => string.Equals(mp.Name, poolName.Value, StringComparison.OrdinalIgnoreCase));
      
      if (pool == null) {
        DriveBender.Logger?.Invoke($"Pool '{poolName}' not found");
        return false;
      }
      
      try {
        foreach (var volume in pool.Volumes) {
          _RemovePoolStructureFromDrive(volume, removeData);
        }
        
        DriveBender.Logger?.Invoke($"Pool '{poolName}' deleted successfully");
        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to delete pool '{poolName}': {ex.Message}");
        return false;
      }
    }
    
    public static bool AddDriveToPool(string poolName, string drivePath) {
      try {
        return AddDriveToPool(new PoolName(poolName), new DrivePath(drivePath));
      } catch (DirectoryNotFoundException) {
        DriveBender.Logger?.Invoke($"Failed to add drive to pool '{poolName}': Drive path does not exist: {drivePath}");
        return false;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to add drive to pool '{poolName}': {ex.Message}");
        return false;
      }
    }
    
    public static bool AddDriveToPool(PoolName poolName, DrivePath drivePath) {
      var mountPoints = DriveBender.DetectedMountPoints;
      var pool = mountPoints.FirstOrDefault(mp => string.Equals(mp.Name, poolName.Value, StringComparison.OrdinalIgnoreCase));
      
      if (pool == null) {
        DriveBender.Logger?.Invoke($"Pool '{poolName}' not found");
        return false;
      }
      
      if (!drivePath.Exists) {
        DriveBender.Logger?.Invoke($"Drive path not found: {drivePath}");
        return false;
      }
      
      try {
        _CreatePoolStructureOnDrive(drivePath.Value, pool.Name, pool.Id);
        DriveBender.Logger?.Invoke($"Drive '{drivePath}' added to pool '{poolName}' successfully");
        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to add drive to pool: {ex.Message}");
        return false;
      }
    }
    
    public static bool RemoveDriveFromPool(string poolName, string drivePath, bool moveData = true) {
      try {
        return RemoveDriveFromPool(new PoolName(poolName), new DrivePath(drivePath), new DriveOperationOptions { 
          DryRun = false, 
        CreateBackup = true, 
        PromptUser = false,
        AutoBalance = moveData 
      }).Success;
      } catch (DirectoryNotFoundException) {
        DriveBender.Logger?.Invoke($"Failed to remove drive from pool '{poolName}': Drive path does not exist: {drivePath}");
        return false;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to remove drive from pool '{poolName}': {ex.Message}");
        return false;
      }
    }
    
    public static DriveOperationResult RemoveDriveFromPool(PoolName poolName, DrivePath drivePath, DriveOperationOptions options) {
      var startTime = DateTime.Now;
      var result = new DriveOperationResult();
      
      try {
        var mountPoints = DriveBender.DetectedMountPoints;
        var pool = mountPoints.FirstOrDefault(mp => string.Equals(mp.Name, poolName.Value, StringComparison.OrdinalIgnoreCase));
        
        if (pool == null) {
          result.Success = false;
          result.Message = $"Pool '{poolName}' not found";
          return result;
        }
        
        var volume = pool.Volumes.FirstOrDefault(v => v.Name.Equals(drivePath.Value, StringComparison.OrdinalIgnoreCase));
        if (volume == null) {
          result.Success = false;
          result.Message = $"Drive '{drivePath}' not found in pool '{poolName}'";
          return result;
        }
        
        // Check space before removal
        var spaceCheck = CheckSpaceForDriveRemoval(poolName, drivePath);
        if (!spaceCheck.HasSufficientSpace && options.AutoBalance) {
          result.Success = false;
          result.Message = $"Insufficient space for data migration. {spaceCheck.RecommendedAction}";
          result.Warnings = new[] { $"Shortfall: {spaceCheck.ShortfallSpace.ToHumanReadable()}" };
          return result;
        }
        
        if (options.DryRun) {
          result.Success = true;
          result.Message = $"Dry run: Would remove drive '{drivePath}' from pool '{poolName}'";
          result.DataProcessed = _CalculateVolumeDataSize(volume);
          return result;
        }
        
        var warnings = new List<string>();
        var filesProcessed = 0;
        var dataProcessed = ByteSize.FromBytes(0);
        
        if (options.AutoBalance) {
          var moveResult = _MoveDataFromVolumeWithProgress(pool, volume, options);
          filesProcessed += moveResult.FilesProcessed;
          dataProcessed += moveResult.DataProcessed;
          warnings.AddRange(moveResult.Warnings);
        }
        
        _RemovePoolStructureFromDrive(volume, !options.AutoBalance);
        
        result.Success = true;
        result.Message = $"Drive '{drivePath}' removed from pool '{poolName}' successfully";
        result.FilesProcessed = filesProcessed;
        result.DataProcessed = dataProcessed;
        result.Warnings = warnings;
        
      } catch (Exception ex) {
        result.Success = false;
        result.Message = $"Failed to remove drive from pool: {ex.Message}";
        result.Exception = ex;
      } finally {
        result.Duration = DateTime.Now - startTime;
      }
      
      return result;
    }
    
    public static SpaceCheckResult CheckSpaceForDriveRemoval(PoolName poolName, DrivePath drivePath) {
      var result = new SpaceCheckResult();
      
      try {
        var mountPoints = DriveBender.DetectedMountPoints;
        var pool = mountPoints.FirstOrDefault(mp => string.Equals(mp.Name, poolName.Value, StringComparison.OrdinalIgnoreCase));
        
        if (pool == null) {
          result.HasSufficientSpace = false;
          result.RecommendedAction = "Pool not found";
          return result;
        }
        
        var volumeToRemove = pool.Volumes.FirstOrDefault(v => v.Name.Equals(drivePath.Value, StringComparison.OrdinalIgnoreCase));
        if (volumeToRemove == null) {
          result.HasSufficientSpace = false;
          result.RecommendedAction = "Drive not found in pool";
          return result;
        }
        
        var remainingVolumes = pool.Volumes.Where(v => v != volumeToRemove).ToArray();
        if (remainingVolumes.Length == 0) {
          result.HasSufficientSpace = false;
          result.RecommendedAction = "Cannot remove the last drive from pool";
          return result;
        }
        
        var dataToMove = _CalculateVolumeDataSize(volumeToRemove);
        var availableSpace = remainingVolumes.Aggregate(ByteSize.FromBytes(0), (acc, v) => acc + v.BytesFree);
        
        result.RequiredSpace = dataToMove;
        result.AvailableSpace = availableSpace;
        result.HasSufficientSpace = availableSpace >= dataToMove;
        result.VolumeSpaceInfo = remainingVolumes.Select(v => (v, new ByteSize(v.BytesFree)));
        
        if (!result.HasSufficientSpace) {
          result.RecommendedAction = "Add more drives to pool or free up space on existing drives";
        } else {
          result.RecommendedAction = "Sufficient space available for data migration";
        }
        
      } catch (Exception ex) {
        result.HasSufficientSpace = false;
        result.RecommendedAction = $"Error checking space: {ex.Message}";
      }
      
      return result;
    }
    
    public static DriveOperationResult ReplaceDrive(PoolName poolName, DrivePath oldDrivePath, DrivePath newDrivePath, DriveOperationOptions options = null) {
      options = options ?? new DriveOperationOptions();
      var startTime = DateTime.Now;
      var result = new DriveOperationResult();
      
      try {
        // Step 1: Check space availability for data migration
        var spaceCheck = CheckSpaceForDriveRemoval(poolName, oldDrivePath);
        if (!spaceCheck.HasSufficientSpace) {
          result.Success = false;
          result.Message = "Cannot replace drive: insufficient space for temporary data migration";
          result.Warnings = new[] { $"Shortfall: {spaceCheck.ShortfallSpace.ToHumanReadable()}" };
          return result;
        }
        
        if (options.DryRun) {
          result.Success = true;
          result.Message = $"Dry run: Would replace drive '{oldDrivePath}' with '{newDrivePath}' in pool '{poolName}'";
          result.DataProcessed = spaceCheck.RequiredSpace;
          return result;
        }
        
        var warnings = new List<string>();
        var totalFilesProcessed = 0;
        var totalDataProcessed = ByteSize.FromBytes(0);
        
        // Step 2: Temporarily move data from old drive
        DriveBender.Logger?.Invoke($"Step 1/3: Moving data from old drive '{oldDrivePath}'...");
        var removeOptions = new DriveOperationOptions {
          DryRun = false,
          CreateBackup = options.CreateBackup,
          PromptUser = false,
          AutoBalance = true
        };
        
        var removeResult = RemoveDriveFromPool(poolName, oldDrivePath, removeOptions);
        if (!removeResult.Success) {
          result.Success = false;
          result.Message = $"Failed to remove old drive: {removeResult.Message}";
          result.Exception = removeResult.Exception;
          return result;
        }
        
        totalFilesProcessed += removeResult.FilesProcessed;
        totalDataProcessed += removeResult.DataProcessed;
        warnings.AddRange(removeResult.Warnings);
        
        // Step 3: Add new drive to pool
        DriveBender.Logger?.Invoke($"Step 2/3: Adding new drive '{newDrivePath}' to pool...");
        if (!AddDriveToPool(poolName.Value, newDrivePath.Value)) {
          result.Success = false;
          result.Message = "Failed to add new drive to pool";
          result.Warnings = warnings.Concat(new[] { "Old drive has been removed but new drive addition failed" }).ToArray();
          return result;
        }
        
        // Step 4: Rebalance data if requested
        if (options.AutoBalance) {
          DriveBender.Logger?.Invoke($"Step 3/3: Rebalancing pool data...");
          var balanceResult = RebalancePool(poolName, options);
          totalFilesProcessed += balanceResult.FilesProcessed;
          totalDataProcessed += balanceResult.DataProcessed;
          warnings.AddRange(balanceResult.Warnings);
        }
        
        result.Success = true;
        result.Message = $"Successfully replaced drive '{oldDrivePath}' with '{newDrivePath}' in pool '{poolName}'";
        result.FilesProcessed = totalFilesProcessed;
        result.DataProcessed = totalDataProcessed;
        result.Warnings = warnings;
        
      } catch (Exception ex) {
        result.Success = false;
        result.Message = $"Failed to replace drive: {ex.Message}";
        result.Exception = ex;
      } finally {
        result.Duration = DateTime.Now - startTime;
      }
      
      return result;
    }
    
    public static DriveOperationResult RebalancePool(PoolName poolName, DriveOperationOptions options = null) {
      options = options ?? new DriveOperationOptions();
      var startTime = DateTime.Now;
      var result = new DriveOperationResult();
      
      try {
        var mountPoints = DriveBender.DetectedMountPoints;
        var pool = mountPoints.FirstOrDefault(mp => string.Equals(mp.Name, poolName.Value, StringComparison.OrdinalIgnoreCase));
        
        if (pool == null) {
          result.Success = false;
          result.Message = $"Pool '{poolName}' not found";
          return result;
        }
        
        if (options.DryRun) {
          result.Success = true;
          result.Message = $"Dry run: Would rebalance pool '{poolName}'";
          return result;
        }
        
        var filesProcessed = 0;
        var dataProcessed = ByteSize.FromBytes(0);
        var warnings = new List<string>();
        
        // Simple rebalancing: move files from most full drives to least full drives
        var volumesByFreeSpace = pool.Volumes.OrderBy(v => v.BytesFree).ToArray();
        var sourceVolumes = volumesByFreeSpace.Take(volumesByFreeSpace.Length / 2);
        var targetVolumes = volumesByFreeSpace.Skip(volumesByFreeSpace.Length / 2);
        
        foreach (var sourceVolume in sourceVolumes) {
          var files = sourceVolume.Items.OfType<DriveBender.IPhysicalFile>().Take(10); // Limit for performance
          
          foreach (var file in files) {
            var targetVolume = targetVolumes.OrderByDescending(v => v.BytesFree).FirstOrDefault();
            if (targetVolume != null && targetVolume.BytesFree > file.Size) {
              try {
                file.MoveToDrive(targetVolume);
                filesProcessed++;
                dataProcessed += file.Size;
              } catch (Exception ex) {
                warnings.Add($"Failed to move file {file.FullName}: {ex.Message}");
              }
            }
          }
        }
        
        result.Success = true;
        result.Message = $"Pool '{poolName}' rebalanced successfully";
        result.FilesProcessed = filesProcessed;
        result.DataProcessed = dataProcessed;
        result.Warnings = warnings;
        
      } catch (Exception ex) {
        result.Success = false;
        result.Message = $"Failed to rebalance pool: {ex.Message}";
        result.Exception = ex;
      } finally {
        result.Duration = DateTime.Now - startTime;
      }
      
      return result;
    }
    
    private static void _CreatePoolStructureOnDrive(string drivePath, string poolName, Guid poolId) {
      var poolRootPath = Path.Combine(drivePath, $"{{{poolId}}}");
      Directory.CreateDirectory(poolRootPath);
      
      var infoFileName = Path.Combine(drivePath, $"Pool.{DriveBender.DriveBenderConstants.INFO_EXTENSION}");
      var infoContent = new[] {
        $"volumelabel:{poolName}",
        $"id:{poolId}",
        $"description:Drive Bender Pool - {poolName}",
        $"created:{DateTime.Now:yyyy-MM-dd HH:mm:ss}"
      };
      
      File.WriteAllLines(infoFileName, infoContent);
    }
    
    private static void _RemovePoolStructureFromDrive(DriveBender.IVolume volume, bool removeData) {
      if (volume is DriveBender.Volume vol) {
        var drivePath = vol.Root.Parent?.FullName;
        if (drivePath == null) return;
        
        var infoFiles = Directory.GetFiles(drivePath, $"*.{DriveBender.DriveBenderConstants.INFO_EXTENSION}");
        foreach (var file in infoFiles) {
          File.Delete(file);
        }
        
        if (removeData && Directory.Exists(vol.Root.FullName)) {
          Directory.Delete(vol.Root.FullName, true);
        }
      }
    }
    
    private static void _MoveDataFromVolume(DriveBender.IMountPoint pool, DriveBender.IVolume sourceVolume) {
      var moveResult = _MoveDataFromVolumeWithProgress(pool, sourceVolume, new DriveOperationOptions());
      if (!moveResult.Success) {
        throw new InvalidOperationException(moveResult.Message);
      }
    }
    
    private static DriveOperationResult _MoveDataFromVolumeWithProgress(DriveBender.IMountPoint pool, DriveBender.IVolume sourceVolume, DriveOperationOptions options) {
      var result = new DriveOperationResult();
      var startTime = DateTime.Now;
      
      try {
        var otherVolumes = pool.Volumes.Where(v => v != sourceVolume).ToArray();
        if (otherVolumes.Length == 0) {
          result.Success = false;
          result.Message = "Cannot move data - no other volumes available in pool";
          return result;
        }
        
        var filesProcessed = 0;
        var dataProcessed = ByteSize.FromBytes(0);
        var warnings = new List<string>();
        
        foreach (var item in sourceVolume.Items) {
          if (item is DriveBender.IPhysicalFile file) {
            try {
              var targetVolume = otherVolumes.OrderByDescending(v => v.BytesFree).FirstOrDefault();
              if (targetVolume != null && targetVolume.BytesFree > file.Size) {
                if (!options.DryRun) {
                  file.MoveToDrive(targetVolume);
                }
                filesProcessed++;
                dataProcessed += file.Size;
              } else {
                warnings.Add($"Insufficient space to move file: {file.FullName}");
              }
            } catch (Exception ex) {
              warnings.Add($"Failed to move file {file.FullName}: {ex.Message}");
            }
          }
        }
        
        result.Success = true;
        result.Message = $"Moved {filesProcessed} files ({dataProcessed.ToHumanReadable()}) from volume";
        result.FilesProcessed = filesProcessed;
        result.DataProcessed = dataProcessed;
        result.Warnings = warnings;
        
      } catch (Exception ex) {
        result.Success = false;
        result.Message = $"Failed to move data from volume: {ex.Message}";
        result.Exception = ex;
      } finally {
        result.Duration = DateTime.Now - startTime;
      }
      
      return result;
    }
    
    private static ByteSize _CalculateVolumeDataSize(DriveBender.IVolume volume) {
      try {
        var totalSize = ByteSize.FromBytes(0);
        foreach (var item in volume.Items) {
          if (item is DriveBender.IPhysicalFile file) {
            totalSize += file.Size;
          }
        }
        return totalSize;
      } catch {
        // If we can't calculate the exact size, estimate based on used space
        var driveInfo = new DriveInfo(volume.Name);
        return ByteSize.FromBytes((ulong)(driveInfo.TotalSize - driveInfo.AvailableFreeSpace));
      }
    }
  }
}