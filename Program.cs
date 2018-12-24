using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DivisonM;

namespace DriveBensderUtility {
  internal class Program {
    private static void Main(string[] args) {
      var mountPoints = DriveBender.DetectedMountPoints;
      var mountPoint = mountPoints.FirstOrDefault();
      if (mountPoint == null)
        return; /* no pool found */

      DriveBender.Logger = Console.WriteLine;

      Console.WriteLine();

      var items = mountPoint.GetItems("Movies", SearchOption.AllDirectories).OrderBy(d=>d.FullName).Select(d=>$"{d.FullName}({(d is DriveBender.IFile file?DriveBender.FormatSize(file.Size):string.Empty)},{d.Parent.Drive.Name})").ToArray();
      

      DriveBender.Logger($"Pool:{mountPoint.Name}({mountPoint.Description}) [{string.Join(", ", mountPoint.Volumes.Select(d=>d.Name))}]");

      DriveBender.Logger("Restoring primaries from doubles where needed");
      mountPoint.RestoreMissingPrimaries();

      DriveBender.Logger("Restoring doubles from primaries where needed");
      mountPoint.CreateMissingShadowCopies();

      //_DeleteFilesAlsoOnPool(new DirectoryInfo(@"A:\{94C96B74-F849-4D1F-BCEE-0C18A66EFFFC}"), pool);

      DriveBender.Logger("NFO files without movie file");
      _FindDeletedMovieFiles(mountPoint);

      //pool.Rebalance();

      Console.WriteLine("READY.");
      Console.ReadKey(false);
    }

    private static void _DeleteFilesAlsoOnPool(DirectoryInfo root, DriveBender.IMountPoint mountPoint) {
      foreach (var t in _FilesAlsoOnPool(root, mountPoint)) {
        Console.WriteLine($"File also on pool: {t.Item2[0].FullName} - deleting");
        t.Item1.TryDelete();
      }
    }

    private static IEnumerable<Tuple<FileInfo, DriveBender.IFile[]>> _FilesAlsoOnPool(DirectoryInfo root, DriveBender.IMountPoint mountPoint) {
      var poolFiles = new Dictionary<string, List<DriveBender.IFile>>(StringComparer.OrdinalIgnoreCase);
      foreach (var volume in mountPoint.Volumes)
        foreach (var file in volume.Items.EnumerateFiles(true))
          poolFiles.GetOrAdd(file.FullName, _ => new List<DriveBender.IFile>()).Add(file);

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
      var hash = new HashSet<string>();
      foreach (var volume in mountPoint.Volumes)
        foreach (var file in volume.Items.EnumerateFiles(true))
          hash.TryAdd(file.FullName);

      // find all nfo without video
      var count = 0;
      foreach (var nfo in hash.Where(i => Path.GetExtension(i) == ".nfo" && Path.GetFileNameWithoutExtension(i) != "tvshow").OrderBy(i => i))
        if (!(
          hash.Contains(Path.ChangeExtension(nfo, ".mkv"))
          || hash.Contains(Path.ChangeExtension(nfo, ".avi"))
          || hash.Contains(Path.ChangeExtension(nfo, ".flv"))
          || hash.Contains(Path.ChangeExtension(nfo, ".mpg"))
          || hash.Contains(Path.ChangeExtension(nfo, ".mp4"))
          || hash.Contains(Path.ChangeExtension(nfo, ".mp2"))
          ))
          Console.WriteLine($"{++count}:{nfo}");

    }

  }
}

