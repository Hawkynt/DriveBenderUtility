using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DriveBender;

namespace DriveBenderUtility {
  internal class Program {
    private static void Main(string[] args) {
      var pools = Pool.Detect();
      var pool = pools.FirstOrDefault();
      if (pool == null)
        return; /* no pool found */

      _RebalancePool(pool);
    }

    /// <summary>
    /// Rebalances files on pool to ensure a good average across all drives.
    /// </summary>
    /// <param name="pool">The pool.</param>
    private static void _RebalancePool(IPool pool) {
      var drives = pool.Drives.ToArray();
      var drivesWithSpaceFree = drives.ToDictionary(d => d, d => d.BytesFree);

      var avgBytesFree = drives.Sum(i => drivesWithSpaceFree[i]) / (ulong)drives.Length;

      const ulong MIN_BYTES_DIFFERENCE_BEFORE_ACTING = 2 * 1024 * 1024UL;
      if (avgBytesFree < MIN_BYTES_DIFFERENCE_BEFORE_ACTING)
        return;

      var valueBeforeGettingDataFrom = avgBytesFree - MIN_BYTES_DIFFERENCE_BEFORE_ACTING;
      var valueBeforePuttingDataTo = avgBytesFree + MIN_BYTES_DIFFERENCE_BEFORE_ACTING;

      var drivesToGetFilesFrom = drives.Where(i => drivesWithSpaceFree[i] < valueBeforeGettingDataFrom).ToArray();
      var drivesToPutFilesTo = drives.Where(i => drivesWithSpaceFree[i] > valueBeforePuttingDataTo).ToArray();

      if (!(drivesToPutFilesTo.Any() && drivesToGetFilesFrom.Any()))
        return;

      foreach (var sourceDrive in drivesToGetFilesFrom) {

        // get all files which could be moved somewhere else
        var files =
          sourceDrive
          .Items
          .EnumerateFiles(true)
          .OrderByDescending(t => t.Size)
          .ToList()
          ;

        // as long as the source drive has less than the calculated average bytes free
        while (drivesWithSpaceFree[sourceDrive] < avgBytesFree) {

          // calculate how many bytes are left until the average is reached
          var bestFit = avgBytesFree - drivesWithSpaceFree[sourceDrive];

          // find the first file, that is nearly big enough
          var fileToMove = files.FirstOrDefault(f => f.Size <= bestFit);
          if (fileToMove == null)
            break; /* no file found to move */

          var fileSize = fileToMove.Size;

          // avoid to move file again
          files.Remove(fileToMove);

          // find a drive to put the file onto (basically it should not be already there and the drive should have enough free bytes available)
          var targetDrive = drivesToPutFilesTo.FirstOrDefault(d => drivesWithSpaceFree[d] > fileSize && !fileToMove.ExistsOnDrive(d));
          if (targetDrive == null)
            continue; /* no target drive big enough */

          // move file to target drive
          fileToMove.MoveToDrive(targetDrive);

          drivesWithSpaceFree[targetDrive] -= fileSize;
          drivesWithSpaceFree[sourceDrive] += fileSize;
        }

      } /* next overloaded drive */

    } /* end of Rebalance method */

  }
}

namespace DriveBender {

  #region NativeMethods

  internal static class NativeMethods {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
      string lpDirectoryName,
      out ulong lpFreeBytesAvailable,
      out ulong lpTotalNumberOfBytes,
      out ulong lpTotalNumberOfFreeBytes
    );

    public static Tuple<ulong, ulong> GetDiskFreeSpace(DirectoryInfo path) {
      ulong freeBytesAvailable;
      ulong totalNumberOfBytes;
      ulong totalNumberOfFreeBytes;

      var success = GetDiskFreeSpaceEx(path.FullName, out freeBytesAvailable, out totalNumberOfBytes, out totalNumberOfFreeBytes);
      if (!success)
        throw new System.ComponentModel.Win32Exception();

      return Tuple.Create(totalNumberOfFreeBytes, totalNumberOfBytes);
    }
  }

  #endregion

  #region interface

  internal static class DriveBenderConstants {
    public const string SHADOW_COPY_FOLDER_NAME = "Folder.Duplicate.$DriveBender";
    public const string INFO_EXTENSION = "$DriveBender";
  }

  internal static class DriveBenderExtensions {

    public static IEnumerable<IFile> EnumerateFiles(this IEnumerable<IFileSystemItem> items, bool suppressExceptions = false) {
      var emptyFileSystemItems = new IFileSystemItem[0];
      var stack = new Stack<IEnumerable<IFileSystemItem>>();
      stack.Push(items);
      while (stack.Count > 0) {
        var current = stack.Pop();
        IFileSystemItem[] cached;
        if (suppressExceptions) {
          try {
            cached = current.ToArray();
          } catch {
            cached = emptyFileSystemItems;
          }
        } else
          cached = current.ToArray();

        foreach (var item in cached) {
          var file = item as IFile;
          if (file != null)
            yield return file;

          var folder = item as IFolder;
          if (folder != null)
            stack.Push(folder.Items);
        }
      }
    }

  }

  public interface IFileSystemItem {
    string Name { get; }
    string FullName { get; }
    IFolder Parent { get; }
  }

  public interface IFile : IFileSystemItem {
    bool IsShadowCopy { get; }
    ulong Size { get; }
    bool ExistsOnDrive(IPoolDrive poolDrive);
    void MoveToDrive(IPoolDrive targetDrive);
  }

  public interface IFolder : IFileSystemItem {
    IEnumerable<IFileSystemItem> Items { get; }
    IPoolDrive Drive { get; }
  }

  public interface IPoolDrive {
    IEnumerable<IFileSystemItem> Items { get; }
    IPool Pool { get; }
    string Name { get; }
    string Description { get; }
    Guid Id { get; }
    ulong BytesTotal { get; }
    ulong BytesFree { get; }
    ulong BytesUsed { get; }
  }

  public interface IPool {
    IEnumerable<IPoolDrive> Drives { get; }
    string Name { get; }
    string Description { get; }
    Guid Id { get; }
    // TODO: allow enumeration of all pool files and link them to all pool drives on which they are present
  }

  #endregion

  #region concrete

  public class Pool : IPool {

    #region Implementation of IPool

    public IEnumerable<IPoolDrive> Drives { get; private set; }
    public string Name => this.Drives.First().Name;
    public string Description => this.Drives.First().Description;
    public Guid Id => this.Drives.First().Id;

    #endregion

    public static IPool[] Detect() {
      var drives = new List<PrivatePoolDrive>();
      for (var i = 0; i < 26; ++i) {
        var letter = (char)('A' + i);
        try {
          var infoFiles = new DirectoryInfo(letter + ":\\").EnumerateFiles("*." + DriveBenderConstants.INFO_EXTENSION);
          foreach (var file in infoFiles) {
            var content = file.ReadAllLines();
            var data = (
              from line in content
              where line.IsNotNullOrWhiteSpace()
              let parts = line.Split(new[] { ':' }, 2)
              where parts.Length == 2
              select parts
            ).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase)
            ;

            var idText = data.GetValueOrDefault("id");
            var label = data.GetValueOrDefault("volumelabel");
            var description = data.GetValueOrDefault("description");

            Guid id;
            if (!Guid.TryParse(idText, out id))
              continue;

            if (label == null)
              continue;

            drives.Add(new PrivatePoolDrive(label, description, id, file.Directory.Directory($"{{{id}}}")));
          }
        } catch (IOException e) {
          // ignore missing drives
        }
      }

      var results = new List<Pool>();
      foreach (var poolGroup in drives.GroupBy(d => d.Id)) {
        var pool = new Pool();
        pool.Drives = poolGroup.Select(i => i.AttachTo(pool)).ToArray();
        results.Add(pool);
      }
      return results.Cast<IPool>().ToArray();
    }

  }

  internal class PrivatePoolDrive : IPoolDrive {

    private readonly DirectoryInfo _root;

    public PrivatePoolDrive(string name, string description, Guid id, DirectoryInfo root) {
      this.Name = name;
      this.Description = description;
      this.Id = id;
      this._root = root;
    }

    #region Implementation of IPoolDrive

    public IEnumerable<IFileSystemItem> Items => null;
    public IPool Pool => null;
    public string Name { get; }
    public string Description { get; }
    public Guid Id { get; }
    public ulong BytesTotal { get { throw new NotImplementedException(); } }
    public ulong BytesFree { get { throw new NotImplementedException(); } }
    public ulong BytesUsed { get { throw new NotImplementedException(); } }

    #endregion

    public PoolDrive AttachTo(IPool pool) => new PoolDrive(pool, this.Name, this.Description, this.Id, this._root);
  }

  [DebuggerDisplay("{Root.FullName}:{Name}")]
  public class PoolDrive : IPoolDrive {
    public DirectoryInfo Root { get; }

    internal PoolDrive(IPool pool, string name, string description, Guid id, DirectoryInfo root) {
      this.Pool = pool;
      this.Name = name;
      this.Description = description;
      this.Id = id;
      this.Root = root;
    }

    #region Implementation of IPoolDrive

    public IEnumerable<IFileSystemItem> Items => Folder.Enumerate(this.Root, this, null);
    public IPool Pool { get; }
    public string Name { get; }
    public string Description { get; }
    public Guid Id { get; }
    public ulong BytesTotal => NativeMethods.GetDiskFreeSpace(this.Root).Item2;
    public ulong BytesFree => NativeMethods.GetDiskFreeSpace(this.Root).Item1;

    public ulong BytesUsed {
      get {
        var result = NativeMethods.GetDiskFreeSpace(this.Root);
        return result.Item2 - result.Item1;
      }
    }

    #endregion
  }

  [DebuggerDisplay("{Name}")]
  public class Folder : IFolder {

    private readonly DirectoryInfo _physical;
    public Folder(DirectoryInfo physical, IFolder parent, IPoolDrive drive) {
      this._physical = physical;
      this.Parent = parent;
      this.Drive = drive;
    }

    #region Implementation of IFileSystemItem

    public string Name => this._physical.Name;
    public string FullName => this.Parent == null ? this.Name : Path.Combine(this.Parent.FullName, this.Name);
    public IFolder Parent { get; }

    #endregion

    #region Implementation of IFolder

    public IEnumerable<IFileSystemItem> Items => Enumerate(this._physical, this.Drive, this);
    public IPoolDrive Drive { get; }

    #endregion

    internal static IEnumerable<IFileSystemItem> Enumerate(DirectoryInfo di, IPoolDrive drive, IFolder parent) {
      foreach (var item in di.EnumerateFileSystemInfos()) {
        var folder = item as DirectoryInfo;
        if (folder == null) {
          yield return new File((FileInfo)item, parent, false);
          continue;
        }

        if (string.Equals(folder.Name, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase)) {
          foreach (var file in folder.EnumerateFiles())
            yield return new File(file, parent, true);

          continue;
        }

        yield return new Folder(folder, parent, drive);
      }
    }

  }

  [DebuggerDisplay("{Name}")]
  public class File : IFile {

    private readonly FileInfo _physical;
    public File(FileInfo physical, IFolder parent, bool isShadowCopy) {
      this._physical = physical;
      this.Parent = parent;
      this.IsShadowCopy = isShadowCopy;
    }

    #region Implementation of IFileSystemItem

    public string Name => this._physical.Name;
    public string FullName => this.Parent == null ? this.Name : Path.Combine(this.Parent.FullName, this.Name);
    public IFolder Parent { get; }

    #endregion

    #region Implementation of IFile

    public bool IsShadowCopy { get; }
    public ulong Size => (ulong)this._physical.Length;

    public bool ExistsOnDrive(IPoolDrive poolDrive) {
      var fullName = this.FullName;
      var shadowCopyName = Path.Combine(Path.GetDirectoryName(fullName), DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, Path.GetFileName(fullName));

      var internalPoolDrive = (PoolDrive)poolDrive;
      return
        System.IO.File.Exists(Path.Combine(internalPoolDrive.Root.FullName, fullName))
        || System.IO.File.Exists(Path.Combine(internalPoolDrive.Root.FullName, shadowCopyName))
        ;
    }

    public void MoveToDrive(IPoolDrive targetDrive) {
      var internalPoolDrive = (PoolDrive)targetDrive;

      var sourceFile = this._physical;
      var fullName = this.FullName;
      var targetFileName = Path.Combine(internalPoolDrive.Root.FullName, Path.GetDirectoryName(fullName));
      if (this.IsShadowCopy)
        targetFileName = Path.Combine(targetFileName, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);

      targetFileName = Path.Combine(targetFileName, Path.GetFileName(fullName));
      Trace.WriteLine($"Moving {sourceFile.FullName} to {targetFileName}, {sourceFile.Length} Bytes");
      sourceFile.MoveTo(targetFileName);
    }

    #endregion
  }

  #endregion

}
