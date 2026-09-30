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
  // A rename onto the name of a file that is still being written. That file has no copy under its
  // name yet, only a temp, and its publish at the last close used to rename the temp over whatever
  // was renamed in — the renamed file's bytes were then gone, after the rename had been acknowledged.
  // The rename now replaces the file being written, as a delete of it does, so the close publishes
  // nothing. Interrupted at every step of the rename and the close, the renamed content must survive
  // whole under exactly one of its two names, with every copy agreeing.
  public void Crash_GivenAFileIsRenamedOntoAFileStillBeingWritten_ThenTheRenamedContentSurvivesWhole(
    [Range(1, 15)] int abortAfter) {
    var renamed = _Version(31);
    var staged = _Version(32);

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      var other = fs.Create("other.bin", NodeKind.File, CreateFlags.None);
      fs.Write(other, renamed, 0, WriteMode.Normal);
      fs.Close(other);

      var late = fs.Create("race.bin", NodeKind.File, CreateFlags.None);
      fs.Write(late, staged, 0, WriteMode.Normal);

      this._AbortAfter(abortAfter);
      try {
        fs.Rename("other.bin", "race.bin", RenameFlags.ReplaceExisting); // the name is taken meanwhile
        fs.Close(late);
      } catch (Exception) {
        // interrupted
      } finally {
        this._ClearHooks();
      }
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss();

    var atNew = _ReadWhole(recovered, "race.bin");
    var atOld = _ReadWhole(recovered, "other.bin");
    var holders = new[] { atNew, atOld }.Count(c => c != null && c.AsSpan().SequenceEqual(renamed));
    holders.Should().Be(1,
      $"after a crash at step {abortAfter} the renamed content must be whole under exactly one name "
      + $"(race.bin: {atNew?.Length.ToString() ?? "absent"} bytes, other.bin: {atOld?.Length.ToString() ?? "absent"} bytes)");
    if (atNew != null)
      (atNew.AsSpan().SequenceEqual(renamed) || (atOld != null && atNew.AsSpan().SequenceEqual(staged))).Should().BeTrue(
        $"after a crash at step {abortAfter}, race.bin is the renamed file, or the one being written while the rename had not happened");

    foreach (var name in new[] { "race.bin", "other.bin" }) {
      var copies = new[] { this._v1, this._v2 }
        .SelectMany(v => new[] { false, true }.Select(shadow => v.GetContent(name, shadow)))
        .Where(c => c != null)
        .ToArray();
      copies.All(c => c!.AsSpan().SequenceEqual(copies[0]!)).Should().BeTrue(
        $"after a crash at step {abortAfter} the copies of '{name}' disagree — reads would depend on which disk answered");
    }
  }


  [Test]
  [Category("HappyPath")]
  // The invariant the step matrix cannot reach on its own: every case above interrupts an operation
  // part-way, so none of them ever loses power AFTER the application was told the file was saved.
  // That is the moment that matters most, and the one a missing flush-before-rename would betray.
  public void Crash_GivenAFileWasWrittenAndClosed_ThenItSurvivesPowerLossWhole(
    [Values(1, 3)] int writes,
    [Values("write-through", "write-back", "performance")] string policy,
    [Values(false, true)] bool presized) {
    // presized: Windows' copy engine sets a file's length BEFORE filling it, so every copied file
    // is truncated while it is still a staging temp — a path with no journal intent of its own.
    // write-back and performance acknowledge a write before every copy has it and owe the rest —
    // exactly the blocks that reach a staging temp with no barrier of their own, and that only the
    // publish makes durable. The policy is the variable that matters here, not a detail.
    var content = _Version(41, 3000);

    using (var fs = new PoolFileSystem(_pool, [new(this._v1), new(this._v2)],
             new("crash" + Guid.NewGuid().ToString("N"), new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" }),
             ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 2, "write": { "policy": "{{policy}}" }, "readAhead": { "enabled": false } }"""))) {
      fs.Mount(new(@"X:\"));
      var handle = fs.Create("saved.bin", NodeKind.File, CreateFlags.None);
      if (presized)
        fs.SetLength(handle, content.Length);

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


  // ---------------------------------------------------------------------------------------------
  // "Saved means saved" for every way an application saves — not just a brand-new file. Each case
  // COMPLETES the operation (the application has been told it succeeded), then loses power, then
  // requires the outcome to be there whole on every copy. The step matrix above cannot see these:
  // it only ever interrupts an operation part-way.
  // ---------------------------------------------------------------------------------------------

  public enum SaveShape { EditInPlace, OverwriteTruncating, TempThenRenameOver, Append }

  private PoolFileSystem _EngineWith(string policy)
    => new(_pool, [new(this._v1), new(this._v2)],
      new("crash" + Guid.NewGuid().ToString("N"), new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": 2, "write": { "policy": "{{policy}}" }, "readAhead": { "enabled": false } }"""));

  private static void _WriteNew(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    fs.Write(handle, content, 0, WriteMode.Normal);
    fs.Close(handle);
  }

  private void _AssertEveryCopyHolds(string path, byte[] expected, string because) {
    var any = false;
    foreach (var volume in new[] { this._v1, this._v2 })
    foreach (var shadow in new[] { false, true })
      if (volume.GetContent(path, shadow) is { } copy) {
        any = true;
        copy.Should().Equal(expected, $"{because} — the copy on '{volume.DisplayName}' (shadow: {shadow}) does not hold it");
      }

    any.Should().BeTrue($"{because} — no copy of '{path}' exists at all");
  }

  [Test]
  [Category("HappyPath")]
  public void Crash_GivenAnExistingFileWasSavedAndClosed_ThenTheSavedVersionSurvivesPowerLossWhole(
    [Values] SaveShape shape,
    [Values("write-through", "write-back", "performance")] string policy) {
    var original = _Version(51, 2048);
    var saved = shape == SaveShape.Append ? [.. original, .. _Version(52, 700)] : _Version(52, 2048);

    using (var fs = this._EngineWith(policy)) {
      fs.Mount(new(@"X:\"));
      _WriteNew(fs, "doc.bin", original);

      switch (shape) {
        case SaveShape.EditInPlace: {
          var handle = fs.Open("doc.bin", AccessMode.ReadWrite, ShareMode.Read);
          fs.Write(handle, saved, 0, WriteMode.Normal);
          fs.Close(handle);
          break;
        }
        case SaveShape.OverwriteTruncating: {
          var handle = fs.Create("doc.bin", NodeKind.File, CreateFlags.Truncate);
          fs.Write(handle, saved, 0, WriteMode.Normal);
          fs.Close(handle);
          break;
        }
        case SaveShape.TempThenRenameOver:
          _WriteNew(fs, "doc.bin.tmp", saved);
          fs.Rename("doc.bin.tmp", "doc.bin", RenameFlags.ReplaceExisting);
          break;
        case SaveShape.Append: {
          var handle = fs.Open("doc.bin", AccessMode.ReadWrite, ShareMode.Read);
          fs.Write(handle, saved.AsSpan(original.Length), 0, WriteMode.Append);
          fs.Close(handle);
          break;
        }
      }
    }

    using var recovered = this._RecoverAfterPowerLoss();
    _ReadWhole(recovered, "doc.bin").Should().Equal(saved, $"{shape} under {policy}: the save completed, then the power went");
    this._AssertEveryCopyHolds("doc.bin", saved, $"{shape} under {policy}");
    if (shape == SaveShape.TempThenRenameOver)
      _ReadWhole(recovered, "doc.bin.tmp").Should().BeNull("the temp name was renamed away and must not come back");
  }

  [Test]
  [Category("HappyPath")]
  public void Crash_GivenNamespaceChangesCompleted_ThenTheyAllSurvivePowerLoss(
    [Values("write-through", "write-back", "performance")] string policy) {
    var kept = _Version(61, 1500);
    var moved = _Version(62, 1500);

    using (var fs = this._EngineWith(policy)) {
      fs.Mount(new(@"X:\"));
      fs.MakeDir("folder");
      _WriteNew(fs, "folder/kept.bin", kept);
      _WriteNew(fs, "old-name.bin", moved);
      _WriteNew(fs, "doomed.bin", _Version(63, 900));

      fs.Rename("old-name.bin", "folder/new-name.bin", RenameFlags.None);
      fs.Unlink("doomed.bin");
    }

    using var recovered = this._RecoverAfterPowerLoss();
    recovered.ReadDirectory("folder").Select(e => e.Name).Should().Contain(["kept.bin", "new-name.bin"], $"under {policy}");
    _ReadWhole(recovered, "folder/kept.bin").Should().Equal(kept);
    _ReadWhole(recovered, "folder/new-name.bin").Should().Equal(moved, $"a completed rename must survive under {policy}");
    _ReadWhole(recovered, "old-name.bin").Should().BeNull("the file must live under exactly one name");
    _ReadWhole(recovered, "doomed.bin").Should().BeNull($"a completed delete must not resurrect under {policy}");
    this._AssertEveryCopyHolds("folder/new-name.bin", moved, $"rename under {policy}");
  }


  [Test]
  [Category("Exception")]
  // SAFE-NOLOSS read literally: a write that RETURNED was acknowledged, and "write-back after
  // minCopiesBeforeAck" durable copies is part of the acknowledgement. The existing matrix only
  // asks that a file end up as SOME whole version, and only under write-through — so a lower ack
  // level, where the acknowledged bytes sit on one copy (chosen by load, not always the primary)
  // while the other is owed, was never tested against a power cut at all.
  public void Crash_GivenAWriteWasAcknowledgedButTheFileWasNeverClosed_ThenTheAcknowledgedBytesSurviveOnEveryCopy(
    [Values("write-through", "write-back", "deferred", "performance")] string policy,
    [Values(1, 2)] int minCopiesBeforeAck,
    [Values(0, 512)] int offset) {
    // offset 512 starts in the SECOND block (this engine's blocks are 512 bytes), and the ack copy
    // rotates with the block — so with one required copy the acknowledgement lands on the other
    // copy than at offset 0. Without it, every case could quietly be acking on the primary.
    var original = _Version(71, 4096);
    var patch = _Version(72, 512);
    var acknowledged = (byte[])original.Clone();
    patch.CopyTo(acknowledged, offset);
    var config = $$"""{ "duplication": 2, "write": { "policy": "{{policy}}", "minCopiesBeforeAck": {{minCopiesBeforeAck}} }, "readAhead": { "enabled": false } }""";

    PoolFileSystem Engine() => new(_pool, [new(this._v1), new(this._v2)],
      new("crash" + Guid.NewGuid().ToString("N"), new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, config));

    using (var fs = Engine()) {
      fs.Mount(new(@"X:\"));
      _WriteNew(fs, "acked.bin", original);
      fs.Unmount();
    }

    // Not disposed: disposing unmounts, and an unmount flushes everything owed, which is not what a
    // power cut does. The engine is simply abandoned mid-flight.
    var abandoned = Engine();
    abandoned.Mount(new(@"X:\"));
    var handle = abandoned.Open("acked.bin", AccessMode.ReadWrite, ShareMode.Read);
    abandoned.Write(handle, patch, offset, WriteMode.Normal); // returned: acknowledged

    using var recovered = this._RecoverAfterPowerLoss();
    _ReadWhole(recovered, "acked.bin").Should().Equal(acknowledged,
      $"{policy} with minCopiesBeforeAck {minCopiesBeforeAck} at offset {offset}: the write returned, so it was acknowledged, and a power cut must not take it back");
    this._AssertEveryCopyHolds("acked.bin", acknowledged,
      $"{policy} / {minCopiesBeforeAck}: after recovery every copy must agree on the acknowledged content");
  }


  [Test]
  [Category("Exception")]
  // With fewer required copies than there are copies, the copy that takes the acknowledgement
  // ROTATES by block. Two acknowledged writes to different blocks can therefore land on DIFFERENT
  // copies, each still owing the other — so after a power cut no single copy holds both. Recovery
  // that picks one copy as the truth and overwrites the other with it discards an acknowledged
  // write. One write at a time can never show this; it takes two.
  public void Crash_GivenTwoAcknowledgedWritesLandedOnDifferentCopies_ThenBothSurvive(
    [Values("write-back", "deferred", "performance")] string policy) {
    var original = _Version(81, 4096);
    var first = _Version(82, 512);
    var second = _Version(83, 512);
    var expected = (byte[])original.Clone();
    first.CopyTo(expected, 0);     // block 0
    second.CopyTo(expected, 512);  // block 1 — the other copy takes this acknowledgement
    var config = $$"""{ "duplication": 2, "write": { "policy": "{{policy}}", "minCopiesBeforeAck": 1 }, "readAhead": { "enabled": false } }""";

    PoolFileSystem Engine() => new(_pool, [new(this._v1), new(this._v2)],
      new("crash" + Guid.NewGuid().ToString("N"), new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, config));

    using (var fs = Engine()) {
      fs.Mount(new(@"X:\"));
      _WriteNew(fs, "split.bin", original);
      fs.Unmount();
    }

    var abandoned = Engine();
    abandoned.Mount(new(@"X:\"));
    var handle = abandoned.Open("split.bin", AccessMode.ReadWrite, ShareMode.Read);
    abandoned.Write(handle, first, 0, WriteMode.Normal);    // acknowledged
    abandoned.Write(handle, second, 512, WriteMode.Normal); // acknowledged

    using var recovered = this._RecoverAfterPowerLoss();
    _ReadWhole(recovered, "split.bin").Should().Equal(expected,
      $"{policy} with one required copy: both writes returned, so both were acknowledged — a power cut must keep both");
    this._AssertEveryCopyHolds("split.bin", expected, $"{policy}: every copy must end up with both acknowledged writes");
  }


  [Test]
  [Category("EdgeCase")]
  // The ordering trap in replaying writes by range. Three acknowledged writes: one on copy X at
  // block 0, one on copy Y at block 1, then a wider one on X over BOTH blocks. After a power cut X
  // holds the newest bytes everywhere and Y holds the older middle write. Replaying one range at a
  // time reads a source that an earlier step of the same replay already overwrote, and puts the
  // older middle write back on top of the newest — so every range must be read before any is written.
  public void Crash_GivenOverlappingAcknowledgedWritesOnDifferentCopies_ThenTheNewestBytesWinEverywhere(
    [Values("write-back", "performance")] string policy) {
    var original = _Version(91, 2048);
    var first = _Version(92, 512);   // block 0 → copy X
    var middle = _Version(93, 512);  // block 1 → copy Y
    var widest = _Version(94, 1024); // block 0 again → copy X, covering both earlier writes
    var expected = (byte[])original.Clone();
    widest.CopyTo(expected, 0);
    var config = $$"""{ "duplication": 2, "write": { "policy": "{{policy}}", "minCopiesBeforeAck": 1 }, "readAhead": { "enabled": false } }""";

    PoolFileSystem Engine() => new(_pool, [new(this._v1), new(this._v2)],
      new("crash" + Guid.NewGuid().ToString("N"), new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, config));

    using (var fs = Engine()) {
      fs.Mount(new(@"X:\"));
      _WriteNew(fs, "overlap.bin", original);
      fs.Unmount();
    }

    var abandoned = Engine();
    abandoned.Mount(new(@"X:\"));
    var handle = abandoned.Open("overlap.bin", AccessMode.ReadWrite, ShareMode.Read);
    abandoned.Write(handle, first, 0, WriteMode.Normal);
    abandoned.Write(handle, middle, 512, WriteMode.Normal);
    abandoned.Write(handle, widest, 0, WriteMode.Normal);

    using var recovered = this._RecoverAfterPowerLoss();
    _ReadWhole(recovered, "overlap.bin").Should().Equal(expected, $"{policy}: the newest acknowledged bytes must win everywhere");
    this._AssertEveryCopyHolds("overlap.bin", expected, $"{policy}: every copy must end on the newest bytes");
  }


  [Test]
  [Category("EdgeCase")]
  // An edit session: one intent covers every write to a file until it is next flushed or closed.
  // This interrupts the THIRD write of a session at every step it takes: the two acknowledged writes
  // before it must survive, the interrupted one must be whole or absent, and every copy must agree.
  // 2 steps: inside an open session a write is just its two copies — the journal is not touched.
  public void Crash_GivenAWriteInterruptedMidSession_ThenEarlierAcknowledgedWritesSurviveAndCopiesAgree(
    [Range(1, 2)] int abortAfter) {
    var original = _Version(101, 3072);
    var first = _Version(102, 1024);
    var second = _Version(103, 1024);
    var third = _Version(104, 1024);

    using (var fs = _NewEngine()) {
      fs.Mount(new(@"X:\"));
      _WriteNew(fs, "session.bin", original);
      fs.Unmount();
    }

    var abandoned = _NewEngine();
    abandoned.Mount(new(@"X:\"));
    var handle = abandoned.Open("session.bin", AccessMode.ReadWrite, ShareMode.Read);
    abandoned.Write(handle, first, 0, WriteMode.Normal);      // acknowledged
    abandoned.Write(handle, second, 1024, WriteMode.Normal);  // acknowledged
    this._AbortAfter(abortAfter);
    try {
      abandoned.Write(handle, third, 2048, WriteMode.Normal); // the power goes during this one
    } catch (Exception) {
      // interrupted — exactly the point
    } finally {
      this._ClearHooks();
    }

    this._AssertTheCrashActuallyHappened(abortAfter);
    using var recovered = this._RecoverAfterPowerLoss();
    var content = _ReadWhole(recovered, "session.bin")!;
    content.AsSpan(0, 1024).SequenceEqual(first).Should().BeTrue($"step {abortAfter}: the first acknowledged write was lost");
    content.AsSpan(1024, 1024).SequenceEqual(second).Should().BeTrue($"step {abortAfter}: the second acknowledged write was lost");
    var tail = content.AsSpan(2048, 1024);
    (tail.SequenceEqual(third) || tail.SequenceEqual(original.AsSpan(2048, 1024))).Should().BeTrue(
      $"step {abortAfter}: the interrupted write must be whole or absent, never torn");
    this._AssertEveryCopyHolds("session.bin", content, $"step {abortAfter}: copies must agree after recovery");
  }

}
