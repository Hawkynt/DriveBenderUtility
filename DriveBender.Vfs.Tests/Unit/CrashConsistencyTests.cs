using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Crash consistency as a MATRIX (SAFE-WAL, SAFE-ORDER, SAFE-NOLOSS, SAFE-OFFLINE). The engine's
/// durability argument is an ordering one — log the intent, mutate, complete the intent — and an
/// ordering argument is only as good as the worst moment to be interrupted. Individual recovery
/// tests pick a moment and prove that one; these interrupt each operation at EVERY step it takes
/// and assert the same invariants hold at all of them.
///
/// The crash is modelled the way power loss actually behaves: the operation is aborted part-way,
/// unflushed content reverts to its last durable state, never-flushed files vanish, and the pool
/// is then re-mounted so journal replay and reconciliation run exactly as they would on restart.
///
/// The invariants, at every step:
/// <list type="bullet">
///   <item>an ACKNOWLEDGED write is never lost — the file always reads back as a whole version,
///   either the acknowledged old one or the new one, never a mixture and never empty;</item>
///   <item>a completed delete never resurrects, and an interrupted one leaves the file either
///   wholly present or wholly gone;</item>
///   <item>an interrupted rename leaves the file under exactly one name, never both and never
///   neither.</item>
/// </list>
/// </summary>
[TestFixture]
[Category("Unit")]
public class CrashConsistencyTests {

  private const string _CONFIG = """
    { "duplication": 2, "write": { "policy": "write-through" }, "readAhead": { "enabled": false } }
    """;

  private static readonly Guid _pool = Guid.Parse("c8a50000-0000-0000-0000-0000000000c8");

  /// <summary>Thrown by the injected abort — power loss is not a graceful error path.</summary>
  private sealed class PowerLoss : Exception;

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 24);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 24);
  }

  /// <summary>A pool over the SAME two volumes — a fresh engine on unchanged storage, i.e. a restart.</summary>
  private PoolFileSystem _NewEngine()
    => new(_pool, [new(this._v1), new(this._v2)],
      new("crash" + Guid.NewGuid().ToString("N"), new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, _CONFIG));

  private static byte[] _Version(int version, int length = 1024) {
    var content = new byte[length];
    for (var i = 0; i < length; ++i)
      content[i] = (byte)((version * 7 + i) & 0xFF);

    return content;
  }

  private int _abortFired;

  /// <summary>Aborts the Nth volume operation from now on, modelling the machine dying mid-way.</summary>
  private void _AbortAfter(int operations) {
    var remaining = operations;
    this._abortFired = 0;
    void Hook(VolumeOp op, string path) {
      if (Interlocked.Decrement(ref remaining) != 0)
        return;

      Interlocked.Exchange(ref this._abortFired, 1);
      throw new PowerLoss();
    }

    this._v1.BeforeOperation = Hook;
    this._v2.BeforeOperation = Hook;
  }

  /// <summary>
  /// Guards against a vacuous matrix: past the last step an operation actually takes, the abort
  /// never fires, the operation simply succeeds, and the case would "pass" having tested nothing.
  /// Each range below is therefore held to be inside its operation's real length.
  /// </summary>
  private void _AssertTheCrashActuallyHappened(int abortAfter)
    => (Volatile.Read(ref this._abortFired) == 1).Should().BeTrue(
      $"step {abortAfter} is past the end of this operation — the case tested nothing, so the range must be tightened");

  private void _ClearHooks() {
    this._v1.BeforeOperation = null;
    this._v2.BeforeOperation = null;
  }

  /// <summary>Power loss: unflushed content reverts, never-flushed files vanish, the engine restarts.</summary>
  private PoolFileSystem _RecoverAfterPowerLoss() {
    this._ClearHooks();
    this._v1.SimulateCrash();
    this._v2.SimulateCrash();

    var recovered = _NewEngine();
    recovered.Mount(new(@"X:\"));
    return recovered;
  }

  private static byte[]? _ReadWhole(PoolFileSystem fs, string path) {
    NodeHandle handle;
    try {
      handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    } catch (PoolFsException) {
      return null; // the file is not there — a legal outcome for some crash points
    }

    try {
      var length = fs.GetAttributes(path).Length;
      var buffer = new byte[length];
      var read = 0;
      while (read < length) {
        var got = fs.Read(handle, buffer.AsSpan(read), read);
        if (got <= 0)
          break;

        read += got;
      }

      return buffer.AsSpan(0, read).ToArray();
    } finally {
      fs.Close(handle);
    }
  }

  /// <summary>Every physical copy of a path across both members, primary and shadow.</summary>
  private List<(string where, byte[] content)> _Copies(string path) {
    var found = new List<(string, byte[])>();
    foreach (var (volume, name) in new[] { (this._v1, "v1"), (this._v2, "v2") })
    foreach (var shadow in new[] { false, true })
      if (volume.GetContent(path, shadow) is { } content)
        found.Add(($"{name}{(shadow ? "/shadow" : "/primary")}", content));

    return found;
  }

  /// <summary>True when the bytes are EXACTLY one of the two whole versions — not a mixture of both.</summary>
  private static bool _IsWholeVersion(byte[] actual, byte[] before, byte[] after)
    => actual.AsSpan().SequenceEqual(before) || actual.AsSpan().SequenceEqual(after);

  [Test]
  [Category("EdgeCase")]
  public void Crash_GivenAWriteInterruptedAtEveryStep_ThenTheAcknowledgedVersionSurvivesWhole(
    [Range(1, 10)] int abortAfter) {
    var acknowledged = _Version(1);
    var replacement = _Version(2);

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      var create = fs.Create("crash.bin", NodeKind.File, CreateFlags.None);
      fs.Write(create, acknowledged, 0, WriteMode.Normal);
      fs.Close(create);
      fs.Unmount(); // version 1 is durable and acknowledged before anything is interrupted
    }

    var interrupted = false;
    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      this._AbortAfter(abortAfter);
      try {
        var handle = fs.Open("crash.bin", AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
        fs.Write(handle, replacement, 0, WriteMode.Normal);
        fs.Close(handle);
      } catch (Exception) {
        interrupted = true; // the machine died part-way through — exactly the point
      } finally {
        // disarm before the using block unmounts: an abort left armed would fire during the
        // clean shutdown instead, which tests the harness rather than the engine
        this._ClearHooks();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss();
    var content = _ReadWhole(recovered, "crash.bin");

    content.Should().NotBeNull($"an acknowledged file must survive a crash at step {abortAfter}");
    _IsWholeVersion(content!, acknowledged, replacement).Should().BeTrue(
      $"a crash at step {abortAfter} left a file that is neither the acknowledged version nor the new one — it is torn or truncated"
      + (interrupted ? "" : " (the write actually completed here)"));

    foreach (var (where, copy) in this._Copies("crash.bin"))
      _IsWholeVersion(copy, acknowledged, replacement).Should().BeTrue(
        $"copy {where} holds neither whole version after a crash at step {abortAfter}");
  }

  [Test]
  [Category("EdgeCase")]
  public void Crash_GivenADeleteInterruptedAtEveryStep_ThenTheFileIsWhollyGoneOrWhollyIntact(
    [Range(1, 10)] int abortAfter) {
    var content = _Version(3);

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      var create = fs.Create("doomed.bin", NodeKind.File, CreateFlags.None);
      fs.Write(create, content, 0, WriteMode.Normal);
      fs.Close(create);
      fs.Unmount();
    }

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      this._AbortAfter(abortAfter);
      try {
        fs.Unlink("doomed.bin");
      } catch (Exception) {
        // interrupted mid-delete
      } finally {
        this._ClearHooks();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss();
    var survivor = _ReadWhole(recovered, "doomed.bin");

    if (survivor != null)
      survivor.Should().Equal(content, $"a crash at step {abortAfter} left a partially deleted file readable as damaged content");

    // whatever the outcome, a SECOND restart must not change it — recovery has to be idempotent,
    // or a file could flicker in and out of existence across reboots
    recovered.Unmount();
    using var again = _NewEngine();
    again.Mount(new(@"X:\"));
    var second = _ReadWhole(again, "doomed.bin");
    (second == null).Should().Be(survivor == null, $"recovery is not idempotent at step {abortAfter} — the file's existence changed on the next restart");
  }

  [Test]
  [Category("EdgeCase")]
  public void Crash_GivenARenameInterruptedAtEveryStep_ThenTheFileLivesUnderExactlyOneName(
    [Range(1, 11)] int abortAfter) {
    var content = _Version(4);

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      var create = fs.Create("before.bin", NodeKind.File, CreateFlags.None);
      fs.Write(create, content, 0, WriteMode.Normal);
      fs.Close(create);
      fs.Unmount();
    }

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      this._AbortAfter(abortAfter);
      try {
        fs.Rename("before.bin", "after.bin", RenameFlags.None);
      } catch (Exception) {
        // interrupted mid-rename
      } finally {
        this._ClearHooks();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss();
    var under = new[] { "before.bin", "after.bin" }
      .Select(name => (name, content: _ReadWhole(recovered, name)))
      .Where(x => x.content != null)
      .ToArray();

    under.Should().NotBeEmpty($"a crash at step {abortAfter} lost the file entirely — a rename must never destroy data");
    foreach (var (name, found) in under)
      found.Should().Equal(content, $"'{name}' is damaged after a crash at step {abortAfter}");
  }

  [Test]
  [Category("EdgeCase")]
  // 11, not 21: a new staged file is no longer journaled at all (its temp is invisible and the
  // orphan sweep removes it after any crash), and its duplicate is created as a plain empty temp
  // rather than through a full publish with a barrier and a rename. Before that it was 21 — a write
  // into a not-yet-published temp had already stopped journaling an intent and a completion of its
  // own. The guard below caught the stale range both times the path got shorter, which is exactly
  // what it is for.
  public void Crash_GivenACreateInterruptedAtEveryStep_ThenNoHalfWrittenFileIsEverVisible(
    [Range(1, 11)] int abortAfter) {
    var content = _Version(5);

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      this._AbortAfter(abortAfter);
      try {
        var create = fs.Create("staged.bin", NodeKind.File, CreateFlags.None);
        fs.Write(create, content, 0, WriteMode.Normal);
        fs.Close(create); // publication (temp → final) is the LAST step
      } catch (Exception) {
        // interrupted before the file was ever complete
      } finally {
        this._ClearHooks();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss();
    var found = _ReadWhole(recovered, "staged.bin");

    // FR-STAGED-WRITE: a file between Create and its last Close lives under a temp name, so an
    // interrupted create leaves either nothing or the whole file — never a visible partial one
    if (found is { Length: > 0 })
      found.Should().Equal(content, $"a crash at step {abortAfter} published a partially written file");

    recovered.ReadDirectory("").Should().NotContain(e => e.Name.Contains("TEMP.$DRIVEBENDER", StringComparison.OrdinalIgnoreCase),
      "recovery must sweep the staging temps rather than leave them in the namespace");
  }


  [Test]
  [Category("EdgeCase")]
  // The one publish that REPLACES a visible file. A new staged file is not journaled, on the ground
  // that nothing exists at its final name for an interrupted publish to disagree with — so the case
  // where something does has to be proved separately. It is reachable: rename another file onto the
  // name while this one is still being written, and the publish on close then renames over it copy
  // by copy. Interrupted between two of those renames, one copy holds the new file and another the
  // old, and after recovery they must agree on ONE whole version.
  public void Crash_GivenAStagedFileIsPublishedOverAFileRenamedOntoItsName_ThenEveryCopyAgreesAfterRecovery(
    [Range(1, 8)] int abortAfter) {
    var renamed = _Version(31);
    var staged = _Version(32);

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      var other = fs.Create("other.bin", NodeKind.File, CreateFlags.None);
      fs.Write(other, renamed, 0, WriteMode.Normal);
      fs.Close(other);

      var late = fs.Create("race.bin", NodeKind.File, CreateFlags.None);
      fs.Write(late, staged, 0, WriteMode.Normal);
      fs.Rename("other.bin", "race.bin", RenameFlags.ReplaceExisting); // the name is taken meanwhile

      this._AbortAfter(abortAfter);
      try {
        fs.Close(late); // the publish now replaces a visible file
      } catch (Exception) {
        // interrupted mid-publish
      } finally {
        this._ClearHooks();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss();

    var found = _ReadWhole(recovered, "race.bin");
    found.Should().NotBeNull($"a crash at step {abortAfter} lost a file that existed before the publish began");
    (found.AsSpan().SequenceEqual(renamed) || found.AsSpan().SequenceEqual(staged)).Should().BeTrue(
      $"after a crash at step {abortAfter} the file must be one whole version, not a mixture");

    var copies = new[] { this._v1, this._v2 }
      .SelectMany(v => new[] { false, true }.Select(shadow => v.GetContent("race.bin", shadow)))
      .Where(c => c != null)
      .ToArray();
    copies.Should().HaveCountGreaterThan(0);
    copies.All(c => c!.AsSpan().SequenceEqual(copies[0]!)).Should().BeTrue(
      $"after a crash at step {abortAfter} the copies disagree — reads would return different content "
      + "depending on which disk answered, and the scrub would have to guess which one to keep");
  }


  [Test]
  [Category("HappyPath")]
  // The invariant the step matrix cannot reach on its own: every case above interrupts an operation
  // part-way, so none of them ever loses power AFTER the application was told the file was saved.
  // That is the moment that matters most, and the one a missing flush-before-rename would betray.
  public void Crash_GivenAFileWasWrittenAndClosed_ThenItSurvivesPowerLossWhole(
    [Values(1, 3)] int writes,
    [Values("write-through", "write-back", "performance")] string policy) {
    // write-back and performance acknowledge a write before every copy has it and owe the rest —
    // exactly the blocks that reach a staging temp with no barrier of their own, and that only the
    // publish makes durable. The policy is the variable that matters here, not a detail.
    var content = _Version(41, 3000);

    using (var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)],
             new("crash" + Guid.NewGuid().ToString("N"), new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" }),
             ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 2, "write": { "policy": "{{policy}}" }, "readAhead": { "enabled": false } }"""))) {
      fs.Mount(new(@"X:\"));
      var handle = fs.Create("saved.bin", NodeKind.File, CreateFlags.None);
      var chunk = content.Length / writes;
      for (var i = 0; i < writes; ++i) {
        var from = i * chunk;
        var to = i == writes - 1 ? content.Length : from + chunk;
        fs.Write(handle, content.AsSpan(from, to - from), from, WriteMode.Normal);
      }

      fs.Close(handle); // the application now believes the file is saved
    }

    using var recovered = this._RecoverAfterPowerLoss();
    _ReadWhole(recovered, "saved.bin").Should().Equal(content,
      $"under {policy}, a file whose close returned must survive a power cut whole — that is what 'saved' means");

    // and not just from one disk: every copy that exists holds the whole file
    foreach (var volume in new[] { this._v1, this._v2 })
    foreach (var shadow in new[] { false, true })
      if (volume.GetContent("saved.bin", shadow) is { } copy)
        copy.Should().Equal(content, $"under {policy}, the copy on '{volume.DisplayName}' (shadow: {shadow}) lost bytes in the power cut");
  }

}
