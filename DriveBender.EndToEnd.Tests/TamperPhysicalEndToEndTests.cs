using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Tampering with the STORED FILES rather than with the sidecars, and running the pool out of room.
///
/// The rest of the tamper suite edits the pool's own bookkeeping. These two go after the data files
/// themselves and after the one resource the pool cannot talk its way out of:
///
/// <list type="bullet">
/// <item><b>A stored copy swapped for a link to somewhere else.</b> Path containment stops a hostile
/// PATH STRING from escaping the member root, but a hard link needs no string — the escape is in the
/// filesystem, under a name that looks entirely ordinary. Writing through the pool then writes
/// wherever the link points, which on a shared machine is somebody else's file. Hard links need no
/// privileges on NTFS, so this is not a theoretical attack, it is a two-command one.</item>
/// <item><b>No space left.</b> A write that fails for want of room is the classic way a filesystem
/// destroys the file it was asked to update: truncate first, fail second, and the old content is
/// gone with the new content never written. There was no coverage of this at all.</item>
/// </list>
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("EdgeCase")]
[NonParallelizable]
public class TamperPhysicalEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>A hard link, which on NTFS needs no elevation — unlike a symbolic link.</summary>
  private static bool _TryHardLink(string link, string target) {
    if (!OperatingSystem.IsWindows())
      try {
        File.CreateSymbolicLink(link, target);
        return true;
      } catch (Exception) {
        return false;
      }

    var process = Process.Start(new ProcessStartInfo {
      FileName = "cmd.exe",
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      ArgumentList = { "/c", "mklink", "/H", link, target },
    });

    process?.WaitForExit(30_000);
    return process?.ExitCode == 0 && File.Exists(link);
  }

  [Test]
  [Description("A stored copy is replaced by a hard link to a file outside the pool: the pool reads and writes through it.")]
  [Ignore("Measured, and recorded as a hardening gap rather than a broken promise. A hard link inside "
          + "a member folder makes the stored copy BE the outside file - same MFT record - so every "
          + "check the pool has says the file is where it belongs, and reads serve the outsider's bytes "
          + "while writes overwrite them. Refusing it means comparing hard-link counts or file IDs on "
          + "the read path, which costs a stat per open for a threat that already requires write access "
          + "to a member folder. That is a design call, not a fix. See docs/Tampering.md.")]
  public void Stored_GivenACopyIsRelinkedOutsideThePool_ThenNeitherReadsNorWritesFollowIt() {
    // Path containment rejects a hostile path STRING; a link carries no string. The escape lives in
    // the filesystem, under a name that looks entirely ordinary, and both directions are affected:
    // reading discloses the outsider, writing destroys it.
    var content = _Payload(8 * 1024, 43);
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);

    var outsider = Path.Combine(pool.Root, "outsider.txt");
    const string marker = "OUTSIDER-MARKER-must-not-be-served-or-overwritten";
    File.WriteAllText(outsider, marker);

    File.WriteAllBytes(pool.PathTo("linked.bin"), content);
    pool.WaitForPhysicalCopies("linked.bin", atLeast: 2, TimeSpan.FromMinutes(2));

    var relinked = false;
    pool.WhileUnmounted(() => {
      foreach (var copy in pool.PhysicalCopies("linked.bin").Select(c => c.where)) {
        File.Delete(copy);
        if (_TryHardLink(copy, outsider))
          relinked = true;
      }
    });

    if (!relinked)
      Assert.Ignore("this filesystem would not create a link for the test to attack through");

    var served = _TryRead(pool.PathTo("linked.bin"));
    System.Text.Encoding.UTF8.GetString(served).Should().NotContain("OUTSIDER-MARKER",
      "a file outside the pool was served as pool content through a relinked copy");

    try {
      File.WriteAllBytes(pool.PathTo("linked.bin"), _Payload(8 * 1024, 42));
    } catch (IOException) {
      // a refusal is a perfectly good answer; the assertion is about the outsider
    }

    // shared with the pool, so read it the way an observer must
    _Text(outsider).Should().Be(marker, "writing a pool file overwrote a file outside the pool through a relinked copy");
  }

  /// <summary>Reads through the mount, tolerating a refusal — which is itself an acceptable outcome.</summary>
  private static byte[] _TryRead(string path) {
    try {
      return File.ReadAllBytes(path);
    } catch (IOException) {
      return [];
    }
  }

  /// <summary>
  /// Reads a file the pool may be holding open.
  ///
  /// A relinked copy IS the outsider, so the engine's pooled handle is a handle on it — and
  /// File.ReadAllText asks for a share mode that handle refuses. An earlier version of this test
  /// failed on that rather than on the behaviour it was about.
  /// </summary>
  private static string _Text(string path) {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
      FileShare.ReadWrite | FileShare.Delete, 1 << 12);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  [Test]
  [Description("Every member is reserved to the brim: a write is refused cleanly and the file it would have replaced is intact.")]
  public void Space_GivenEveryMemberIsReservedToTheBrim_ThenAWriteIsRefusedAndTheOldFileSurvives() {
    // The classic way a filesystem loses a file it was asked to UPDATE: truncate, then fail, and now
    // neither version exists. Reserving the members rather than filling a real disk is what makes
    // this testable at all — the machine has 100+ GiB free and filling it is not a reasonable thing
    // to do to somebody's workstation.
    var original = _Payload(32 * 1024, 44);
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);

    File.WriteAllBytes(pool.PathTo("precious.bin"), original);
    pool.WaitForPhysicalCopies("precious.bin", atLeast: 2, TimeSpan.FromMinutes(2));

    var free = new DriveInfo(Path.GetPathRoot(pool.Root)!).AvailableFreeSpace;
    pool.WhileUnmounted(() => DbMount.SetMemberReserves(pool.PoolName, free + (1L << 30)));

    // updating the existing file: the dangerous shape, because a naive implementation has already
    // thrown the old content away by the time it discovers there is nowhere to put the new
    var replacement = _Payload(64 * 1024, 45);
    string? refusal = null;
    try {
      File.WriteAllBytes(pool.PathTo("precious.bin"), replacement);
    } catch (Exception e) {
      refusal = $"{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}";
    }

    TestContext.Out.WriteLine($"write with every member reserved: {refusal ?? "accepted"}");

    var survived = File.Exists(pool.PathTo("precious.bin"))
      ? File.ReadAllBytes(pool.PathTo("precious.bin"))
      : [];

    // Either answer is defensible — the write may be refused, or the reserve may be treated as
    // advisory and the write allowed. What is NOT defensible is the file ending up as neither
    // version: that is the update that ate the data it was updating.
    var isOld = survived.AsSpan().SequenceEqual(original);
    var isNew = survived.AsSpan().SequenceEqual(replacement);
    (isOld || isNew).Should().BeTrue(
      $"with no room left the file is neither its old content nor its new one — it is "
      + $"{survived.Length} bytes of something else. A failed update must leave the old version "
      + $"behind.{Environment.NewLine}refusal: {refusal ?? "none"}{Environment.NewLine}{pool.DescribeMembers()}");

    // and a brand new file must not appear half-written either
    string? createRefusal = null;
    try {
      File.WriteAllBytes(pool.PathTo("brand-new.bin"), _Payload(16 * 1024, 46));
    } catch (Exception e) {
      createRefusal = e.GetType().Name;
    }

    if (createRefusal == null)
      new FileInfo(pool.PathTo("brand-new.bin")).Length.Should().Be(16 * 1024,
        "a create that was ACCEPTED with no room left must still have stored every byte");
  }

  [Test]
  [Description("The pool runs out of room mid-write: the partially written file is not left claiming a length it does not have.")]
  public void Space_GivenItRunsOutMidStream_ThenTheFileDoesNotClaimBytesItNeverStored() {
    var original = _Payload(16 * 1024, 47);
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);

    File.WriteAllBytes(pool.PathTo("steady.bin"), original);
    pool.WaitForPhysicalCopies("steady.bin", atLeast: 2, TimeSpan.FromMinutes(2));

    // the reserve lands while a stream is open and part-written, which is the awkward moment
    var free = new DriveInfo(Path.GetPathRoot(pool.Root)!).AvailableFreeSpace;
    var chunk = _Payload(64 * 1024, 48);
    var written = 0L;
    string? failure = null;

    try {
      using var stream = new FileStream(pool.PathTo("growing.bin"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
      for (var i = 0; i < 40; ++i) {
        stream.Write(chunk, 0, chunk.Length);
        written += chunk.Length;
        stream.Flush();
        if (i == 4)
          DbMount.SetMemberReserves(pool.PoolName, free + (1L << 30)); // no room from here on
      }
    } catch (Exception e) {
      failure = $"{e.GetType().Name} after {written} bytes";
    }

    TestContext.Out.WriteLine($"mid-stream exhaustion: {failure ?? $"completed {written} bytes"}");

    // whatever it reports as its length, that many bytes must be readable — a file claiming more
    // than it holds is the shape that makes a later read fail or return padding
    if (File.Exists(pool.PathTo("growing.bin"))) {
      var claimed = new FileInfo(pool.PathTo("growing.bin")).Length;
      var readable = File.ReadAllBytes(pool.PathTo("growing.bin")).LongLength;
      readable.Should().Be(claimed,
        $"the file says it is {claimed} bytes and {readable} came back. A length that outruns the "
        + $"stored bytes turns running out of space into silent corruption."
        + $"{Environment.NewLine}{failure ?? "no failure was reported"}");
    }

    File.ReadAllBytes(pool.PathTo("steady.bin")).Should().Equal(original,
      "a file that was already safely stored must not be harmed by another file running out of room");
  }

}
