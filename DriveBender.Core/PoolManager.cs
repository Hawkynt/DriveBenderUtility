using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DivisonM {
  public static class PoolManager {
    
    public static bool CreatePool(string poolName, string mountPointPath, IEnumerable<string> drivePaths) {
      if (string.IsNullOrWhiteSpace(poolName))
        throw new ArgumentException("Pool name cannot be empty", nameof(poolName));
      
      if (string.IsNullOrWhiteSpace(mountPointPath))
        throw new ArgumentException("Mount point path cannot be empty", nameof(mountPointPath));
      
      var drives = drivePaths?.ToArray() ?? throw new ArgumentNullException(nameof(drivePaths));
      if (drives.Length == 0)
        throw new ArgumentException("At least one drive path is required", nameof(drivePaths));
      
      try {
        var poolId = Guid.NewGuid();
        
        foreach (var drivePath in drives) {
          if (!Directory.Exists(drivePath))
            throw new DirectoryNotFoundException($"Drive path not found: {drivePath}");
          
          _CreatePoolStructureOnDrive(drivePath, poolName, poolId);
        }
        
        DriveBender.Logger?.Invoke($"Pool '{poolName}' created successfully with ID {poolId}");
        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to create pool '{poolName}': {ex.Message}");
        return false;
      }
    }
    
    public static bool DeletePool(string poolName, bool removeData = false) {
      var mountPoints = DriveBender.DetectedMountPoints;
      var pool = mountPoints.FirstOrDefault(mp => string.Equals(mp.Name, poolName, StringComparison.OrdinalIgnoreCase));
      
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
      var mountPoints = DriveBender.DetectedMountPoints;
      var pool = mountPoints.FirstOrDefault(mp => string.Equals(mp.Name, poolName, StringComparison.OrdinalIgnoreCase));
      
      if (pool == null) {
        DriveBender.Logger?.Invoke($"Pool '{poolName}' not found");
        return false;
      }
      
      if (!Directory.Exists(drivePath)) {
        DriveBender.Logger?.Invoke($"Drive path not found: {drivePath}");
        return false;
      }
      
      try {
        _CreatePoolStructureOnDrive(drivePath, pool.Name, pool.Id);
        DriveBender.Logger?.Invoke($"Drive '{drivePath}' added to pool '{poolName}' successfully");
        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to add drive to pool: {ex.Message}");
        return false;
      }
    }
    
    public static bool RemoveDriveFromPool(string poolName, string drivePath, bool moveData = true) {
      var mountPoints = DriveBender.DetectedMountPoints;
      var pool = mountPoints.FirstOrDefault(mp => string.Equals(mp.Name, poolName, StringComparison.OrdinalIgnoreCase));
      
      if (pool == null) {
        DriveBender.Logger?.Invoke($"Pool '{poolName}' not found");
        return false;
      }
      
      var volume = pool.Volumes.FirstOrDefault(v => v.Name.Equals(drivePath, StringComparison.OrdinalIgnoreCase));
      if (volume == null) {
        DriveBender.Logger?.Invoke($"Drive '{drivePath}' not found in pool '{poolName}'");
        return false;
      }
      
      try {
        if (moveData) {
          _MoveDataFromVolume(pool, volume);
        }
        
        _RemovePoolStructureFromDrive(volume, !moveData);
        DriveBender.Logger?.Invoke($"Drive '{drivePath}' removed from pool '{poolName}' successfully");
        return true;
      } catch (Exception ex) {
        DriveBender.Logger?.Invoke($"Failed to remove drive from pool: {ex.Message}");
        return false;
      }
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
      var otherVolumes = pool.Volumes.Where(v => v != sourceVolume).ToArray();
      if (otherVolumes.Length == 0) {
        throw new InvalidOperationException("Cannot move data - no other volumes available in pool");
      }
      
      foreach (var item in sourceVolume.Items) {
        if (item is DriveBender.IPhysicalFile file) {
          var targetVolume = otherVolumes.OrderByDescending(v => v.BytesFree).First();
          file.MoveToDrive(targetVolume);
        }
      }
    }
  }
}