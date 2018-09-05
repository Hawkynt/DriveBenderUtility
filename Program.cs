using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriveBender;

namespace DriveBenderUtility {
  internal class Program {
    private static void Main(string[] args) {
      var pools = Pool.DetectedPools;
      var pool = pools.FirstOrDefault();
      if (pool == null)
        return; /* no pool found */

      Action<string> logger = Console.WriteLine;
      Console.WriteLine();

      logger("Restoring primaries from doubles where needed");
      pool.RestoreMissingPrimaries(logger);

      logger("Restoring doubles from primaries where needed");
      pool.CreateMissingShadowCopies(logger);

      //_DeleteFilesAlsoOnPool(new DirectoryInfo(@"A:\{94C96B74-F849-4D1F-BCEE-0C18A66EFFFC}"), pool);

      logger("NFO files wihtout movie file");
      _FindDeletedMovieFiles(pool);

      //pool.Rebalance(logger);

      Console.WriteLine("READY.");
      Console.ReadKey(false);
    }

    private static void _DeleteFilesAlsoOnPool(DirectoryInfo root, IPool pool) {
      foreach (var t in _FilesAlsoOnPool(root, pool)) {
        Console.WriteLine($"File also on pool: {t.Item2[0].FullName} - deleting");
        t.Item1.TryDelete();
      }
    }

    private static IEnumerable<Tuple<FileInfo, IFile[]>> _FilesAlsoOnPool(DirectoryInfo root, IPool pool) {
      var poolFiles = new Dictionary<string, List<IFile>>(StringComparer.OrdinalIgnoreCase);
      foreach (var drive in pool.Drives)
        foreach (var file in drive.Items.EnumerateFiles(true))
          poolFiles.GetOrAdd(file.FullName, _ => new List<IFile>()).Add(file);

      var length = root.FullName.Length;
      foreach (var file in root.EnumerateFiles("*.*", SearchOption.AllDirectories)) {
        var fileName = file.FullName;
        if (string.Equals(file.Directory.Name, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
          fileName = Path.Combine(file.Directory.Parent.FullName, file.Name);

        var relativeNameToRoot = fileName.Substring(length + 1);
        List<IFile> value;
        if (!poolFiles.TryGetValue(relativeNameToRoot, out value))
          continue;

        yield return Tuple.Create(file, value.ToArray());
      }
    }

    private static void _FindDeletedMovieFiles(IPool pool) {

      // get all files
      var hash = new HashSet<string>();
      foreach (var drive in pool.Drives)
        foreach (var file in drive.Items.EnumerateFiles(true))
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

    /* end of Rebalance method */

  }

}

