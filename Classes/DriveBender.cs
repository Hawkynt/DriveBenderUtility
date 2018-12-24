using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DivisonM {

  internal static class DriveBenderExtensions {

    public static IEnumerable<DriveBender.IFile> EnumerateFiles(this IEnumerable<DriveBender.IFileSystemItem> items, bool suppressExceptions = false) {
      var emptyFileSystemItems = new DriveBender.IFileSystemItem[0];
      var stack = new Stack<IEnumerable<DriveBender.IFileSystemItem>>();
      stack.Push(items);
      while (stack.Count > 0) {
        var current = stack.Pop();
        DriveBender.IFileSystemItem[] cached;
        if (suppressExceptions) {
          try {
            cached = current.ToArray();
          } catch {
            cached = emptyFileSystemItems;
          }
        } else
          cached = current.ToArray();

        foreach (var item in cached)
          switch (item) {
            case DriveBender.IFile file:
              yield return file;
              break;
            case DriveBender.IFolder folder:
              stack.Push(folder.Items);
              break;
          }
      }
    }

  }

  public static partial class DriveBender {
    
    #region NativeMethods

    private static class NativeMethods {
      [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = "GetDiskFreeSpaceEx")]
      [return: MarshalAs(UnmanagedType.Bool)]
      private static extern bool _GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes
      );

      public static (ulong free, ulong total) GetDiskFreeSpace(DirectoryInfo path) {
        if (_GetDiskFreeSpaceEx(path.FullName, out _, out var totalNumberOfBytes, out var totalNumberOfFreeBytes))
          return (totalNumberOfFreeBytes, totalNumberOfBytes);

        throw new System.ComponentModel.Win32Exception();

      }
    }

    #endregion

    #region interface

    internal static class DriveBenderConstants {
      public const string TEMP_EXTENSION = "TEMP.$DriveBender";
      public const string SHADOW_COPY_FOLDER_NAME = "Folder.Duplicate.$DriveBender";
      public const string INFO_EXTENSION = "MP.$DriveBender";
    }

    // ReSharper disable UnusedMember.Global

    public interface IFileSystemItem {
      string Name { get; }
      string FullName { get; }
      IFolder Parent { get; }
    }

    public interface IFile : IFileSystemItem {
      FileInfo Source { get; }
      bool IsShadowCopy { get; }
      ulong Size { get; }
      bool ExistsOnDrive(IVolume volume);
      void MoveToDrive(IVolume targetDrive);
      void CopyToDrive(IVolume targetDrive, bool asPrimary);
    }

    public interface IFolder : IFileSystemItem {
      IEnumerable<IFileSystemItem> Items { get; }
      IVolume Drive { get; }
    }

    public interface IVolume {
      IEnumerable<IFileSystemItem> Items { get; }
      IMountPoint MountPoint { get; }
      string Label { get; }
      string Name { get; }
      string Description { get; }
      Guid Id { get; }
      ulong BytesTotal { get; }
      ulong BytesFree { get; }
      ulong BytesUsed { get; }
    }

    public interface IMountPoint {
      IEnumerable<IVolume> Volumes { get; }
      string Name { get; }
      string Description { get; }
      Guid Id { get; }
      ulong BytesTotal { get; }
      ulong BytesFree { get; }
      ulong BytesUsed { get; }
      void Rebalance();
      void RestoreMissingPrimaries();
      void CreateMissingShadowCopies();
      IEnumerable<IFileSystemItem> GetItems(string path, SearchOption searchOption);
      IEnumerable<IFileSystemItem> GetItems(SearchOption searchOption);
    }

    // ReSharper restore UnusedMember.Global

    #endregion

    private static Action<string> _logger;
    private static readonly Action<string> _DEFAULT_LOGGER = s => { Trace.WriteLine(s); };

    public static Action<string> Logger {
      get => _logger?? _DEFAULT_LOGGER;
      set => _logger = value;
    }

    public static IMountPoint[] DetectedMountPoints => (
      from drive in _FindAllDrivesWithMountPoints()
      group drive by drive.Id
      into pool
      select (IMountPoint) new MountPoint(pool)
    ).ToArray();

#region concrete

    [DebuggerDisplay("{" + nameof(Name) + "}({" + nameof(Description) + "})")]
    private partial class MountPoint : IMountPoint {

      private readonly Volume[] _volumes;

      public MountPoint(IEnumerable<PoolDriveWithoutPool> drives) {
        if (drives == null)
          throw new ArgumentNullException(nameof(drives));

        this._volumes = drives.Select(d => d.AttachTo(this)).ToArray();
        var first = this._volumes.First();
        this.Name = first.Label;
        this.Description = first.Description;
        this.Id = first.Id;

      }

      #region Implementation of IMountPoint

      public IEnumerable<IVolume> Volumes => this._volumes;
      public string Name { get; }
      public string Description { get; }
      public Guid Id { get; }

      [DebuggerDisplay("{" + nameof(_FormatBytesTotal) + "}")]
      public ulong BytesTotal => this._volumes.Sum(d=>d.BytesTotal);

      [DebuggerHidden]
      private string _FormatBytesTotal => FormatSize(this.BytesTotal);

      [DebuggerDisplay("{" + nameof(_FormatBytesFree) + "}")]
      public ulong BytesFree => this._volumes.Sum(d => d.BytesFree);

      [DebuggerHidden]
      private string _FormatBytesFree => FormatSize(this.BytesFree);

      [DebuggerDisplay("{" + nameof(_FormatBytesUsed) + "}")]
      public ulong BytesUsed => this._volumes.Sum(d => d.BytesUsed);

      [DebuggerHidden]
      private string _FormatBytesUsed => FormatSize(this.BytesUsed);

      public IEnumerable<IFileSystemItem> GetItems(SearchOption searchOption)
        => this.GetItems(string.Empty, searchOption)
      ;

      public IEnumerable<IFileSystemItem> GetItems(string path,SearchOption searchOption) {

        var alreadySeen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // TODO: folder loop
        alreadySeen.Clear();
        
        // return primaries first
        foreach (var volume in this._volumes) {
          var primaryDirectory= volume.Root.Directory(path);
          var parent = new Folder(volume, primaryDirectory, null);

          foreach (var item in primaryDirectory.EnumerateFileSystemInfos()) {
            if(string.Equals(item.Name, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
              continue;
            if (alreadySeen.Contains(item.Name))
              continue;

            alreadySeen.Add(item.Name);
            switch (item) {
              case FileInfo file:
                yield return new File(file,parent, false);
                break;
              case DirectoryInfo directory:
                yield return new Folder(volume,directory, parent);
                break;
            }
          }
        }

        // search for shadow copies without primary
        foreach (var volume in this._volumes) {
          var primaryDirectory = volume.Root.Directory(path);
          var shadowDirectory = primaryDirectory.Directory(DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);
          var parent = new Folder(volume, primaryDirectory, null);

          foreach (var file in shadowDirectory.EnumerateFiles()) {
            if (string.Equals(file.Name, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
              continue;
            if (alreadySeen.Contains(file.Name))
              continue;

            alreadySeen.Add(file.Name);

            yield return new File(file, parent, true);
          }
        }
      }

#endregion

    }

    [DebuggerDisplay("{" + nameof(_Name) + "}({" + nameof(_description) + "})")]
    private class PoolDriveWithoutPool {

      private readonly DirectoryInfo _root;
      private readonly string _label;
      private readonly string _description;

      public PoolDriveWithoutPool(string label, string description, Guid id, DirectoryInfo root) {
        this._root = root ?? throw new ArgumentNullException(nameof(root));
        this._label = label;
        this._description = description;
        this.Id = id;
      }

      private string _Name => this._root.Parent?.Name;
      public Guid Id { get; }

      public Volume AttachTo(IMountPoint mountPoint) => new Volume(mountPoint, this._label, this._description, this.Id, this._root);

    }

    [DebuggerDisplay("{Root.FullName}:{" + nameof(Label) + "}")]
    private class Volume : IVolume {

      public DirectoryInfo Root { get; }

      internal Volume(IMountPoint mountPoint, string label, string description, Guid id, DirectoryInfo root) {
        this.MountPoint = mountPoint;
        this.Label = label;
        this.Description = description;
        this.Id = id;
        this.Root = root;
      }

#region Implementation of IPoolDrive

      public IEnumerable<IFileSystemItem> Items => _EnumeratePoolDirectory(this.Root, this, null);
      public IMountPoint MountPoint { get; }
      public string Label { get; }
      public string Name => this.Root.Parent?.Name;
      public string Description { get; }
      public Guid Id { get; }

      [DebuggerDisplay("{" + nameof(_FormatBytesTotal) + "}")]
      public ulong BytesTotal => NativeMethods.GetDiskFreeSpace(this.Root).total;

      [DebuggerHidden]
      private string _FormatBytesTotal => FormatSize(this.BytesTotal);

      [DebuggerDisplay("{" + nameof(_FormatBytesFree) + "}")]
      public ulong BytesFree => NativeMethods.GetDiskFreeSpace(this.Root).free;

      [DebuggerHidden]
      private string _FormatBytesFree => FormatSize(this.BytesFree);

      [DebuggerDisplay("{" + nameof(_FormatBytesUsed) + "}")]
      public ulong BytesUsed {
        get {
          var result = NativeMethods.GetDiskFreeSpace(this.Root);
          return result.total - result.free;
        }
      }

      [DebuggerHidden]
      private string _FormatBytesUsed => FormatSize(this.BytesUsed);

#endregion
    }

    [DebuggerDisplay("{" + nameof(Name) + "}")]
    private class Folder : IFolder {

      private readonly DirectoryInfo _physical;
      private readonly Volume _drive;
      private readonly Folder _parent;

      public Folder(Volume drive, DirectoryInfo physical, Folder parent) {
        this._drive = drive ?? throw new ArgumentNullException(nameof(drive));
        this._physical = physical ?? throw new ArgumentNullException(nameof(physical));
        this._parent = parent;
      }

#region Implementation of IFileSystemItem

      public string Name => this._physical.Name;
      public string FullName => this.Parent == null ? this.Name : Path.Combine(this.Parent.FullName, this.Name);
      public IFolder Parent => this._parent;

#endregion

#region Implementation of IFolder

      public IEnumerable<IFileSystemItem> Items => _EnumeratePoolDirectory(this._physical, this._drive, this);
      public IVolume Drive => this._drive;

#endregion

    }

    [DebuggerDisplay("{" + nameof(Name) + "}")]
    private class File : IFile {

      private readonly Folder _parent;

      public File(FileInfo physical, Folder parent, bool isShadowCopy) {
        this.Source = physical ?? throw new ArgumentNullException(nameof(physical));
        this._parent = parent;
        this.IsShadowCopy = isShadowCopy;
      }

#region Implementation of IFileSystemItem

      public FileInfo Source { get; }
      public string Name => this.Source.Name;
      public string FullName => this.Parent == null ? this.Name : Path.Combine(this.Parent.FullName, this.Name);
      public IFolder Parent => this._parent;

#endregion

#region Implementation of IFile

      public bool IsShadowCopy { get; }

      [DebuggerDisplay("{" + nameof(_FormatSize) + "}")]
      public ulong Size => (ulong) this.Source.Length;

      [DebuggerHidden]
      private string _FormatSize => FormatSize(this.Size);

      public bool ExistsOnDrive(IVolume volume) => _ExistsOnDrive(this, (Volume) volume);
      public void CopyToDrive(IVolume targetDrive, bool asPrimary) => _CopyToDrive(this, (Volume) targetDrive, asPrimary);
      public void MoveToDrive(IVolume targetDrive) => _MoveToDrive(this, (Volume) targetDrive);

#endregion
    }

#endregion

    private static void _CopyToDrive(File file, Volume targetDrive, bool asPrimary) {
      var sourceFile = file.Source;
      sourceFile.Refresh();
      if (!sourceFile.Exists)
        return;

      var targetDirectory = targetDrive.Root.Directory(file.Parent?.FullName);
      if (!asPrimary)
        targetDirectory = targetDirectory.Directory(DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);

      var targetFile = targetDirectory.File(file.Name);
      var tempFile = targetDirectory.File(targetFile.Name + "." + DriveBenderConstants.TEMP_EXTENSION).FullName;

      Trace.WriteLine($"Copying {sourceFile.FullName} to {targetFile}, {sourceFile.Length} Bytes");
      targetDirectory.Create();

      try {
        sourceFile.CopyTo(tempFile);
        if (!System.IO.File.Exists(tempFile))
          return;

        System.IO.File.Move(tempFile, targetFile.FullName);

      } finally {
        if (!System.IO.File.Exists(tempFile))
          System.IO.File.Delete(tempFile);
      }
    }

    private static void _MoveToDrive(File file, Volume targetDrive) {

      var sourceFile = file.Source;
      sourceFile.Refresh();
      if (!sourceFile.Exists)
        return;

      var targetDirectory = targetDrive.Root.Directory(file.Parent?.FullName);
      if (file.IsShadowCopy)
        targetDirectory = targetDirectory.Directory(DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);

      var targetFile = targetDirectory.File(file.Name);
      var tempFile = targetDirectory.File(targetFile.Name + "." + DriveBenderConstants.TEMP_EXTENSION).FullName;

      Trace.WriteLine($"Moving {sourceFile.FullName} to {targetFile}, {sourceFile.Length} Bytes");
      targetDirectory.Create();

      try {
        sourceFile.CopyTo(tempFile);
        if (!System.IO.File.Exists(tempFile))
          return;

        System.IO.File.Move(tempFile, targetFile.FullName);
        try {
          sourceFile.Delete();
        } catch (UnauthorizedAccessException) {

          // could not delete source file - delete target file, otherwise we have too many copies
          var tries = 3;
          Exception ex = null;
          while (tries-- > 0) {
            try {
              targetFile.Delete();
            } catch (Exception e) {
              ex = e;
              Thread.Sleep(100);
            }
          }

          if (ex != null)
            throw (ex);

        }

      } finally {
        if (!System.IO.File.Exists(tempFile))
          System.IO.File.Delete(tempFile);
      }
    }

    private static bool _ExistsOnDrive(File file, Volume volume) {
      var fullName = file.FullName;
      var parentDirectoryName = Path.GetDirectoryName(fullName);
      var fileNameOnly = Path.GetFileName(fullName);
      if (fileNameOnly == null)
        throw new NotSupportedException("Need valid filename");

      var shadowCopyName = parentDirectoryName == null ? Path.Combine(DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, fileNameOnly) : Path.Combine(parentDirectoryName, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, fileNameOnly);

      return
        System.IO.File.Exists(Path.Combine(volume.Root.FullName, fullName))
        || System.IO.File.Exists(Path.Combine(volume.Root.FullName, shadowCopyName))
        ;
    }

    private static IEnumerable<PoolDriveWithoutPool> _FindAllDrivesWithMountPoints() {
      var results = new List<PoolDriveWithoutPool>();
      for (var i = 0; i < 26; ++i) {
        var letter = (char) ('A' + i);
        results.Clear();
        try {
          var infoFiles = new DirectoryInfo(letter + ":\\").EnumerateFiles("*." + DriveBenderConstants.INFO_EXTENSION);
          foreach (var file in infoFiles) {
            var content = file.ReadAllLines();
            var data = (
                from line in content
                where line.IsNotNullOrWhiteSpace()
                let parts = line.Split(new[] {':'}, 2)
                where parts.Length == 2
                select parts
              ).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase)
              ;

            var label = data.GetValueOrDefault("volumelabel");
            if (label == null)
              continue;

            var idText = data.GetValueOrDefault("id");
            if (!Guid.TryParse(idText, out var id))
              continue;

            var rootDirectory = file.Directory.Directory($"{{{id}}}");
            if (!rootDirectory.Exists)
              continue;

            var description = data.GetValueOrDefault("description");
            results.Add(new PoolDriveWithoutPool(label, description, id, rootDirectory));
          } // foreach info
        } catch (IOException) {
          // ignore missing drives
          continue;
        } catch (UnauthorizedAccessException) {
          // ignore locked drives
          continue;
        }

        if (results.Count>0)
          foreach(var result in results)
            yield return result;
      }
    }

    private static IEnumerable<IFileSystemItem> _EnumeratePoolDirectory(DirectoryInfo di, Volume drive, Folder parent) {
      foreach (var item in di.EnumerateFileSystemInfos()) {
        if (item is FileInfo f) {
          yield return new File(f, parent, false);
          continue;
        }

        if (!(item is DirectoryInfo folder))
          continue;

        if (string.Equals(folder.Name, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase)) {
          foreach (var file in folder.EnumerateFiles())
            yield return new File(file, parent, true);

          continue;
        }

        yield return new Folder(drive, folder, parent);
      }
    }

    internal static string FormatSize(ulong size) {
      const double factor = 1.5;
      const ulong KiB = 1024;
      const ulong MiB = KiB * 1024;
      const ulong GiB = MiB * 1024;
      const ulong TiB = GiB * 1024;
      const ulong PiB = TiB * 1024;
      const ulong EiB = PiB * 1024;

      foreach (var (d, t) in new[] {(EiB, "EiB"), (PiB, "PiB"), (TiB, "TiB"), (GiB, "GiB"), (MiB, "MiB"), (KiB, "KiB")})
        if (size / factor >= d)
          return $"{((double) size / d):0.#}{t}";

      return $"{size:format}B";
    }

  }
}