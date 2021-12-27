using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DivisonM;

namespace DriveBenderUtility {
  internal class Program {
    private static void Main(string[] args) {
      var mountPoints = DriveBender.DetectedMountPoints;
      var mountPoint = mountPoints.FirstOrDefault();
      if (mountPoint == null)
        return; /* no pool found */

      DriveBender.Logger = Console.WriteLine;

      Console.WriteLine();

      DriveBender.Logger($"Pool:{mountPoint.Name}({mountPoint.Description}) [{string.Join(", ", mountPoint.Volumes.Select(d => d.Name))}]");
      
      mountPoint.FixMissingDuplicationOnAllFolders();
      mountPoint.FixDuplicatePrimaries();
      mountPoint.FixDuplicateShadowCopies();
      mountPoint.FixMissingPrimaries();
      mountPoint.FixMissingShadowCopies();

      //mountPoint.Rebalance();
      
      DriveBender.Logger("NFO files without movie file");
      _FindDeletedMovieFiles(mountPoint);
      
      Console.WriteLine("READY.");
      Console.ReadKey(false);
    }

    private static void _DeleteFilesAlsoOnPool(DirectoryInfo root, DriveBender.IMountPoint mountPoint) {
      foreach (var t in _FilesAlsoOnPool(root, mountPoint)) {
        Console.WriteLine($"File also on pool: {t.Item2[0].FullName} - deleting");
        t.Item1.TryDelete();
      }
    }

    private static IEnumerable<Tuple<FileInfo, DriveBender.IPhysicalFile[]>> _FilesAlsoOnPool(DirectoryInfo root, DriveBender.IMountPoint mountPoint) {
      var poolFiles = new Dictionary<string, List<DriveBender.IPhysicalFile>>(StringComparer.OrdinalIgnoreCase);
      foreach (var volume in mountPoint.Volumes)
        foreach (var file in volume.Items.EnumerateFiles(true))
          poolFiles.GetOrAdd(file.FullName, _ => new List<DriveBender.IPhysicalFile>()).Add(file);

      var length = root.FullName.Length;
      foreach (var file in root.EnumerateFiles("*.*", SearchOption.AllDirectories)) {
        var fileName = file.FullName;
        if (string.Equals(file.Directory.Name, DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
          fileName = Path.Combine(file.Directory.Parent.FullName, file.Name);

        var relativeNameToRoot = fileName.Substring(length + 1);
        if (!poolFiles.TryGetValue(relativeNameToRoot, out var value))
          continue;

        yield return Tuple.Create(file, value.ToArray());
      }
    }

    private static void _FindDeletedMovieFiles(DriveBender.IMountPoint mountPoint) {

      // get all files
      var hash = new Dictionary<string,DriveBender.IFile>();
      foreach(var file in mountPoint.GetItems(SearchOption.AllDirectories).OfType<DriveBender.IFile>())
        hash.TryAdd(file.FullName,file);

      // find all nfo without video
      var count = 0;
      foreach (var nfo in hash.Keys.Where(i => Path.GetExtension(i) == ".nfo" && Path.GetFileNameWithoutExtension(i) != "tvshow").OrderBy(i => hash[i].Parent?.FullName).ThenBy(i=>hash[i].Name))
        if (!(
          hash.ContainsKey(Path.ChangeExtension(nfo, ".mkv"))
          || hash.ContainsKey(Path.ChangeExtension(nfo, ".avi"))
          || hash.ContainsKey(Path.ChangeExtension(nfo, ".flv"))
          || hash.ContainsKey(Path.ChangeExtension(nfo, ".mpg"))
          || hash.ContainsKey(Path.ChangeExtension(nfo, ".mp4"))
          || hash.ContainsKey(Path.ChangeExtension(nfo, ".mp2"))
          ))
          Console.WriteLine($"{++count}:{nfo}({(hash[nfo].Primary?? hash[nfo].ShadowCopy).Name})");

    }

  }
}

