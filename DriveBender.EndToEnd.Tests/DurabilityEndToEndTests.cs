using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The promise the whole product rests on (SAFE-NOLOSS, SAFE-ATOMIC): data the pool accepted is
/// still there afterwards, and a failure in the middle of a write leaves the file in a state the
/// user can explain — the old content or the new one — never a fabrication.
///
/// These cut the power. The mount is killed outright: no unmount, no flush, no shutdown hook,
/// nothing the engine can do on the way out. A clean unmount can paper over almost any durability
/// defect by flushing as it detaches, so it proves far less than it appears to; only a kill shows
/// what was genuinely on the disks at the moment the lights went off.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class DurabilityEndToEndTests {

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  [Test]
  [Category("HappyPath")]
  [Description("A power cut after files were written and closed: every byte is still there after the pool comes back.")]
  public void Crash_GivenFilesWereWrittenAndClosed_ThenEveryByteSurvivesThePowerCut() {
    using var pool = MountedPool.Create();
    var expected = new Dictionary<string, byte[]>();

    // a mix of sizes, because small files may take a different path than ones that stream
    foreach (var (name, size) in new[] { ("tiny.bin", 11), ("page.bin", 4096), ("big.bin", 3 * 1024 * 1024) }) {
      var content = _Payload(size, size);
      File.WriteAllBytes(pool.PathTo(name), content);
      expected[name] = content;
    }

    // closed and acknowledged — from here on the pool owns the data, whatever happens to it
    pool.CrashAndRemount();

    foreach (var (name, content) in expected) {
      File.Exists(pool.PathTo(name)).Should().BeTrue(
        $"'{name}' was written and closed before the crash, so losing it entirely is losing user data."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content,
        $"'{name}' came back with different bytes than were stored in it");
    }
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A power cut in the middle of overwriting a file: every byte read back afterwards belongs to either the old or the new content, never to neither.")]
  public void Crash_GivenAnOverwriteWasInFlight_ThenNoByteIsFabricated() {
    using var pool = MountedPool.Create();
    const int size = 8 * 1024 * 1024;
    var old = _Payload(size, 1);
    var fresh = _Payload(size, 2);
    var path = pool.PathTo("inflight.bin");

    File.WriteAllBytes(path, old);

    // start overwriting and pull the plug part-way through
    var finished = false;
    var writer = new Thread(() => {
      try {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
        for (var offset = 0; offset < size; offset += 64 * 1024) {
          stream.Write(fresh, offset, Math.Min(64 * 1024, size - offset));
          stream.Flush();
          Thread.Sleep(25); // paced so the overwrite is still in flight when the power goes
        }

        Volatile.Write(ref finished, true);
      } catch (Exception) {
        // the crash is expected to interrupt this
      }
    }) { IsBackground = true };

    writer.Start();
    Thread.Sleep(TimeSpan.FromMilliseconds(1200)); // let a good part of the overwrite land
    pool.CrashAndRemount();
    writer.Join(TimeSpan.FromSeconds(30));

    // Without this the scenario quietly degrades into "crash after the write finished", which
    // proves nothing about an interrupted one and would pass no matter how badly the engine
    // handled the real case.
    Volatile.Read(ref finished).Should().BeFalse(
      "the overwrite has to still be running when the power goes off, or this tests nothing");

    File.Exists(path).Should().BeTrue("a file being overwritten must not vanish because the power went off");
    var got = File.ReadAllBytes(path);

    // The honest oracle. An interrupted overwrite may legitimately leave a mixture of old and new —
    // that is what every filesystem does and what an application that cares uses a temp-and-rename
    // for. What is NEVER acceptable is a byte that was in neither version: a zero-filled gap, a
    // misaligned block, or content bleeding in from another file. Those are invented data, and a
    // user cannot recover from data that merely looks plausible.
    got.Length.Should().BeInRange(0, size, "an interrupted overwrite must not make the file longer than either version");
    var fabricated = Enumerable.Range(0, got.Length)
      .Where(i => got[i] != old[i] && got[i] != fresh[i])
      .Take(8)
      .ToArray();

    fabricated.Should().BeEmpty(
      $"every byte must come from the old content or the new one, but offsets "
      + $"[{string.Join(", ", fabricated)}] hold something that was never written there."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A power cut leaves half-written staging files on the members; none of them may show up in the pool as if they were the user's.")]
  public void Crash_GivenStagedWritesWereInterrupted_ThenNoInternalFileIsExposedToTheUser() {
    using var pool = MountedPool.Create();

    // the same 20 names rewritten round after round: staging is happening continuously, while the
    // disk footprint stays at 5 MiB rather than growing with the test's duration
    const int rounds = 30;
    const int files = 20;
    var written = 0;
    var writer = new Thread(() => {
      try {
        for (var round = 0; round < rounds; ++round)
          for (var file = 0; file < files; ++file) {
            File.WriteAllBytes(pool.PathTo($"staged{file}.bin"), _Payload(256 * 1024, round * files + file));
            Interlocked.Increment(ref written);
          }
      } catch (Exception) {
        // the crash is expected to interrupt this
      }
    }) { IsBackground = true };

    writer.Start();
    Thread.Sleep(TimeSpan.FromMilliseconds(1500));
    pool.CrashAndRemount();
    writer.Join(TimeSpan.FromSeconds(30));

    // the crash has to land while files are still being staged, or there is no interrupted staging
    // to find and the assertions below have nothing to bite on
    Volatile.Read(ref written).Should().BeLessThan(rounds * files,
      "the writer must still be going when the power is cut, or this tests nothing");

    var visible = Directory.EnumerateFileSystemEntries(pool.MountPath, "*", SearchOption.AllDirectories)
      .Select(entry => Path.GetRelativePath(pool.MountPath, entry))
      .ToArray();

    // Staging files (*.TEMP.$DRIVEBENDER) and shadow containers (FOLDER.DUPLICATE.$DRIVEBENDER) are
    // the engine's private business. Leaking them into the user's namespace is not cosmetic: the
    // user tidies up what looks like debris and deletes a copy the pool was relying on, or a backup
    // tool copies a half-written staging file over a good one.
    visible.Where(entry => entry.Contains("$DRIVEBENDER", StringComparison.OrdinalIgnoreCase))
      .Should().BeEmpty($"the pool's internal files must never be visible through the mount."
                        + $"{Environment.NewLine}{pool.MountLog}");

    // and whatever DID survive must be readable — a listed file that cannot be opened is worse
    // than a missing one, because a backup run stops on it
    foreach (var entry in visible.Where(e => File.Exists(pool.PathTo(e))))
      FluentActions.Invoking(() => File.ReadAllBytes(pool.PathTo(entry))).Should().NotThrow(
        $"'{entry}' survived the crash as a listed file, so it has to be readable");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A file deleted before a power cut stays deleted afterwards, rather than being resurrected from a member that had not caught up.")]
  public void Crash_GivenADeleteWasAcknowledged_ThenTheFileDoesNotComeBack() {
    using var pool = MountedPool.Create(poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var path = pool.PathTo("deleted.bin");

    File.WriteAllBytes(path, _Payload(1024 * 1024, 7));
    pool.WaitForPhysicalCopies("deleted.bin", atLeast: 2, TimeSpan.FromMinutes(1));

    File.Delete(path);
    File.Exists(path).Should().BeFalse("the delete was acknowledged");

    pool.CrashAndRemount();

    // A resurrected file is a data-integrity failure in the other direction, and duplication is
    // exactly where it comes from: one copy is removed, the machine dies before the other is, and a
    // naive rescan finds the survivor and calls it a file. Users delete things for reasons — an
    // exported key, a customer record they are legally required to erase.
    File.Exists(path).Should().BeFalse(
      $"a file deleted before the crash must not come back after it."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A power cut during a rename: the file is at one of the two names with its content intact, never at neither.")]
  public void Crash_GivenARenameWasInFlight_ThenTheFileExistsUnderExactlyOneName() {
    using var pool = MountedPool.Create();
    var content = _Payload(1024 * 1024, 9);

    for (var file = 0; file < 12; ++file)
      File.WriteAllBytes(pool.PathTo($"from{file}.bin"), content);

    var renamed = 0;
    var renamer = new Thread(() => {
      try {
        for (var file = 0; file < 12; ++file) {
          File.Move(pool.PathTo($"from{file}.bin"), pool.PathTo($"to{file}.bin"));
          Interlocked.Increment(ref renamed);
          Thread.Sleep(50);
        }
      } catch (Exception) {
        // the crash is expected to interrupt this
      }
    }) { IsBackground = true };

    renamer.Start();
    Thread.Sleep(TimeSpan.FromMilliseconds(400));
    pool.CrashAndRemount();
    renamer.Join(TimeSpan.FromSeconds(30));

    // some must have been renamed and some not, otherwise the crash missed the window entirely
    Volatile.Read(ref renamed).Should().BeInRange(1, 11,
      "the crash must land in the middle of the renames, or this tests nothing");

    for (var file = 0; file < 12; ++file) {
      var source = pool.PathTo($"from{file}.bin");
      var destination = pool.PathTo($"to{file}.bin");
      var present = new[] { source, destination }.Where(File.Exists).ToArray();

      // A rename is a promise of atomicity. Losing the file because the power went off between
      // removing the old name and recording the new one is the classic way a move eats data.
      present.Should().NotBeEmpty(
        $"file {file} was renamed across the crash and now exists under NEITHER name."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

      foreach (var name in present)
        File.ReadAllBytes(name).Should().Equal(content,
          $"'{Path.GetFileName(name)}' survived the rename, so it must have survived it whole");
    }
  }

}
