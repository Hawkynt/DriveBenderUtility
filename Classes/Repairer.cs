using System;
using System.Collections.Generic;
using System.Linq;
using Libraries;

namespace DriveBender {
  partial class Pool {

    /// <summary>
    /// Returns all shadow copies where to primary file is missing.
    /// </summary>
    /// <param name="pool">The pool.</param>
    /// <returns></returns>
    private static IEnumerable<IFile> _ShadowCopiesWithoutPrimary(IPool pool) {
      var primaries = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
      var shadows = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
      foreach (var drive in pool.Drives)
        foreach (var file in drive.Items.EnumerateFiles(true))
          if (file.IsShadowCopy)
            shadows.TryAdd(file.FullName, file);
          else
            primaries.TryAdd(file.FullName, file);

      foreach (var kvp in shadows)
        if (!primaries.ContainsKey(kvp.Key))
          yield return kvp.Value;
    }

    /// <summary>
    /// Returns all primary files where the shadow copy is missing.
    /// </summary>
    /// <param name="pool">The pool.</param>
    /// <returns></returns>
    private static IEnumerable<IFile> _PrimariesWithoutShadowCopy(IPool pool) {
      var primaries = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
      var shadows = new Dictionary<string, IFile>(StringComparer.OrdinalIgnoreCase);
      foreach (var drive in pool.Drives)
        foreach (var file in drive.Items.EnumerateFiles(true))
          if (file.IsShadowCopy)
            shadows.TryAdd(file.FullName, file);
          else
            primaries.TryAdd(file.FullName, file);

      foreach (var kvp in primaries)
        if (!shadows.ContainsKey(kvp.Key))
          yield return kvp.Value;
    }

    public void RestoreMissingPrimaries(Action<string> logger) {
      var pool = this;
      foreach (var file in _ShadowCopiesWithoutPrimary(pool)) {
        var target = pool.Drives.OrderByDescending(d => d.BytesFree).FirstOrDefault(d => !file.ExistsOnDrive(d));
        logger($@" - Restoring primary file {file.Name} from {file.Source.Directory.Root.Name}, {FilesizeFormatter.FormatIEC(file.Size, "0.#")}");
        file.CopyToDrive(target, true);
      }
    }

    public void CreateMissingShadowCopies(Action<string> logger) {
      var pool = this;
      foreach (var file in _PrimariesWithoutShadowCopy(pool)) {
        var target = pool.Drives.OrderByDescending(d => d.BytesFree).FirstOrDefault(d => !file.ExistsOnDrive(d));
        logger($@" - Restoring shadow file {file.Name} from {file.Source.Directory.Root.Name}, {FilesizeFormatter.FormatIEC(file.Size, "0.#")}");
        file.CopyToDrive(target, false);
      }
    }
  }
}