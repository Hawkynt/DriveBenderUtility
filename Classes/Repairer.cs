using System;
using System.Collections.Generic;
using System.Linq;
using Libraries;

namespace DivisonM {
  partial class DriveBender {
    partial class Pool {

      /// <summary>
      /// Returns all shadow copies where to primary file is missing.
      /// </summary>
      /// <param name="pool">The pool.</param>
      /// <returns></returns>
      private static IEnumerable<IFile> _ShadowCopiesWithoutPrimary(IPool pool) {
        var primaries = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
        var shadows = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in pool.Drives.SelectMany(drive => drive.Items.EnumerateFiles(true)))
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
      /// <param name="pool">The pool.</param>
      /// <returns></returns>
      private static IEnumerable<IFile> _PrimariesWithoutShadowCopy(IPool pool) {
        var primaries = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
        var shadows = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in pool.Drives.SelectMany(drive => drive.Items.EnumerateFiles(true)))
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

      public void RestoreMissingPrimaries(Action<string> logger) {
        var pool = this;
        foreach (var file in _ShadowCopiesWithoutPrimary(pool)) {
          var target = pool.Drives.OrderByDescending(d => d.BytesFree).FirstOrDefault(d => !file.ExistsOnDrive(d));
          logger($@" - Restoring primary file {file.FullName} from {file.Source.Directory?.Root.Name}, {FilesizeFormatter.FormatIEC(file.Size, "0.#")}");
          file.CopyToDrive(target, true);
        }
      }

      public void CreateMissingShadowCopies(Action<string> logger) {
        var pool = this;
        foreach (var file in _PrimariesWithoutShadowCopy(pool)) {
          var target = pool.Drives.OrderByDescending(d => d.BytesFree).FirstOrDefault(d => !file.ExistsOnDrive(d));
          logger($@" - Restoring shadow file {file.FullName} from {file.Source.Directory?.Root.Name}, {FilesizeFormatter.FormatIEC(file.Size, "0.#")}");
          file.CopyToDrive(target, false);
        }
      }
    }
  }
}