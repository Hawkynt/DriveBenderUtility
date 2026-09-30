using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Power cuts during a striped write (docs/IncomingFiles.md). The write, the fill and the publish
/// are interrupted at every storage step; after the power returns and the pool remounts, the file
/// is either not there at all (it was never published) or whole on every copy under its real name
/// — never a half-filled file under the real name — and no stripe temp is left behind.
/// </summary>
[TestFixture]
[Category("Unit")]
public class StripeCrashTests {

  private static readonly Guid _pool = Guid.Parse("57a1bec0-0000-0000-0000-00000000c0de");

  /// <summary>Thrown by the injected abort — power loss is not a graceful error path.</summary>
  private sealed class PowerLoss : Exception;

  private FakeVolumeIO _d1 = null!;
  private FakeVolumeIO _d2 = null!;
  private FakeVolumeIO _d3 = null!;
  private int _abortFired;

  private FakeVolumeIO[] _All => [this._d1, this._d2, this._d3];

  [SetUp]
  public void SetUp() {
    this._d1 = new(Guid.NewGuid(), "d1", "PHYS-1", capacity: 1L << 24);
    this._d2 = new(Guid.NewGuid(), "d2", "PHYS-2", capacity: 1L << 24);
    this._d3 = new(Guid.NewGuid(), "d3", "PHYS-3", capacity: 1L << 24);
  }

  private PoolFileSystem _Engine(int duplication) {
    var cache = new CacheInstance("sc" + Guid.NewGuid().ToString("N"),
      new() { Size = "2097152", BlockSize = "512", MetadataEntries = 500, MetadataTtl = "1m" });
    var fs = new PoolFileSystem(_pool, [new(this._d1), new(this._d2), new(this._d3)], cache,
      ConfigResolver.ResolveEffective(null, $$"""{ "duplication": {{duplication}}, "readAhead": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  // eight blocks per write: enough that a write spreads over the whole group, so there is something to fill
  private static byte[] _Content() => [.. Enumerable.Range(0, 512 * 8 + 100).Select(i => (byte)(i * 11 + 3))];

  private void _AbortAfter(int operations) {
    var remaining = operations;
    this._abortFired = 0;
    // once the power is gone it stays gone: nothing after the cut reaches a disk — not the rest of
    // the write, and not the clean unmount the engine's disposal would otherwise run
    void Hook(VolumeOp op, string path) {
      if (Volatile.Read(ref this._abortFired) == 1)
        throw new PowerLoss();
      if (Interlocked.Decrement(ref remaining) != 0)
        return;

      Interlocked.Exchange(ref this._abortFired, 1);
      throw new PowerLoss();
    }

    foreach (var volume in this._All)
      volume.BeforeOperation = Hook;
  }

  private void _Disarm() {
    foreach (var volume in this._All)
      volume.BeforeOperation = null;
  }

  private static byte[]? _Read(PoolFileSystem fs, string path) {
    NodeHandle handle;
    try {
      handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    } catch (PoolFsException) {
      return null;
    }

    try {
      var buffer = new byte[fs.GetAttributes(path).Length];
      var read = 0;
      while (read < buffer.Length) {
        var got = fs.Read(handle, buffer.AsSpan(read), read);
        if (got == 0)
          break;
        read += got;
      }

      return buffer[..read];
    } finally {
      fs.Close(handle);
    }
  }

  /// <summary>Writes a new file in two calls and closes it — the whole striped lifecycle.</summary>
  private static void _WriteStriped(PoolFileSystem fs, string path, byte[] content) {
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    var half = content.Length / 2;
    fs.Write(handle, content.AsSpan(0, half), 0, WriteMode.Normal);
    fs.Write(handle, content.AsSpan(half), half, WriteMode.Normal);
    fs.Close(handle);
  }

  [Test]
  [Category("HappyPath")]
  public void Stripe_GivenNoPowerCut_ThenTheWriteReallySpreadOverTheGroup() {
    // the premise the crash cases rest on: the file's blocks did go to helpers, so filling had work
    var helperWrites = 0;
    using var fs = this._Engine(1);
    foreach (var volume in this._All)
      volume.BeforeOperation = (op, path) => {
        if (op == VolumeOp.OpenWrite && path.Contains(".STRIPE.", StringComparison.Ordinal))
          Interlocked.Increment(ref helperWrites);
      };

    var content = _Content();
    _WriteStriped(fs, "spread.bin", content);
    this._Disarm();

    helperWrites.Should().BeGreaterThan(0, "an eight-block write keeps more than one disk busy");
    _Read(fs, "spread.bin").Should().Equal(content);
    this._All.SelectMany(v => v.FilePaths).Should().NotContain(p => p.Contains(".STRIPE."), "helpers are gone once the file is published");
  }

  // measured lengths of the whole striped write + close; the guard fails the moment one gets shorter
  [TestCase(1, 30)]
  [TestCase(2, 38)]
  [Category("EdgeCase")]
  public void Stripe_GivenThePowerIsCutAtEveryStep_ThenTheFileIsWholeOrAbsentAndNoStripeTempSurvives(int duplication, int steps) {
    var content = _Content();
    for (var abortAfter = 1; abortAfter <= steps; ++abortAfter) {
      this.SetUp();
      var when = $"duplication {duplication}, power cut at step {abortAfter}";

      var fs = this._Engine(duplication);
      this._AbortAfter(abortAfter);
      try {
        _WriteStriped(fs, "striped.bin", content);
      } catch (Exception) {
        // died part-way (a power loss inside a parallel section arrives wrapped)
      }

      try {
        fs.Dispose(); // the machine is off: this reaches no disk
      } catch (Exception) {
        // nothing to shut down cleanly without power
      } finally {
        this._Disarm();
      }

      Volatile.Read(ref this._abortFired).Should().Be(1, $"{when} is past the end of the write — the measured length is stale, tighten it");

      foreach (var volume in this._All)
        volume.SimulateCrash();

      using var recovered = this._Engine(duplication);
      var back = _Read(recovered, "striped.bin");
      if (back != null)
        back.Should().Equal(content, $"{when}: a file under its real name must be whole");

      foreach (var volume in this._All)
      foreach (var shadow in new[] { false, true })
        if (volume.GetContent("striped.bin", shadow) is { } copy)
          copy.Should().Equal(content, $"{when}: the copy on '{volume.DisplayName}' (shadow: {shadow}) carries the real name, so it must be whole");

      this._All.SelectMany(v => v.FilePaths).Should().NotContain(p => p.Contains(".STRIPE."), $"{when}: recovery sweeps stripe temps");
    }
  }

  [TestCase(1)]
  [TestCase(2)]
  [Category("HappyPath")]
  public void Stripe_GivenTheFileWasClosed_WhenThePowerIsCut_ThenItSurvivesWholeOnEveryCopy(int duplication) {
    // a close under the default (safe) policy returns only once the file is whole and durable
    var content = _Content();
    using (var fs = this._Engine(duplication))
      _WriteStriped(fs, "closed.bin", content);

    foreach (var volume in this._All)
      volume.SimulateCrash();

    using var recovered = this._Engine(duplication);
    _Read(recovered, "closed.bin").Should().Equal(content, "the close promised the file");
    this._All.Sum(v => new[] { false, true }.Count(shadow => v.GetContent("closed.bin", shadow) is { } c && c.SequenceEqual(content)))
      .Should().Be(duplication, "every copy the folder asks for exists, whole");
  }


  [Test]
  [Category("HappyPath")]
  public void Stripe_GivenTheOpenFileIsStampedBeforeItIsClosed_ThenEveryCopyKeepsTheStampedTime() {
    // cp -p: write, set the source's times on the still-open file, close. Filling at close writes to
    // the copies after the stamp, which used to leave them with the time of the fill.
    var stamped = new DateTime(2019, 3, 14, 15, 9, 26, DateTimeKind.Utc);
    using var fs = this._Engine(2);
    var handle = fs.Create("kept.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, _Content(), 0, WriteMode.Normal);
    fs.SetAttributes("kept.bin", new(LastWriteTimeUtc: stamped));
    fs.Close(handle);

    fs.GetAttributes("kept.bin").LastWriteTimeUtc.Should().Be(stamped);
    foreach (var volume in this._All)
    foreach (var shadow in new[] { false, true })
      if (volume.Stat("kept.bin", shadow) is { } meta)
        meta.LastWriteTimeUtc.Should().Be(stamped, $"the copy on '{volume.DisplayName}' (shadow: {shadow}) keeps the stamped time");
  }

  [Test]
  [Category("EdgeCase")]
  public void Stripe_GivenTheFileIsWrittenAfterItWasStamped_ThenTheWriteDecidesTheTime() {
    var stamped = new DateTime(2019, 3, 14, 15, 9, 26, DateTimeKind.Utc);
    using var fs = this._Engine(1);
    var handle = fs.Create("later.bin", NodeKind.File, CreateFlags.None);
    fs.SetAttributes("later.bin", new(LastWriteTimeUtc: stamped));
    fs.Write(handle, _Content(), 0, WriteMode.Normal);
    fs.Close(handle);

    fs.GetAttributes("later.bin").LastWriteTimeUtc.Should().BeAfter(stamped.AddYears(1), "a write after the stamp is the newer truth");
  }

}
