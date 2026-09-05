using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Writes racing a FOLDER rename — the one data-loss risk this repository has written down and never
/// reproduced.
///
/// From docs/Issues.md: <c>_RenameFolder</c> holds leases on the two folder paths but on no CHILD
/// FILE while moving them. It flushes dirty children and publishes staged ones first, but takes no
/// lease on any of them, so a write can land between that flush and the member-level RenameFolder and
/// address a path whose physical file has since moved. The note adds that the stress suite races file
/// renames only, which is why it would not be caught.
///
/// So this races the case nothing else does. The oracle is the one that matters and needs no timing:
/// a write that was ACKNOWLEDGED must be findable afterwards. The writer follows the folder as it
/// moves and remembers the last version it was told had been stored; if the file then holds anything
/// else, an acknowledged write was lost.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class FolderRenameRaceEndToEndTests {

  private const int _SIZE = 64 * 1024;

  /// <summary>
  /// Content that says which version it is in every block, so a survivor can be identified without
  /// keeping every version around — and so a BLEND of two versions is visible rather than merely
  /// being "not equal to what we expected".
  /// </summary>
  private static byte[] _Version(int version) {
    var content = new byte[_SIZE];
    for (var offset = 0; offset < content.Length; offset += 4)
      BitConverter.TryWriteBytes(content.AsSpan(offset), version);

    return content;
  }

  private static int? _VersionOf(byte[] content) {
    if (content.Length != _SIZE)
      return null;

    var first = BitConverter.ToInt32(content, 0);
    for (var offset = 0; offset < content.Length; offset += 4)
      if (BitConverter.ToInt32(content, offset) != first)
        return null; // a blend of two versions — worse than a stale read

    return first;
  }

  /// <summary>
  /// Several children, several writers, and the whole thing repeated.
  ///
  /// The window the note describes opens between <c>_RenameFolder</c> flushing its dirty children and
  /// the member-level rename, so it is WIDER the more children there are to flush — one file gives
  /// the narrowest possible target. A race scenario that runs once and passes has also not said much:
  /// repeating it is the cheapest way to turn "did not happen this time" into evidence.
  /// </summary>
  private const int _CHILDREN = 4;

  [Test]
  [Category("EdgeCase")]
  [Repeat(4)]
  [Description("A folder renamed under files that are being written: no write the pool acknowledged may go missing.")]
  public void RenameFolder_WhileAChildIsBeingWritten_ThenNoAcknowledgedWriteIsLost() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);

    const string a = "folder-a";
    const string b = "folder-b";
    Directory.CreateDirectory(pool.PathTo(a));
    for (var child = 0; child < _CHILDREN; ++child)
      File.WriteAllBytes(Path.Combine(pool.PathTo(a), $"child{child}.bin"), _Version(0));

    var stop = false;
    var lastAcknowledged = new int[_CHILDREN];
    var acknowledgedWrites = 0;
    var renames = 0;

    // each writer follows the folder wherever it currently is, and records ONLY versions the pool
    // said it had taken
    var writers = Enumerable.Range(0, _CHILDREN).Select(child => new Thread(() => {
      for (var version = 1; !Volatile.Read(ref stop); ++version) {
        var content = _Version(version);
        foreach (var folder in new[] { a, b }) {
          try {
            File.WriteAllBytes(Path.Combine(pool.PathTo(folder), $"child{child}.bin"), content);
            Volatile.Write(ref lastAcknowledged[child], version);
            Interlocked.Increment(ref acknowledgedWrites);
            break;
          } catch (IOException) {
            // the folder is mid-rename; try its other name
          } catch (UnauthorizedAccessException) {
            // same
          }
        }
      }
    }) { IsBackground = true }).ToArray();

    // …while the folder moves back and forth underneath it
    var renamer = new Thread(() => {
      while (!Volatile.Read(ref stop)) {
        foreach (var (from, to) in new[] { (a, b), (b, a) }) {
          try {
            if (Directory.Exists(pool.PathTo(from))) {
              Directory.Move(pool.PathTo(from), pool.PathTo(to));
              Interlocked.Increment(ref renames);
            }
          } catch (IOException) {
            // a rename refused while the child is open is legal; losing data is not
          } catch (UnauthorizedAccessException) {
            // same
          }

          Thread.Sleep(15);
        }
      }
    }) { IsBackground = true };

    foreach (var writer in writers)
      writer.Start();

    renamer.Start();
    Thread.Sleep(TimeSpan.FromSeconds(15));
    Volatile.Write(ref stop, true);

    foreach (var writer in writers)
      writer.Join(TimeSpan.FromMinutes(1)).Should().BeTrue($"a writer must not hang.{Environment.NewLine}{pool.MountLog}");

    renamer.Join(TimeSpan.FromMinutes(1)).Should().BeTrue($"the renamer must not hang.{Environment.NewLine}{pool.MountLog}");

    // The race has to have actually happened, or this proves nothing.
    //
    // On Windows it cannot be constructed this way at all: the OS refuses to rename a directory
    // while files beneath it are open, and with writers hammering four children there is always a
    // handle. Every attempt is declined before the pool ever sees it, which is why this comes back
    // with zero renames there rather than with a race the engine declined to lose data to. Worth
    // saying rather than skipping silently — it also means the window the note describes is far
    // harder to reach on that platform, because the rename that would open it mostly cannot start.
    if (renames == 0 && OperatingSystem.IsWindows())
      Assert.Ignore(
        "Windows refuses to rename a directory while files beneath it are open, so every rename in "
        + $"this scenario was declined by the OS ({acknowledgedWrites} writes were taken meanwhile). "
        + "The race cannot be built here; it runs on Linux, where POSIX allows the rename.");

    renames.Should().BeGreaterThan(5, "the folder must really have moved repeatedly");
    acknowledgedWrites.Should().BeGreaterThan(5, "and writes must really have been taken while it did");

    // wherever the folder ended up, every child is there and holds a version the pool accepted
    var settled = Directory.Exists(pool.PathTo(a)) ? a : b;
    for (var child = 0; child < _CHILDREN; ++child) {
      var path = Path.Combine(pool.PathTo(settled), $"child{child}.bin");

      File.Exists(path).Should().BeTrue(
        $"'child{child}.bin' must exist under whichever name the folder settled at — a folder rename "
        + $"may not lose its contents.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      var version = _VersionOf(File.ReadAllBytes(path));
      version.Should().NotBeNull(
        $"'child{child}.bin' must hold ONE version, not a blend of two — a mixture means a write "
        + $"landed on a path whose physical file had already moved.{Environment.NewLine}{pool.MountLog}");

      version!.Value.Should().Be(Volatile.Read(ref lastAcknowledged[child]),
        $"the last write the pool ACKNOWLEDGED for 'child{child}.bin' must be the one that survived. "
        + $"Anything earlier means an acknowledged write was lost to the rename ({renames} renames, "
        + $"{acknowledgedWrites} acknowledged writes)."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
    }

    pool.IsMountAlive.Should().BeTrue($"the mount must survive the churn.{Environment.NewLine}{pool.MountLog}");
  }

}
