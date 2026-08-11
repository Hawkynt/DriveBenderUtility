using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Metadata coherence under concurrency (SAFE-COHERE): the length a caller sees must never
/// go BACKWARDS past an acknowledged write. The engine derives a file's logical length from
/// two sources — the durable stat of one physical copy plus the write buffer's overlay of the
/// bytes still owed to the others — and those two reads must be atomic with respect to a
/// flush. They were not: <see cref="PoolFileSystem.GetAttributes"/> took no path lease, so
/// <see cref="PoolFileSystem.FlushPath"/> (which does) could drain the buffer in between,
/// leaving the caller with the pre-write stat and an empty overlay.
///
/// This is the deterministic form of the intermittent read-your-writes failure the
/// <see cref="ConcurrencyFuzzTests"/> stress suite caught roughly once in twenty-four runs.
/// </summary>
[TestFixture]
[Category("Unit")]
public class MetadataCoherenceTests {

  // duplication 2 with an ack from ONE copy is the only shape that leaves the second copy
  // OWED in the write buffer — which is what makes the overlay load-bearing for the length
  private const string _CONFIG = """
    {
      "duplication": 2,
      "write": { "policy": "write-back", "minCopiesBeforeAck": 1 },
      "readAhead": { "enabled": false }
    }
    """;

  private static readonly Guid _pool = Guid.Parse("d0da0000-0000-0000-0000-00000000000d");

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 26);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 26);
    this._fs = new(_pool, [new(this._v1), new(this._v2)],
      new("meta" + Guid.NewGuid().ToString("N"), new() { Size = "8388608", BlockSize = "512", MetadataEntries = 1000, MetadataTtl = "1m" }),
      ConfigResolver.ResolveEffective(null, _CONFIG));
    this._fs.Mount(new(@"X:\"));
  }

  [TearDown]
  public void TearDown() => this._fs.Dispose();

  /// <summary>
  /// Writes <paramref name="length"/> bytes so that the copy resolved FIRST is the one left
  /// lagging: the ack lands on the fallback copy and the leading copy's bytes stay owed in the
  /// write buffer. That is the state in which the overlay — not the disk — carries the length.
  /// </summary>
  private byte[] _WriteLeavingFirstCopyOwed(string path, int length) {
    this._fs.Close(this._fs.Create(path, NodeKind.File, CreateFlags.None));

    var content = new byte[length];
    for (var i = 0; i < length; ++i)
      content[i] = (byte)(i & 0xFF);

    // the ack quorum tries the first copy, fails, and redirects to the next ready storage —
    // exactly what happens for real when a drive stalls mid-write. The fault is scoped to THIS
    // file's data write: a plain one-shot fault would be swallowed by the journal's own mirror
    // write to the same member, which happens first.
    this._v1.BeforeOperation = (op, target) => {
      if (op == VolumeOp.OpenWrite && string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        throw new PoolFsException(PoolFsError.IoError, "injected: this storage stalled mid-write");
    };

    var handle = this._fs.Open(path, AccessMode.ReadWrite, ShareMode.Read | ShareMode.Write);
    try {
      this._fs.Write(handle, content, 0, WriteMode.Normal);
    } finally {
      this._fs.Close(handle);
      this._v1.BeforeOperation = null;
    }

    this._v1.GetContent(path, false).Should().BeEmpty("the precondition is that v1's copy is still owed");
    this._fs.WriteBuffer.IsDirty(path).Should().BeTrue("the owed bytes must live in the write buffer");
    return content;
  }

  /// <summary>
  /// Drives a flush of <paramref name="path"/> into the exact window between the durable stat
  /// and the overlay read: the hook fires with the stat's answer captured but not yet returned.
  ///
  /// The hook does NOT wait for the flush to finish. Against the unsynchronised code the flush
  /// runs straight through (there is nothing to block it) and wins the race well inside the
  /// grace period; against the fixed code it parks on the path's write lease until the stat's
  /// shared lease is released, which is precisely the property under test. Waiting for
  /// completion here would instead be the test deadlocking itself on its own correctness.
  /// </summary>
  private void _FlushConcurrentlyOnFirstStatOf(string path, out Func<bool> fired) {
    var hasFired = 0;
    this._v1.AfterOperation = (op, statted) => {
      if (op != VolumeOp.Stat || !string.Equals(statted, path, StringComparison.OrdinalIgnoreCase))
        return;
      if (Interlocked.Exchange(ref hasFired, 1) != 0)
        return;

      var flush = new Thread(() => this._fs.FlushPath(path)) { IsBackground = true, Name = "interleaved-flush" };
      flush.Start();
      flush.Join(TimeSpan.FromMilliseconds(500)); // long enough for an unguarded flush to win the race
    };

    fired = () => Volatile.Read(ref hasFired) == 1;
  }

  /// <summary>Asserts the interleaved flush eventually completed — i.e. the lease was released, not held forever.</summary>
  private void _AssertFlushCompletes(string path) {
    this._v1.AfterOperation = null;
    var drain = new Thread(() => this._fs.FlushPath(path)) { IsBackground = true };
    drain.Start();
    drain.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the path's lease must be released — a flush after the stat must not hang");
  }

  [Test]
  [Category("EdgeCase")]
  public void GetAttributes_GivenAnOwedCopyIsFlushedBetweenStatAndOverlay_ThenTheLengthNeverGoesBackwards() {
    const string path = "owed.bin";
    var content = this._WriteLeavingFirstCopyOwed(path, 2048);

    this._FlushConcurrentlyOnFirstStatOf(path, out var fired);
    var meta = this._fs.GetAttributes(path);

    fired().Should().BeTrue("the interleaving hook must actually have run — otherwise this proves nothing");
    meta.Length.Should().Be(content.Length,
      "an acknowledged write must never read back shorter because a background flush moved the bytes from the overlay onto the copies");
    this._AssertFlushCompletes(path);
  }

  [Test]
  [Category("EdgeCase")]
  public void GetAttributes_GivenAnOwedCopyIsFlushedBetweenStatAndOverlay_ThenTheStaleLengthIsNotCached() {
    const string path = "owed-cached.bin";
    var content = this._WriteLeavingFirstCopyOwed(path, 4096);

    this._FlushConcurrentlyOnFirstStatOf(path, out var fired);
    this._fs.GetAttributes(path);
    fired().Should().BeTrue();
    this._v1.AfterOperation = null;

    // the second call is served from the metadata cache: a length observed during the race
    // would persist there for the whole TTL, turning one lost interleaving into a lastingly
    // wrong file size
    this._fs.GetAttributes(path).Length.Should().Be(content.Length,
      "a length observed during the race must not be cached as the file's size");
  }

  [Test]
  [Category("EdgeCase")]
  public void ReadDirectory_GivenACopyStillOwesBytes_ThenTheEntryReportsTheAcknowledgedLength() {
    const string path = "listed.bin";
    var content = this._WriteLeavingFirstCopyOwed(path, 3072);

    // no interleaving needed: a listing takes each entry's length from whichever member
    // enumerates it first, which here is the copy that never received the write
    var entry = this._fs.ReadDirectory("").Single(e => string.Equals(e.Name, path, StringComparison.OrdinalIgnoreCase));
    entry.Length.Should().Be(content.Length, "a listing must report the acknowledged length, not a lagging copy's on-disk size");
  }

  [Test]
  [Category("EdgeCase")]
  public void ReadDirectory_GivenAnOwedCopyIsFlushedDuringTheListing_ThenTheEntryLengthIsCorrect() {
    const string path = "listed-raced.bin";
    var content = this._WriteLeavingFirstCopyOwed(path, 1536);

    this._FlushConcurrentlyOnFirstStatOf(path, out var fired);
    var entry = this._fs.ReadDirectory("").Single(e => string.Equals(e.Name, path, StringComparison.OrdinalIgnoreCase));

    fired().Should().BeTrue("the listing must re-stat the still-dirty child — otherwise the overlay is never consulted");
    entry.Length.Should().Be(content.Length, "a listing must report the acknowledged length, not a mid-flush observation");
    this._AssertFlushCompletes(path);
  }

}
