using System.Buffers;
using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Rented buffers must never leak, lose or overwrite data (SAFE-NOLOSS).
///
/// <see cref="ArrayPool{T}"/> hands back memory that is NOT zeroed and is NOT the size asked for —
/// it is whatever the previous tenant left, in an array that is at least as long as requested and
/// frequently longer. Three mistakes follow from that, and all three are silent:
///
/// <list type="bullet">
/// <item>trusting <c>buffer.Length</c> as "how much data there is", which appends the previous
/// tenant's bytes — one file's contents surfacing inside another;</item>
/// <item>trusting a single <c>Read</c> to fill the buffer, which leaves a stale tail in the middle
/// of otherwise-good data;</item>
/// <item>letting a rented array escape — into a cache, a queue, a retained stream — after it has
/// been returned, so the next tenant overwrites live data.</item>
/// </list>
///
/// These tests make the first two impossible to miss by DIRTYING the pool first: every buffer any
/// code under test rents arrives full of a poison byte. If a single poison byte reaches an output,
/// the assertion names the offset. A test against a clean pool would pass on all three mistakes,
/// because a freshly allocated array is zeroed and zeros look innocent.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Regression")]
public class RentedBufferSafetyTests {

  private const byte _POISON = 0xDB;

  /// <summary>
  /// Fills the shared pool with poisoned arrays, so the next rent of that size gets one back.
  ///
  /// Renting and returning across a spread of sizes covers the pool's size buckets — code under
  /// test asks for its own sizes, and a bucket that was never dirtied would hand out a clean array
  /// and quietly weaken the check.
  /// </summary>
  private static void _PoisonThePool() {
    var held = new List<byte[]>();
    foreach (var size in new[] { 4096, 64 * 1024, 128 * 1024, 1 << 20, 4 << 20 })
      for (var i = 0; i < 8; ++i) {
        var array = ArrayPool<byte>.Shared.Rent(size);
        Array.Fill(array, _POISON);
        held.Add(array);
      }

    foreach (var array in held)
      ArrayPool<byte>.Shared.Return(array); // returned dirty on purpose — that is the point
  }

  /// <summary>
  /// A stream that hands back only a few bytes per call, however much is asked for.
  ///
  /// Short reads are legal for every stream and are what a network member actually does. Code that
  /// assumes one Read fills the buffer works perfectly against a MemoryStream and corrupts data
  /// against a socket, so the tests read through this rather than the convenient thing.
  /// </summary>
  private sealed class DribblingStream(byte[] content, int mostPerRead) : Stream {

    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;
    public override long Position { get => this._position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      var remaining = content.Length - this._position;
      if (remaining <= 0)
        return 0;

      var take = Math.Min(Math.Min(mostPerRead, buffer.Length), remaining);
      content.AsSpan(this._position, take).CopyTo(buffer);
      this._position += take;
      return take;
    }

    public override void Flush() {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  private static byte[] _Content(int length) {
    var content = new byte[length];
    for (var i = 0; i < length; ++i)
      content[i] = (byte)(i % 251); // never 0xDB for any i, so poison is unambiguous
    return content;
  }

  /// <summary>Sizes chosen to sit awkwardly against any buffer size: never a whole multiple.</summary>
  private static IEnumerable<int> _AwkwardLengths => [0, 1, 4095, 4097, 64 * 1024 + 1, (1 << 20) + 3];

  [Test]
  [Category("EdgeCase")]
  public void Copy_GivenAPoisonedPool_ThenTheDestinationHoldsExactlyTheSourceAndNothingElse() {
    foreach (var length in _AwkwardLengths) {
      _PoisonThePool();

      var content = _Content(length);
      var destination = new MemoryStream();
      var copied = WholeFilePublisher.CopyCounted(new DribblingStream(content, 7), destination);

      copied.Should().Be(length, $"a {length}-byte source must report {length} bytes copied");
      var written = destination.ToArray();

      // the length assertion alone would catch a buffer-length bug; the poison scan catches the
      // subtler one where the right NUMBER of bytes is written but some came from the last tenant
      written.Length.Should().Be(length, $"a {length}-byte source must produce a {length}-byte destination");
      var poisoned = Enumerable.Range(0, written.Length).Where(i => written[i] != content[i]).Take(4).ToArray();
      poisoned.Should().BeEmpty(
        $"copying {length} bytes through a rented buffer must reproduce the source exactly, but offsets "
        + $"[{string.Join(", ", poisoned)}] differ"
        + $"{(poisoned.Any(i => written[i] == _POISON) ? " — and hold the POISON byte, so a previous tenant's data leaked through" : "")}");
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Hash_GivenAPoisonedPool_ThenItMatchesTheHashOfTheContentAlone() {
    foreach (var length in _AwkwardLengths) {
      var content = _Content(length);
      var expected = ChecksumDatabase.HashOf(content); // the span overload — no pooling involved

      _PoisonThePool();
      var actual = ChecksumDatabase.HashOf(new DribblingStream(content, 5));

      actual.Should().Be(expected,
        $"hashing {length} bytes through a rented buffer must hash the content and nothing else — a "
        + $"mismatch here means the hash swallowed part of the previous tenant's data, which would "
        + $"mark a perfectly good file as corrupt on the next scrub");
    }
  }

  [Test]
  [Category("Exception")]
  public void Copy_GivenTheSourceEndsMidBuffer_ThenNoTailIsInvented() {
    // The specific shape that trusting buffer.Length produces: a source far shorter than one
    // rented buffer. Everything past the real end must simply not exist.
    _PoisonThePool();

    var content = _Content(13);
    var destination = new MemoryStream();
    WholeFilePublisher.CopyCounted(new DribblingStream(content, 4), destination);

    destination.ToArray().Should().Equal(content,
      "a 13-byte file must arrive as 13 bytes, not as 13 bytes followed by whatever the buffer held");
  }

  [Test]
  [Category("Exception")]
  public void Copy_GivenItRunsRepeatedly_ThenEachRunIsUnaffectedByTheLast() {
    // Buffers are returned to the pool still holding the previous run's data, so a later copy rents
    // an array full of an EARLIER FILE's bytes. This is the real-world version of the poison: not
    // an artificial pattern, but one user file leaking into another.
    var first = _Content(64 * 1024 + 17);
    var second = new byte[999];
    Array.Fill(second, (byte)0x5A);

    var firstOut = new MemoryStream();
    WholeFilePublisher.CopyCounted(new DribblingStream(first, 8191), firstOut);

    var secondOut = new MemoryStream();
    WholeFilePublisher.CopyCounted(new DribblingStream(second, 8191), secondOut);

    secondOut.ToArray().Should().Equal(second,
      "the second copy must be exactly its own content — any byte from the first file here is one "
      + "user's data appearing inside another's");
  }

}
