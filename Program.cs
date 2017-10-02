using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DriveBender;

namespace DriveBenderUtility {
  internal class Program {
    private static void Main(string[] args) {
      var pools = Pool.Detect();
      Debugger.Break();
    }
  }
}

namespace DriveBender {

  #region interface

  public interface IFileSystemItem {
    string Name { get; }
    string FullName { get; }
    IFolder Parent { get; }
  }

  public interface IFile : IFileSystemItem {
    bool IsShadowCopy { get; }
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
  }

  public interface IPool {
    IEnumerable<IPoolDrive> Drives { get; }
    string Name { get; }
    string Description { get; }
    Guid Id { get; }
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
      const string INFO_EXTENSION = "$DriveBender";

      var drives = new List<PrivatePoolDrive>();
      for (var i = 0; i < 26; ++i) {
        var letter = (char)('A' + i);
        try {
          var infoFiles = new DirectoryInfo(letter + ":\\").EnumerateFiles("*." + INFO_EXTENSION);
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

    #endregion

    public PoolDrive AttachTo(IPool pool) => new PoolDrive(pool, this.Name, this.Description, this.Id, this._root);
  }

  [DebuggerDisplay("{Name}")]
  public class PoolDrive : IPoolDrive {
    private readonly DirectoryInfo _root;

    internal PoolDrive(IPool pool, string name, string description, Guid id, DirectoryInfo root) {
      this.Pool = pool;
      this.Name = name;
      this.Description = description;
      this.Id = id;
      this._root = root;
    }

    #region Implementation of IPoolDrive

    public IEnumerable<IFileSystemItem> Items => Folder.Enumerate(this._root, this, null);
    public IPool Pool { get; }
    public string Name { get; }
    public string Description { get; }
    public Guid Id { get; }

    #endregion
  }

  [DebuggerDisplay("{Name}")]
  public class Folder : IFolder {

    private const string FOLDER_NAME = "Folder.Duplicate.$DriveBender";

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

        if (string.Equals(folder.Name, FOLDER_NAME, StringComparison.OrdinalIgnoreCase)) {
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

    #endregion
  }

  #endregion

}
