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

    public static IEnumerable<DriveBender.IPhysicalFile> EnumerateFiles(this IEnumerable<DriveBender.IPhysicalFileSystemItem> items, bool suppressExceptions = false) {
      var emptyFileSystemItems = new DriveBender.IPhysicalFileSystemItem[0];
      var stack = new Stack<IEnumerable<DriveBender.IPhysicalFileSystemItem>>();
      stack.Push(items);
      while (stack.Count > 0) {
        var current = stack.Pop();
        DriveBender.IPhysicalFileSystemItem[] cached;
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
            case DriveBender.IPhysicalFile file:
              yield return file;
              break;
            case DriveBender.IPhysicalFolder folder:
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

    #region utils

    internal class SizeFormatter {

      private const ulong KiB = 1024;
      private const ulong MiB = KiB * 1024;
      private const ulong GiB = MiB * 1024;
      private const ulong TiB = GiB * 1024;
      private const ulong PiB = TiB * 1024;
      private const ulong EiB = PiB * 1024;

      private static readonly ValueTuple<ulong, string>[] SIZE_VALUES = { (EiB, "EiB"), (PiB, "PiB"), (TiB, "TiB"), (GiB, "GiB"), (MiB, "MiB"), (KiB, "KiB") };

      public static string Format(ulong size) {
        const double factor = 1.5;
        foreach (var (d, t) in SIZE_VALUES)
          if (size / factor >= d)
            return $"{((double)size / d):0.#}{t}";

        return $"{size}B";
      }
    }

    #endregion

    #region interface

    internal static class DriveBenderConstants {
      public const string TEMP_EXTENSION = "TEMP.$DRIVEBENDER";
      public const string SHADOW_COPY_FOLDER_NAME = "FOLDER.DUPLICATE.$DRIVEBENDER";
      public const string INFO_EXTENSION = "MP.$DRIVEBENDER";
    }

    // ReSharper disable UnusedMember.Global

    #region virtual file/folder stuff

    public interface IFileSystemItem {
      string Name { get; }
      string FullName { get; }
      IFolder Parent { get; }
    }

    public interface IFolder : IFileSystemItem {
      IEnumerable<IFileSystemItem> Items { get; }
      ulong Size { get; }
    }

    public interface IFile : IFileSystemItem {
      ulong Size { get; }
      IVolume Primary { get; }
      IEnumerable<IVolume> Primaries { get; }
      IVolume ShadowCopy { get; }
      IEnumerable<IVolume> ShadowCopies { get; }
    }

    #endregion

    #region physical file/folder stuff

    public interface IPhysicalFileSystemItem {
      string Name { get; }
      string FullName { get; }
      IPhysicalFolder Parent { get; }
    }

    public interface IPhysicalFile : IPhysicalFileSystemItem {
      FileInfo Source { get; }
      bool IsShadowCopy { get; }
      ulong Size { get; }
      bool ExistsOnDrive(IVolume volume);
      void MoveToDrive(IVolume targetDrive);
      void CopyToDrive(IVolume targetDrive, bool asPrimary);
    }

    public interface IPhysicalFolder : IPhysicalFileSystemItem {
      IEnumerable<IPhysicalFileSystemItem> Items { get; }
      IVolume Drive { get; }
    }

    #endregion

    public interface IVolume {
      IEnumerable<IPhysicalFileSystemItem> Items { get; }
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
      IEnumerable<IFileSystemItem> GetItems(string path, SearchOption searchOption);
      IEnumerable<IFileSystemItem> GetItems(SearchOption searchOption);
      void FixMissingShadowCopies();
      void FixMissingPrimaries();
      void FixDuplicateShadowCopies();
      void FixDuplicatePrimaries();
      void FixMissingDuplicationOnAllFolders();
    }

    // ReSharper restore UnusedMember.Global

    #endregion

    private static Action<string> _logger;
    private static readonly Action<string> _DEFAULT_LOGGER = s => { Trace.WriteLine(s); };

    public static Action<string> Logger {
      get => _logger ?? _DEFAULT_LOGGER;
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

      public readonly Volume[] volumes;

      public MountPoint(IEnumerable<PoolDriveWithoutPool> drives) {
        if (drives == null)
          throw new ArgumentNullException(nameof(drives));

        this.volumes = drives.Select(d => d.AttachTo(this)).ToArray();
        var first = this.volumes.First();
        this.Name = first.Label;
        this.Description = first.Description;
        this.Id = first.Id;

      }

      #region Implementation of IMountPoint

      public IEnumerable<IVolume> Volumes => this.volumes;
      public string Name { get; }
      public string Description { get; }
      public Guid Id { get; }

      [DebuggerDisplay("{" + nameof(_FormatBytesTotal) + "}")]
      public ulong BytesTotal => this.volumes.Sum(d => d.BytesTotal);

      [DebuggerHidden]
      private string _FormatBytesTotal => SizeFormatter.Format(this.BytesTotal);

      [DebuggerDisplay("{" + nameof(_FormatBytesFree) + "}")]
      public ulong BytesFree => this.volumes.Sum(d => d.BytesFree);

      [DebuggerHidden]
      private string _FormatBytesFree => SizeFormatter.Format(this.BytesFree);

      [DebuggerDisplay("{" + nameof(_FormatBytesUsed) + "}")]
      public ulong BytesUsed => this.volumes.Sum(d => d.BytesUsed);

      [DebuggerHidden]
      private string _FormatBytesUsed => SizeFormatter.Format(this.BytesUsed);

      public IEnumerable<IFileSystemItem> GetItems(SearchOption searchOption)
        => this.GetItems(string.Empty, searchOption);

      public IEnumerable<IFileSystemItem> GetItems(string path, SearchOption searchOption)
        => _EnumerateItems(this,path, searchOption);

      public void FixMissingShadowCopies() => _FixMissingShadowCopies(this);
      public void FixMissingPrimaries() => _FixMissingPrimaries(this);
      public void FixDuplicateShadowCopies() => _FixDuplicateShadowCopies(this);
      public void FixDuplicatePrimaries() => _FixDuplicatePrimaries(this);
      public void FixMissingDuplicationOnAllFolders() => _FixMissingDuplicationOnAllFolders(this);

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
    private class Volume : IVolume,IEquatable<Volume> {

      public DirectoryInfo Root { get; }

      public Volume(IMountPoint mountPoint, string label, string description, Guid id, DirectoryInfo root) {
        this.MountPoint = mountPoint;
        this.Label = label;
        this.Description = description;
        this.Id = id;
        this.Root = root;
      }

      #region Implementation of IPoolDrive

      public IEnumerable<IPhysicalFileSystemItem> Items => _EnumeratePoolDirectory(this.Root, this, null);
      public IMountPoint MountPoint { get; }
      public string Label { get; }
      public string Name => this.Root.Parent?.Name.TrimEnd('\\','/');
      public string Description { get; }
      public Guid Id { get; }

      [DebuggerDisplay("{" + nameof(_FormatBytesTotal) + "}")]
      public ulong BytesTotal => NativeMethods.GetDiskFreeSpace(this.Root).total;

      [DebuggerHidden]
      private string _FormatBytesTotal => SizeFormatter.Format(this.BytesTotal);

      [DebuggerDisplay("{" + nameof(_FormatBytesFree) + "}")]
      public ulong BytesFree => NativeMethods.GetDiskFreeSpace(this.Root).free;

      [DebuggerHidden]
      private string _FormatBytesFree => SizeFormatter.Format(this.BytesFree);

      [DebuggerDisplay("{" + nameof(_FormatBytesUsed) + "}")]
      public ulong BytesUsed {
        get {
          var result = NativeMethods.GetDiskFreeSpace(this.Root);
          return result.total - result.free;
        }
      }

      [DebuggerHidden]
      private string _FormatBytesUsed => SizeFormatter.Format(this.BytesUsed);

      #endregion

      #region Equality members

      public bool Equals(Volume other) {
        if (ReferenceEquals(null, other))
          return false;
        if (ReferenceEquals(this, other))
          return true;
        return string.Equals(this.Root.FullName,other.Root.FullName,StringComparison.OrdinalIgnoreCase);
      }

      public override bool Equals(object obj) {
        if (ReferenceEquals(null, obj))
          return false;
        if (ReferenceEquals(this, obj))
          return true;
        if (obj.GetType() != this.GetType())
          return false;
        return this.Equals((Volume) obj);
      }

      public override int GetHashCode() => this.Root.FullName.ToLower().GetHashCode();

      public static bool operator ==(Volume left, Volume right) => Equals(left, right);
      public static bool operator !=(Volume left, Volume right) => !Equals(left, right);

      #endregion
    }

    [DebuggerDisplay("{" + nameof(FullName) + "}")]
    private class PhysicalFolder : IPhysicalFolder {

      private readonly DirectoryInfo _physical;
      private readonly Volume _drive;
      private readonly PhysicalFolder _parent;

      public PhysicalFolder(Volume drive, DirectoryInfo physical, PhysicalFolder parent) {
        this._drive = drive ?? throw new ArgumentNullException(nameof(drive));
        this._physical = physical ?? throw new ArgumentNullException(nameof(physical));
        this._parent = parent;
      }

      #region Implementation of IFileSystemItem

      public string Name => this._physical.Name;
      public string FullName => this.Parent == null ? this.Name : Path.Combine(this.Parent.FullName, this.Name);
      public IPhysicalFolder Parent => this._parent;

      #endregion

      #region Implementation of IFolder

      public IEnumerable<IPhysicalFileSystemItem> Items => _EnumeratePoolDirectory(this._physical, this._drive, this);
      public IVolume Drive => this._drive;

      #endregion

    }

    [DebuggerDisplay("{" + nameof(FullName) + "}")]
    private class PhysicalFile : IPhysicalFile {

      private readonly PhysicalFolder _parent;

      public PhysicalFile(FileInfo physical, PhysicalFolder parent, bool isShadowCopy) {
        this.Source = physical ?? throw new ArgumentNullException(nameof(physical));
        this._parent = parent;
        this.IsShadowCopy = isShadowCopy;
      }

      #region Implementation of IFileSystemItem

      public FileInfo Source { get; }
      public string Name => this.Source.Name;
      public string FullName => this.Parent == null ? this.Name : Path.Combine(this.Parent.FullName, this.Name);
      public IPhysicalFolder Parent => this._parent;

      #endregion

      #region Implementation of IFile

      public bool IsShadowCopy { get; }

      [DebuggerDisplay("{" + nameof(_FormatSize) + "}")]
      public ulong Size => (ulong) this.Source.Length;

      [DebuggerHidden]
      private string _FormatSize => SizeFormatter.Format(this.Size);

      public bool ExistsOnDrive(IVolume volume) => _ExistsOnDrive(this, (Volume) volume);
      public void CopyToDrive(IVolume targetDrive, bool asPrimary) => _CopyToDrive(this, (Volume) targetDrive, asPrimary);
      public void MoveToDrive(IVolume targetDrive) => _MoveToDrive(this, (Volume) targetDrive);

      #endregion
    }

    [DebuggerDisplay("{" + nameof(FullName) + "}")]
    private class Folder : IFolder {
      private readonly MountPoint _mountpoint;
      private readonly Folder _parent;

      public Folder(MountPoint mountpoint, string fullName) {
        this._mountpoint = mountpoint;
        this.FullName = fullName;
        this._parent = _GetParentFolder(fullName,mountpoint);
      }
      
      #region Implementation of IFileSystemItem

      public string Name => Path.GetFileName(this.FullName);
    
      public string FullName { get; }

      public IFolder Parent => this._parent;
      public ulong Size => this.Items.OfType<File>().Sum(f => f.Size);

      #endregion

      #region Implementation of IFolder

      public IEnumerable<IFileSystemItem> Items => _EnumerateItems(this._mountpoint, this.FullName, SearchOption.TopDirectoryOnly);

      #endregion
    }

    [DebuggerDisplay("{" + nameof(FullName) + "}")]
    private class File : IFile {
      public readonly MountPoint mountpoint;
      private readonly Folder _parent;

      public File(MountPoint mountpoint, string fullName) {
        this.mountpoint = mountpoint;
        this.FullName = fullName;
        this._parent = _GetParentFolder(fullName, mountpoint);
      }

      public string ShadowCopyFullName => Path.Combine(this._parent?.FullName ?? string.Empty, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, this.Name);

      #region Implementation of IFileSystemItem

      public string Name => Path.GetFileName(this.FullName);

      public string FullName { get; }

      public IFolder Parent => this._parent;

      #endregion

      #region Implementation of IFile

      public ulong Size => _GetFileSize(this);

      public IVolume Primary => this.Primaries.FirstOrDefault();

      public IEnumerable<IVolume> Primaries => _GetPrimaryFileLocations(this).Select(t=>t.volume);

      public IVolume ShadowCopy => this.ShadowCopies.FirstOrDefault();

      public IEnumerable<IVolume> ShadowCopies => _GetShadowCopyFileLocations(this).Select(t => t.volume);

      #endregion
    }

    #endregion

    private static void _FixMissingDuplicationOnAllFolders(MountPoint mountPoint) {
      Logger("Enabling duplication on all folders");
      var folders = mountPoint.GetItems(SearchOption.AllDirectories).OfType<File>().GroupBy(f=>f.Parent?.FullName??string.Empty).Select(g=>g.Key);
      var volumes = mountPoint.Volumes;
      foreach (var folderName in folders) {
        foreach (Volume volume in volumes) {
          var path = volume.Root.Directory(folderName).Directory(DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);
          if(path.Exists)
            continue;

          Logger($@" - Enabling duplication on {folderName} on {volume.Name}");
          Directory.CreateDirectory(path.FullName);
        }
      }
    }

    private static void _FixDuplicatePrimaries(MountPoint mountPoint) {
      Logger("Removing duplicate primaries when both have equal content");
      var files = mountPoint.GetItems(SearchOption.AllDirectories).OfType<File>().Where(f => f.Primaries.Count()>1);
      foreach (var file in files) {
        var locations = _GetPrimaryFileLocations(file).ToArray();
        var first = locations[0].file;
        for (var i = 1; i < locations.Length; ++i) {
          var current = locations[i].file;
          if (!first.IsContentEqualTo(current))
            continue;

          Logger($@" - Deleting redundant primary {current.FullName} from {locations[i].volume.Name}, {SizeFormatter.Format(file.Size)}");
          _TryDelete(current.FullName);
        }
      }
    }

    private static void _FixDuplicateShadowCopies(MountPoint mountPoint) {
      Logger("Removing duplicate shadow-copies when both have equal content");
      var files = mountPoint.GetItems(SearchOption.AllDirectories).OfType<File>().Where(f => f.ShadowCopies.Count() > 1);
      foreach (var file in files) {
        var locations = _GetShadowCopyFileLocations(file).ToArray();
        var first = locations[0].file;
        for (var i = 1; i < locations.Length; ++i) {
          var current = locations[i].file;
          if (!first.IsContentEqualTo(current))
            continue;

          Logger($@" - Deleting redundant shadow-copy {current.FullName} from {locations[i].volume.Name}, {SizeFormatter.Format(file.Size)}");
          _TryDelete(current.FullName);
        }
      }
    }

    private static void _FixMissingPrimaries(MountPoint mountPoint) {
      Logger("Promoting shadow-copies when primaries are missing where needed");
      var files = mountPoint.GetItems(SearchOption.AllDirectories).OfType<File>().Where(f => f.Primary == null);
      foreach (var file in files) {
        var volume = (Volume)file.ShadowCopy;
        Logger($@" - Promoting shadow-copy file {file.FullName} to primary from {volume.Name}, {SizeFormatter.Format(file.Size)}");
        _SetPrimary(file, volume);
      }
    }

    private static void _FixMissingShadowCopies(MountPoint mountPoint) {
      Logger("Restoring shadow-copies from primaries where needed");
      var files = mountPoint.GetItems(SearchOption.AllDirectories).OfType<File>().Where(f => f.ShadowCopy == null);
      foreach (var file in files) {
        var volume = mountPoint.volumes.OrderByDescending(v => v.BytesFree).First(v => (Volume)file.Primary != v);
        Logger($@" - Restoring shadow-copy file {file.FullName} from {file.Primary.Name}, {SizeFormatter.Format(file.Size)} to {volume.Name}");
        _SetShadow(file, volume);
      }
    }

    private static void _SetPrimary(File file, Volume targetDrive) {
      if (file == null)
        throw new ArgumentNullException(nameof(file));
      if (targetDrive == null)
        throw new ArgumentNullException(nameof(targetDrive));

      var target = targetDrive.Root.File(file.FullName);
      var targetTempFileName = target.FullName + "." + DriveBenderConstants.TEMP_EXTENSION;
      _TryDelete(targetTempFileName);

      var primaryFileLocation =  _GetPrimaryFileLocation(file);
      var currentPrimaryVolume = primaryFileLocation.volume;
      var shadowCopyFileLocation = _GetShadowCopyFileLocation(file);
      var currentShadowVolume = shadowCopyFileLocation.volume;

      // if target already primary - nothing to do
      if (targetDrive == currentPrimaryVolume)
        return;

      // target is shadow
      if (targetDrive == currentShadowVolume) {
        var problemAfterRename = false;
        var hasPrimaryLocation = currentPrimaryVolume != null;
        try {

          // rename from shadow
          _Rename(shadowCopyFileLocation.file.FullName, target.FullName);
          problemAfterRename = true;
          if (hasPrimaryLocation) {

            // rename old primary to a shadow - effectively switching volumes
            _Rename(primaryFileLocation.file.FullName, currentPrimaryVolume.Root.File(file.ShadowCopyFullName).FullName);
          }

          problemAfterRename = false;
        } finally {
          if (problemAfterRename && hasPrimaryLocation)
            _Rename(target.FullName, shadowCopyFileLocation.file.FullName);
        }

        return;
      }

      var hasPrimary = currentPrimaryVolume != null;
      var source = hasPrimary ? primaryFileLocation.file : shadowCopyFileLocation.file;

      var problemAfterSecondPrimary = false;
      try {
        _Copy(source.FullName, targetTempFileName);
        _Rename(targetTempFileName, target.FullName);
        problemAfterSecondPrimary = true;

        if (hasPrimary)
          _TryDelete(primaryFileLocation.file.FullName);

        problemAfterSecondPrimary = false;
      } finally {
        _TryDelete(targetTempFileName);

        if (problemAfterSecondPrimary && hasPrimary)
          _TryDelete(target.FullName);
      }
    }

    private static void _SetShadow(File file, Volume targetDrive) {
      if (file == null)
        throw new ArgumentNullException(nameof(file));
      if (targetDrive == null)
        throw new ArgumentNullException(nameof(targetDrive));

      var target = targetDrive.Root.File(file.ShadowCopyFullName);
      var targetTempFileName = target.FullName + "." + DriveBenderConstants.TEMP_EXTENSION;
      _TryDelete(targetTempFileName);

      var primaryFileLocation = _GetPrimaryFileLocation(file);
      var currentPrimaryVolume = primaryFileLocation.volume;
      var shadowCopyFileLocation = _GetShadowCopyFileLocation(file);
      var currentShadowVolume = shadowCopyFileLocation.volume;

      // if target already shadow - nothing to do
      if (targetDrive == currentShadowVolume)
        return;

      // target is primary
      if (targetDrive == currentPrimaryVolume) {
        var problemAfterRename = false;
        var hasShadowLocation = currentShadowVolume != null;
        try {

          // rename from primary
          _Rename(primaryFileLocation.file.FullName, target.FullName);
          problemAfterRename = true;
          if (hasShadowLocation) {

            // rename old shadow to a primary - effectively switching volumes
            _Rename(shadowCopyFileLocation.file.FullName, currentShadowVolume.Root.File(file.FullName).FullName);
          }

          problemAfterRename = false;
        } finally {
          if (problemAfterRename && hasShadowLocation)
            _Rename(target.FullName, primaryFileLocation.file.FullName);
        }

        return;
      }

      var hasShadow = currentShadowVolume != null;
      var source = currentPrimaryVolume != null ? primaryFileLocation.file : shadowCopyFileLocation.file;

      var problemAfterSecondShadow = false;
      try {
        _Copy(source.FullName, targetTempFileName);
        _Rename(targetTempFileName, target.FullName);
        problemAfterSecondShadow = true;

        if (hasShadow)
          _TryDelete(shadowCopyFileLocation.file.FullName);

        problemAfterSecondShadow = false;
      } finally {
        _TryDelete(targetTempFileName);

        if (problemAfterSecondShadow && hasShadow)
          _TryDelete(target.FullName);
      }
    }

    private static void _Copy(string source, string target) {
      Directory.CreateDirectory(Path.GetDirectoryName(target));
      System.IO.File.Copy(source, target);
    }

    private static void _Move(string source, string target) {
      Directory.CreateDirectory(Path.GetDirectoryName(target));
      System.IO.File.Move(source, target);
    }

    private static void _Rename(string source, string target) => System.IO.File.Move(source, target);

    private static void _TryDelete(string fileName) {
      var file=new FileInfo(fileName);
      if (!file.Exists)
        return;

      file.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);
      file.Delete();
    }

    private static void _CopyToDrive(PhysicalFile physicalFile, Volume targetDrive, bool asPrimary) {
      var sourceFile = physicalFile.Source;
      sourceFile.Refresh();
      if (!sourceFile.Exists)
        return;

      var targetDirectory = targetDrive.Root.Directory(physicalFile.Parent?.FullName);
      if (!asPrimary)
        targetDirectory = targetDirectory.Directory(DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);

      var targetFile = targetDirectory.File(physicalFile.Name);
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

    private static void _MoveToDrive(PhysicalFile physicalFile, Volume targetDrive) {

      var sourceFile = physicalFile.Source;
      sourceFile.Refresh();
      if (!sourceFile.Exists)
        return;

      var targetDirectory = targetDrive.Root.Directory(physicalFile.Parent?.FullName);
      if (physicalFile.IsShadowCopy)
        targetDirectory = targetDirectory.Directory(DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);

      var targetFile = targetDirectory.File(physicalFile.Name);
      var tempFile = targetDirectory.File(targetFile.Name + "." + DriveBenderConstants.TEMP_EXTENSION).FullName;

      Trace.WriteLine($"Moving {sourceFile.FullName} to {targetFile}, {sourceFile.Length} Bytes");
      targetDirectory.Create();

      try {
        sourceFile.CopyTo(tempFile);
        if (!System.IO.File.Exists(tempFile))
          return;

        System.IO.File.Move(tempFile, targetFile.FullName);
        sourceFile.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);
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

    private static bool _ExistsOnDrive(PhysicalFile physicalFile, Volume volume) {
      var fullName = physicalFile.FullName;
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

        if (results.Count > 0)
          foreach (var result in results)
            yield return result;
      }
    }

    private static IEnumerable<IPhysicalFileSystemItem> _EnumeratePoolDirectory(DirectoryInfo di, Volume drive, PhysicalFolder parent) {
      foreach (var item in di.EnumerateFileSystemInfos()) {
        if (item is FileInfo f) {
          yield return new PhysicalFile(f, parent, false);
          continue;
        }

        if (!(item is DirectoryInfo folder))
          continue;

        if (string.Equals(folder.Name, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase)) {
          foreach (var file in folder.EnumerateFiles())
            yield return new PhysicalFile(file, parent, true);

          continue;
        }

        yield return new PhysicalFolder(drive, folder, parent);
      }
    }

    private static IEnumerable<IFileSystemItem> _EnumerateItems(MountPoint mountpoint, string path, SearchOption searchOption) {
      var alreadySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      var queue=new Queue<string>();
      queue.Enqueue(path);

      var recursive = searchOption == SearchOption.AllDirectories;
      do {
        path = queue.Dequeue();
        alreadySeen.Clear();

        // return primaries first
        foreach (var volume in mountpoint.volumes) {
          var primaryDirectory = volume.Root.Directory(path);
          if(!primaryDirectory.Exists)
            continue;

          IEnumerable<FileSystemInfo> fileSystemInfos;
          try {
            fileSystemInfos = primaryDirectory.EnumerateFileSystemInfos();
          } catch (UnauthorizedAccessException) {
            
            // ignore inaccessible paths
            continue;
          }

          foreach (var item in fileSystemInfos) {
            if (alreadySeen.Contains(item.Name))
              continue;
            
            alreadySeen.Add(item.Name);
            var fullName = Path.Combine(path, item.Name);

            switch (item) {
              case FileInfo _:
                if (item.Name.EndsWith(DriveBenderConstants.TEMP_EXTENSION, StringComparison.OrdinalIgnoreCase))
                  continue;

                yield return new File(mountpoint, fullName);
                break;
              case DirectoryInfo _:
                if (string.Equals(item.Name, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
                  continue;

                yield return new Folder(mountpoint, fullName);
                queue.Enqueue(fullName);
                break;
            }
          }
        }

        // search for shadow copies without primary
        foreach (var volume in mountpoint.volumes) {
          var primaryDirectory = volume.Root.Directory(path);
          var shadowDirectory = primaryDirectory.Directory(DriveBenderConstants.SHADOW_COPY_FOLDER_NAME);

          if(!shadowDirectory.Exists)
            continue;

          IEnumerable<FileInfo> fileInfos;
          try {
            fileInfos = shadowDirectory.EnumerateFiles();
          } catch (UnauthorizedAccessException) {

            // ignore inaccessible paths
            continue;
          }

          foreach (var file in fileInfos) {
            if (alreadySeen.Contains(file.Name))
              continue;

            alreadySeen.Add(file.Name);

            if (string.Equals(file.Name, DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
              continue;
            if (file.Name.EndsWith(DriveBenderConstants.TEMP_EXTENSION, StringComparison.OrdinalIgnoreCase))
              continue;

            yield return new File(mountpoint, Path.Combine(path, file.Name));
          }
        }

      } while (queue.Count > 0 && recursive);
    }

    private static (Volume volume,FileInfo file) _GetPrimaryFileLocation(File file) 
      => _GetPrimaryFileLocations(file).FirstOrDefault()
    ;

    private static IEnumerable<(Volume volume, FileInfo file)> _GetPrimaryFileLocations(File file) {
      var fullname = file.FullName;
      foreach (var volume in file.mountpoint.volumes) {
        var current = _GetFileIfExists(volume, fullname);
        if (current != null)
          yield return (volume, current);
      }
    }

    private static (Volume volume, FileInfo file) _GetShadowCopyFileLocation(File file)
      => _GetShadowCopyFileLocations(file).FirstOrDefault()
    ;

    private static IEnumerable<(Volume volume, FileInfo file)> _GetShadowCopyFileLocations(File file) {
      var fullname = file.ShadowCopyFullName;
      foreach (var volume in file.mountpoint.volumes) {
        var current = _GetFileIfExists(volume, fullname);
        if (current != null)
          yield return (volume, current);
      }
    }

    private static FileInfo _GetFileIfExists(Volume volume, string fullName) {
      var fileInfo = volume.Root.File(fullName);
      return fileInfo.Exists ? fileInfo : null;
    }

    private static Folder _GetParentFolder(string fullName, MountPoint mountpoint) {
      var parent = Path.GetDirectoryName(fullName);
      if (parent.IsNullOrEmpty())
        return null;

      return new Folder(mountpoint, parent);
    }

    private static ulong _GetFileSize(File file) 
      => (ulong) (_GetPrimaryFileLocation(file).file?.Length ?? _GetShadowCopyFileLocation(file).file?.Length ?? throw new FileNotFoundException("File missing in pool", file.FullName))
    ;
    
  }
}