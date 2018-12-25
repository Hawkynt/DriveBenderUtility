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
      private static IEnumerable<IPhysicalFile> _ShadowCopiesWithoutPrimary(IMountPoint mountPoint) {
        var primaries = new Dictionary<string, IPhysicalFile>(StringComparer.OrdinalIgnoreCase);
        var shadows = new Dictionary<string, IPhysicalFile>(StringComparer.OrdinalIgnoreCase);
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
      private static IEnumerable<IPhysicalFile> _PrimariesWithoutShadowCopy(IMountPoint mountPoint) {
        var primaries = new Dictionary<string, IPhysicalFile>(StringComparer.OrdinalIgnoreCase);
        var shadows = new Dictionary<string, IPhysicalFile>(StringComparer.OrdinalIgnoreCase);
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
      
    }
  }
}