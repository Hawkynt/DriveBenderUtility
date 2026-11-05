using System;
using System.IO;
using System.Linq;
using DivisonM;
using FluentAssertions;
using NUnit.Framework;

namespace DriveBender.Tests {

  /// <summary>
  /// Operations on a pool's disks that used to delete the last copy of a file, the wrong pool, or a
  /// copy whose content nobody had compared. Each case builds a real pool layout in a temp folder.
  /// </summary>
  [TestFixture]
  public class DataLossSafetyTests : TestBase {

    private const string Shadow = DivisonM.DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME;
    private const string Info = DivisonM.DriveBender.DriveBenderConstants.INFO_EXTENSION;

    private string _root;
    private Func<DivisonM.DriveBender.IMountPoint[]> _originalSource;

    [SetUp]
    public override void SetUp() {
      this._root = Path.Combine(Path.GetTempPath(), $"DataLossSafety_{Guid.NewGuid():N}");
      Directory.CreateDirectory(this._root);
      this._originalSource = PoolManager.MountPointSource;
      DivisonM.DriveBender.Logger = m => TestContext.WriteLine(m);
    }

    [TearDown]
    public override void TearDown() {
      PoolManager.MountPointSource = this._originalSource;
      try {
        Directory.Delete(this._root, true);
      } catch {
        // best effort
      }
    }

    private sealed class Pool {
      public Guid Id;
      public string Name;
      public string[] Drives;
      public string RootOf(int drive) => Path.Combine(this.Drives[drive], $"{{{this.Id}}}");
    }

    private Pool _CreatePool(string name, int drives, Guid? id = null, string[] onDrives = null) {
      var pool = new Pool { Id = id ?? Guid.NewGuid(), Name = name };
      pool.Drives = onDrives ?? Enumerable.Range(0, drives).Select(i => Path.Combine(this._root, $"{name}-d{i}")).ToArray();
      foreach (var drive in pool.Drives) {
        Directory.CreateDirectory(Path.Combine(drive, $"{{{pool.Id}}}"));
        File.WriteAllLines(Path.Combine(drive, $"{name}.{pool.Id:N}.{Info}"), new[] { $"volumelabel:{name}", $"id:{pool.Id}" });
      }

      return pool;
    }

    private static DivisonM.DriveBender.MountPoint _Mount(Pool pool)
      => new DivisonM.DriveBender.MountPoint(pool.Drives.Select(d => new DivisonM.DriveBender.PoolDriveWithoutPool(pool.Name, string.Empty, pool.Id, new DirectoryInfo(Path.Combine(d, $"{{{pool.Id}}}")))));

    private static string _Put(string root, string relative, string content) {
      var path = Path.Combine(root, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      File.WriteAllText(path, content);
      return path;
    }

    // ---- duplication ----------------------------------------------------------------------------

    [Test]
    public void DisableDuplication_GivenAFileThatOnlyHasAShadowCopy_ThenTheShadowCopyIsKept() {
      var pool = this._CreatePool("P", 2);
      var shadow = _Put(pool.RootOf(1), Path.Combine("Movies", Shadow, "a.mkv"), "only copy");

      DuplicationManager.DisableDuplicationOnFolder(_Mount(pool), "Movies");

      File.Exists(shadow).Should().BeTrue("a shadow copy without a primary is the file itself");
      File.ReadAllText(shadow).Should().Be("only copy");
    }

    [Test]
    public void DisableDuplication_GivenAShadowCopyThatDiffersFromItsPrimary_ThenTheShadowCopyIsKept() {
      var pool = this._CreatePool("P", 2);
      _Put(pool.RootOf(0), Path.Combine("Movies", "a.mkv"), "version A");
      var shadow = _Put(pool.RootOf(1), Path.Combine("Movies", Shadow, "a.mkv"), "version B");

      DuplicationManager.DisableDuplicationOnFolder(_Mount(pool), "Movies");

      File.ReadAllText(shadow).Should().Be("version B", "nobody knows which of two different copies is the right one");
    }

    [Test]
    public void DisableDuplication_GivenAnExtraShadowLevelWithoutAPrimary_ThenItsFilesAreKept() {
      var pool = this._CreatePool("P", 2);
      var extra = _Put(pool.RootOf(1), Path.Combine("Movies", Shadow + ".1", "b.mkv"), "only copy");

      DuplicationManager.DisableDuplicationOnFolder(_Mount(pool), "Movies");

      File.Exists(extra).Should().BeTrue();
    }

    [Test]
    public void DisableDuplication_GivenShadowCopiesMatchingTheirPrimaries_ThenTheyAndTheirFoldersAreRemoved() {
      var pool = this._CreatePool("P", 2);
      var primary = _Put(pool.RootOf(0), Path.Combine("Movies", "Sub", "a.mkv"), "same");
      var shadow = _Put(pool.RootOf(1), Path.Combine("Movies", "Sub", Shadow, "a.mkv"), "same");

      DuplicationManager.DisableDuplicationOnFolder(_Mount(pool), "Movies");

      File.Exists(shadow).Should().BeFalse();
      Directory.Exists(Path.GetDirectoryName(shadow)).Should().BeFalse();
      File.ReadAllText(primary).Should().Be("same");
    }

    // ---- integrity repair -----------------------------------------------------------------------

    [Test]
    public void RepairDuplicatePrimary_GivenCopiesWithDifferentContent_ThenBothAreKept() {
      var pool = this._CreatePool("P", 2);
      var older = _Put(pool.RootOf(0), "doc.txt", "the right content");
      var newer = _Put(pool.RootOf(1), "doc.txt", "rotted or stale!!");
      File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-3));
      File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

      var issue = IntegrityChecker.CheckPoolIntegrity(_Mount(pool)).Single(i => i.IssueType == IntegrityChecker.IntegrityIssueType.DuplicatePrimary);
      var repaired = IntegrityChecker.RepairIntegrityIssue(issue, dryRun: false, createBackup: false);

      repaired.Should().BeFalse("the conflict needs a person to decide");
      File.ReadAllText(older).Should().Be("the right content");
      File.ReadAllText(newer).Should().Be("rotted or stale!!");
    }

    [Test]
    public void RepairDuplicatePrimary_GivenIdenticalCopies_ThenOneIsRemoved() {
      var pool = this._CreatePool("P", 2);
      var a = _Put(pool.RootOf(0), "doc.txt", "same");
      var b = _Put(pool.RootOf(1), "doc.txt", "same");

      var issue = IntegrityChecker.CheckPoolIntegrity(_Mount(pool)).Single(i => i.IssueType == IntegrityChecker.IntegrityIssueType.DuplicatePrimary);
      IntegrityChecker.RepairIntegrityIssue(issue, dryRun: false, createBackup: true).Should().BeTrue();

      new[] { a, b }.Count(File.Exists).Should().Be(1);
    }

    [Test]
    public void RepairMissingPrimary_ThenThePrimaryAppearsInsideThePoolFolder() {
      var pool = this._CreatePool("P", 2);
      _Put(pool.RootOf(1), Path.Combine("Docs", Shadow, "x.txt"), "content");

      var issue = IntegrityChecker.CheckPoolIntegrity(_Mount(pool)).Single(i => i.IssueType == IntegrityChecker.IntegrityIssueType.MissingPrimary);
      IntegrityChecker.RepairIntegrityIssue(issue, dryRun: false, createBackup: false).Should().BeTrue();

      File.ReadAllText(Path.Combine(pool.RootOf(1), "Docs", "x.txt")).Should().Be("content");
      File.Exists(Path.Combine(pool.RootOf(1), "Docs", Shadow, "x.txt")).Should().BeTrue();
    }

    // ---- pool selection and info files --------------------------------------------------------

    [Test]
    public void DeletePool_GivenTwoPoolsWithTheSameName_ThenNothingIsDeleted() {
      var first = this._CreatePool("Media", 1);
      var second = this._CreatePool("Media2", 1);
      second.Name = "Media";
      var keepA = _Put(first.RootOf(0), "a.bin", "a");
      var keepB = _Put(second.RootOf(0), "b.bin", "b");
      PoolManager.MountPointSource = () => new DivisonM.DriveBender.IMountPoint[] { _Mount(first), _Mount(second) };

      PoolManager.DeletePool("Media", removeData: true).Should().BeFalse();

      File.Exists(keepA).Should().BeTrue();
      File.Exists(keepB).Should().BeTrue();
    }

    [Test]
    public void DeletePool_GivenADriveSharedWithAnotherPool_ThenTheOtherPoolsInfoFileStays() {
      var drive = Path.Combine(this._root, "shared");
      var a = this._CreatePool("A", 1, onDrives: new[] { drive });
      var b = this._CreatePool("B", 1, onDrives: new[] { drive });
      PoolManager.MountPointSource = () => new DivisonM.DriveBender.IMountPoint[] { _Mount(a), _Mount(b) };

      PoolManager.DeletePool("A").Should().BeTrue();

      File.Exists(Path.Combine(drive, $"B.{b.Id:N}.{Info}")).Should().BeTrue();
      File.Exists(Path.Combine(drive, $"A.{a.Id:N}.{Info}")).Should().BeFalse();
    }

    [Test]
    public void CreatePool_GivenADriveThatAlreadyHoldsAnotherPoolsInfoFile_ThenThatFileIsNotOverwritten() {
      var drive = Path.Combine(this._root, "d");
      Directory.CreateDirectory(drive);
      var existingId = Guid.NewGuid();
      var existing = Path.Combine(drive, $"Pool.{Info}");
      File.WriteAllLines(existing, new[] { "volumelabel:A", $"id:{existingId}" });

      PoolManager.CreatePool("B", "X:\\", new[] { drive }).Should().BeTrue();

      File.ReadAllText(existing).Should().Contain(existingId.ToString());
      Directory.GetFiles(drive, $"*.{Info}").Should().HaveCount(2);
    }

    // ---- removing a drive -----------------------------------------------------------------------

    private static bool _RemoveDrive(Pool pool, int drive, bool moveData) {
      var result = PoolManager.RemoveDriveFromPool(new PoolName(pool.Name), new DrivePath(pool.Drives[drive]), new DriveOperationOptions {
        DryRun = false, CreateBackup = false, PromptUser = false, AutoBalance = moveData
      });
      TestContext.WriteLine($"{result.Message}; {string.Join("; ", result.Warnings ?? Enumerable.Empty<string>())}");
      return result.Success;
    }

    [Test]
    public void RemoveDrive_GivenFilesInSubfolders_ThenEveryFileIsMovedBeforeTheDriveLeaves() {
      var pool = this._CreatePool("P", 2);
      _Put(pool.RootOf(0), Path.Combine("deep", "er", "x.bin"), "nested");
      _Put(pool.RootOf(0), "top.bin", "top");
      PoolManager.MountPointSource = () => new DivisonM.DriveBender.IMountPoint[] { _Mount(pool) };

      _RemoveDrive(pool, 0, moveData: true).Should().BeTrue();

      File.ReadAllText(Path.Combine(pool.RootOf(1), "deep", "er", "x.bin")).Should().Be("nested");
      File.ReadAllText(Path.Combine(pool.RootOf(1), "top.bin")).Should().Be("top");
    }

    [Test]
    public void RemoveDrive_GivenAFileWhoseOtherCopyIsOnTheTarget_ThenBothCopiesAreNotPutOnOneDrive() {
      var pool = this._CreatePool("P", 3);
      _Put(pool.RootOf(0), "f.bin", "data");
      _Put(pool.RootOf(1), Path.Combine(Shadow, "f.bin"), "data");
      PoolManager.MountPointSource = () => new DivisonM.DriveBender.IMountPoint[] { _Mount(pool) };

      _RemoveDrive(pool, 0, moveData: true).Should().BeTrue();

      File.Exists(Path.Combine(pool.RootOf(2), "f.bin")).Should().BeTrue("the drive already holding the shadow copy must not receive the primary too");
      File.Exists(Path.Combine(pool.RootOf(1), "f.bin")).Should().BeFalse();
    }

    [Test]
    public void RemoveDrive_WithoutMovingData_GivenAFileWhoseOnlyCopyIsOnIt_ThenTheDriveStaysAndItsDataIsKept() {
      var pool = this._CreatePool("P", 2);
      var only = _Put(pool.RootOf(0), "only.bin", "irreplaceable");
      PoolManager.MountPointSource = () => new DivisonM.DriveBender.IMountPoint[] { _Mount(pool) };

      _RemoveDrive(pool, 0, moveData: false).Should().BeFalse();

      File.ReadAllText(only).Should().Be("irreplaceable");
      Directory.GetFiles(pool.Drives[0], $"*.{Info}").Should().NotBeEmpty("the drive is still a member");
    }

  }
}
