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

  /// <summary>
  /// Work that runs until something stops it — the crash, normally.
  ///
  /// These scenarios all need the power to go off WHILE something is happening, and each of them
  /// used to arrange that by racing a fixed amount of work against a fixed sleep. That is a bet on
  /// the host's speed, and on a fast enough machine the work finishes first: the scenario quietly
  /// becomes "crash after everything completed", which tests nothing. Both of the guards that catch
  /// that ("or this tests nothing") have now been seen to fire, on a 16-CPU host with tmpfs — and
  /// making the I/O path faster makes them fire MORE often, so raising the workload would only
  /// move the goalposts until the next machine.
  ///
  /// An UNBOUNDED loop removes the bet. There is no amount of work to finish, so the workload is
  /// still running when the power is cut no matter how fast the host is; the only thing left to
  /// establish is that it got far enough to be worth interrupting, which is an observation rather
  /// than a race.
  /// </summary>
  private sealed class Workload : IDisposable {

    private readonly Thread _thread;
    private volatile bool _stop;
    private int _completed;

    public Workload(Action<int> step, TimeSpan pace = default) {
      this._thread = new(() => {
        try {
          for (var iteration = 0; !this._stop; ++iteration) {
            step(iteration);
            Interlocked.Increment(ref this._completed);
            if (pace > TimeSpan.Zero)
              Thread.Sleep(pace);
          }
        } catch (Exception) {
          // the crash is expected to interrupt this
        }
      }) { IsBackground = true };

      this._thread.Start();
    }

    public int Completed => Volatile.Read(ref this._completed);

    /// <summary>Waits until the workload has completed <paramref name="steps"/> steps — the "it got going" observation.</summary>
    public bool ReachedAtLeast(int steps, TimeSpan timeout)
      => MountedPool.WaitUntil(() => this.Completed >= steps, timeout);

    public void Dispose() {
      this._stop = true;
      this._thread.Join(TimeSpan.FromSeconds(30));
    }
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

    // start overwriting and pull the plug part-way through. The stream stays open across the whole
    // overwrite on purpose — an interrupted write with the handle still held is the case the staged
    // -write lifecycle has to survive, and closing per chunk would test something easier.
    const int chunk = 64 * 1024;
    var finished = false;
    var landed = 0;
    var writer = new Thread(() => {
      try {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
        for (var offset = 0; offset < size; offset += chunk) {
          stream.Write(fresh, offset, Math.Min(chunk, size - offset));
          stream.Flush();
          Interlocked.Increment(ref landed);
          Thread.Sleep(25); // paced so the overwrite is still in flight when the power goes
        }

        Volatile.Write(ref finished, true);
      } catch (Exception) {
        // the crash is expected to interrupt this
      }
    }) { IsBackground = true };

    writer.Start();

    // Cut the power on OBSERVED progress rather than after a fixed wait. A fixed wait is a bet on
    // the host: too short and nothing has landed, too long and the overwrite is already over. The
    // pacing above means an eighth of the chunks takes at least 400 ms of wall clock whatever the
    // machine, and leaves seven eighths still to write.
    MountedPool.WaitUntil(() => Volatile.Read(ref landed) >= size / chunk / 8, TimeSpan.FromSeconds(60));
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

    // the same 20 names rewritten round after round, without end: staging happens continuously
    // until the power goes off, while the disk footprint stays at 5 MiB rather than growing with
    // the test's duration
    const int files = 20;
    using var writer = new Workload(iteration =>
      File.WriteAllBytes(pool.PathTo($"staged{iteration % files}.bin"), _Payload(256 * 1024, iteration)));

    // the crash has to land while files are still being staged, or there is no interrupted staging
    // to find and the assertions below have nothing to bite on. The loop above never ends, so being
    // mid-flight is structural; what has to be established is that it got far enough to matter.
    writer.ReachedAtLeast(files * 3, TimeSpan.FromSeconds(60)).Should().BeTrue(
      $"staging never got going — only {writer.Completed} files were written in a minute, so there is "
      + $"nothing interrupted to look for.{Environment.NewLine}{pool.MountLog}");

    pool.CrashAndRemount();
    writer.Dispose(); // stop it here, not at end of scope: it must not write into the REMOUNTED pool

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

    // Renaming back and FORTH, without end: each file moves from→to→from→to for as long as the
    // pool is alive. Sweeping once through twelve files was a race against the host — the sweep
    // finished before the kill landed and the scenario degraded into "crash after the renames",
    // which is not what it is named for. Which direction a file goes is read off the disk rather
    // than tracked, so an interrupted sweep never leaves the loop trying to move a name that is
    // no longer there.
    using var renamer = new Workload(iteration => {
      var file = iteration % 12;
      var source = pool.PathTo($"from{file}.bin");
      var destination = pool.PathTo($"to{file}.bin");
      if (File.Exists(source))
        File.Move(source, destination);
      else
        File.Move(destination, source);
    }, pace: TimeSpan.FromMilliseconds(10));

    // the crash has to land among the renames rather than after them; the loop has no end, so that
    // is structural, and what remains is to see it actually moving
    renamer.ReachedAtLeast(4, TimeSpan.FromSeconds(60)).Should().BeTrue(
      $"only {renamer.Completed} renames happened in a minute, so the crash has nothing to interrupt."
      + $"{Environment.NewLine}{pool.MountLog}");

    pool.CrashAndRemount();
    renamer.Dispose(); // stop it here: it must not go on renaming inside the REMOUNTED pool

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

  [Test]
  [Category("Exception")]
  [Description("A power cut while a member is also missing: everything the surviving member holds is still served, and the pool comes back rather than refusing to start.")]
  public void Crash_GivenAMemberIsAlsoMissingAtRestart_ThenTheSurvivingCopyIsStillServed() {
    using var pool = MountedPool.Create(poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var expected = new Dictionary<string, byte[]>();
    for (var file = 0; file < 6; ++file) {
      var content = _Payload(512 * 1024, 400 + file);
      File.WriteAllBytes(pool.PathTo($"survivor{file}.bin"), content);
      expected[$"survivor{file}.bin"] = content;
      pool.WaitForPhysicalCopies($"survivor{file}.bin", atLeast: 2, TimeSpan.FromMinutes(1));
    }

    // the two failures that actually happen together: the machine dies AND a disk does not come
    // back with it. This is the moment redundancy exists for, and also the moment a pool that
    // refuses to mount without every member turns a survivable incident into an outage.
    pool.Eject(1);
    pool.CrashAndRemount();

    foreach (var (name, content) in expected) {
      File.Exists(pool.PathTo(name)).Should().BeTrue(
        $"'{name}' has a copy on the member that is still here, so a crash plus one lost disk must not lose it."
        + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
      File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content, $"'{name}' must be served whole from the surviving copy");
    }

    // and the disk coming back must not undo any of that
    pool.Restore(1);
    foreach (var (name, content) in expected)
      File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content,
        $"'{name}' must survive the returning member as well.{Environment.NewLine}{pool.DescribeMembers()}");
  }

  [Test]
  [Category("Exception")]
  [Description("Each member took a write while the other was away: the pool serves one whole version, never a mixture of the two.")]
  public void Divergence_GivenEachMemberTookAWriteWhileTheOtherWasAway_ThenOneWholeVersionIsServed() {
    using var pool = MountedPool.Create(poolDefaults: MountedPool.DuplicatedOnOneDisk);
    const int size = 512 * 1024;
    var original = _Payload(size, 500);
    var whileSecondAway = _Payload(size, 501);
    var whileFirstAway = _Payload(size, 502);
    var path = pool.PathTo("diverged.bin");

    File.WriteAllBytes(path, original);
    pool.WaitForPhysicalCopies("diverged.bin", atLeast: 2, TimeSpan.FromMinutes(1));

    // one disk misses a write...
    pool.Eject(1);
    File.WriteAllBytes(path, whileSecondAway);

    // ...and it really did miss it. Without this the scenario collapses into three ordinary
    // overwrites, which any implementation passes while telling us nothing about divergence.
    pool.CopiesOnDetachedStorage(1, "diverged.bin").Should().NotBeEmpty(
      "the detached disk still holds its own copy of the file")
      .And.OnlyContain(copy => copy.SequenceEqual(original),
        "a disk that is not attached cannot have received the write, so its copy must still be the original");

    pool.Restore(1);

    // ...and then the other one does, so neither copy is a superset of the other
    pool.Eject(0);
    File.WriteAllBytes(path, whileFirstAway);

    pool.CopiesOnDetachedStorage(0, "diverged.bin").Should().NotBeEmpty("the other disk holds a copy too")
      .And.NotContain(copy => copy.SequenceEqual(whileFirstAway),
        "the write went in while this disk was detached, so it cannot hold the newest version — "
        + "the two members have genuinely diverged");

    pool.Restore(0);

    MountedPool.WaitUntil(() => false, TimeSpan.FromSeconds(15)); // give heal a chance to converge

    var got = File.ReadAllBytes(path);
    var candidates = new[] { original, whileSecondAway, whileFirstAway };

    // Split brain is unavoidable once both halves accept writes; what is avoidable is answering
    // with a FRANKENSTEIN of the two. A user can reason about "I got the older version" and go to
    // a backup. Nobody can reason about a file that is the first half of one version and the
    // second half of another, because it still opens, still has the right size, and is wrong.
    candidates.Any(candidate => candidate.AsSpan().SequenceEqual(got)).Should().BeTrue(
      $"the pool must answer with one whole version of the file, not a blend of the diverged copies."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    // Deliberately NOT asserting that every physical copy is itself a whole version: heal may be
    // rewriting one of them at the instant the members are sampled, and a copy caught mid-copy
    // looks torn without anything being wrong. The promise that matters is the one above, made
    // through the mount, which is the only thing a user ever sees.
  }

}
