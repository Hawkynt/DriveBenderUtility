using System;
using System.Collections.Generic;
using System.Linq;

namespace DivisonM {
  partial class DriveBender {
    partial class MountPoint {

      /// <summary>
      /// Returns all shadow copies where to primary file is missing.
      /// </summary>
      /// <param name="mountPoint">The pool.</param>
      /// <returns></returns>
      private static IEnumerable<IFile> _ShadowCopiesWithoutPrimary(IMountPoint mountPoint) {
        var primaries = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
        var shadows = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in mountPoint.Volumes.SelectMany(drive => drive.Items.EnumerateFiles(true)))
          if (file.IsShadowCopy)
            shadows.TryAdd(file.FullName, file);
          else
            primaries.TryAdd(file.FullName, file);

        const string tempExtension = "." + DriveBenderConstants.TEMP_EXTENSION;
        return
          from kvp in shadows
          where !primaries.ContainsKey(kvp.Key)
          where !kvp.Value.Name.EndsWith(tempExtension)
          select kvp.Value
          ;
      }

      /// <summary>
      /// Returns all primary files where the shadow copy is missing.
      /// </summary>
      /// <param name="mountPoint">The pool.</param>
      /// <returns></returns>
      private static IEnumerable<IFile> _PrimariesWithoutShadowCopy(IMountPoint mountPoint) {
        var primaries = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
        var shadows = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in mountPoint.Volumes.SelectMany(drive => drive.Items.EnumerateFiles(true)))
          if (file.IsShadowCopy)
            shadows.TryAdd(file.FullName, file);
          else
            primaries.TryAdd(file.FullName, file);

        const string tempExtension = "." + DriveBenderConstants.TEMP_EXTENSION;
        return
          from kvp in primaries
          where !shadows.ContainsKey(kvp.Key)
          where !kvp.Value.Name.EndsWith(tempExtension)
          select kvp.Value
          ;
      }

      public void RestoreMissingPrimaries() {
        var pool = this;
        foreach (var file in _ShadowCopiesWithoutPrimary(pool)) {
          var target = pool.Volumes.OrderByDescending(d => d.BytesFree).FirstOrDefault(d => !file.ExistsOnDrive(d));
          if (target == null)
            continue;

          Logger($@" - Restoring primary file {file.FullName} from {file.Source.Directory?.Root.Name}, {FormatSize(file.Size)} to {target.Name}");
          file.CopyToDrive(target, true);
        }
      }

      public void CreateMissingShadowCopies() {
        var pool = this;
        foreach (var file in _PrimariesWithoutShadowCopy(pool)) {
          var target = pool.Volumes.OrderByDescending(d => d.BytesFree).FirstOrDefault(d => !file.ExistsOnDrive(d));
          if(target==null)
            continue;

          Logger($@" - Restoring shadow file {file.FullName} from {file.Source.Directory?.Root.Name}, {FormatSize(file.Size)} to {target.Name}");
          file.CopyToDrive(target, false);
        }
      }
    }
  }
}