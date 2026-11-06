using System.Buffers.Binary;
using System.IO.Hashing;

namespace DivisonM.Vfs.Integrity;

/// <summary>
/// What one copy of a file held when it was indexed: a hash per shard, and Reed-Solomon parity per
/// stripe. With it a read can be checked shard by shard as it is served, and a damaged shard can be
/// rebuilt from the copy's own other shards even when no other copy is intact.
///
/// The geometry is fixed per stripe. A stripe covers 2 MiB of the file and holds up to 32 data
/// shards. A full stripe uses 64 KiB shards; a shorter one (the file's tail, or a small file) uses
/// the smallest power-of-two shard size that still fits it in 32 shards, so parity costs about the
/// configured share of the file rather than a whole block per parity shard. Powers of two that
/// divide 64 KiB mean a shard never straddles a cache block of 64 KiB or larger.
///
/// The index describes the copy as it was at <see cref="Length"/> and <see cref="MTimeTicks"/>; a
/// copy whose size or time differs is not described by it. The engine also drops indexes before it
/// changes a file, because a rewrite at the same length within one timestamp tick would otherwise
/// leave an index that looks current.
/// </summary>
public sealed class BlockIndex {

  public const int StripeBytes = 2 * 1024 * 1024;
  public const int MaxShardBytes = 64 * 1024;
  public const int MinShardBytes = 64;
  public const int MaxDataShards = StripeBytes / MaxShardBytes; // 32

  private const uint _MAGIC = 0x31494244; // "DBI1"
  private const int _HEADER_BYTES = 4 + 8 + 8 + 4;

  public long Length { get; }
  public long MTimeTicks { get; }
  public int ParityShards { get; }

  /// <summary>Hashes of every data shard, stripe by stripe.</summary>
  private readonly ulong[] _hashes;

  /// <summary>Per stripe: its parity shards, each the stripe's shard size long.</summary>
  private readonly byte[][][] _parity;

  private BlockIndex(long length, long mtimeTicks, int parityShards, ulong[] hashes, byte[][][] parity) {
    this.Length = length;
    this.MTimeTicks = mtimeTicks;
    this.ParityShards = parityShards;
    this._hashes = hashes;
    this._parity = parity;
  }

  public int StripeCount => StripeCountFor(this.Length);

  public static int StripeCountFor(long length) => (int)((length + StripeBytes - 1) / StripeBytes);

  public static long StripeStart(int stripe) => (long)stripe * StripeBytes;

  public static int StripeLength(long fileLength, int stripe) => (int)Math.Min(StripeBytes, fileLength - StripeStart(stripe));

  /// <summary>The shard size a stripe of this many bytes uses.</summary>
  public static int ShardBytesFor(int stripeLength) {
    var wanted = Math.Max(MinShardBytes, (stripeLength + MaxDataShards - 1) / MaxDataShards);
    var size = MinShardBytes;
    while (size < wanted)
      size <<= 1;

    return Math.Min(size, MaxShardBytes);
  }

  public static int ShardCountFor(int stripeLength) {
    var shard = ShardBytesFor(stripeLength);
    return (stripeLength + shard - 1) / shard;
  }

  /// <summary>Where each stripe's hashes begin in the flat hash array.</summary>
  private static int _FirstHash(long fileLength, int stripe) {
    // every stripe but the last is full: 32 shards each
    return stripe * MaxDataShards;
  }

  public static ulong Hash(ReadOnlySpan<byte> shard) => XxHash3.HashToUInt64(shard);

  public bool Describes(long length, DateTime lastWriteUtc) => length == this.Length && lastWriteUtc.Ticks == this.MTimeTicks;

  /// <summary>Whether two indexes describe the same content, shard for shard (their times may differ).</summary>
  public bool SameContentAs(BlockIndex other) => this.Length == other.Length && this._hashes.AsSpan().SequenceEqual(other._hashes);

  /// <summary>Byte range [start, end) of one data shard within the file.</summary>
  public (long Start, int Length) ShardRange(int stripe, int shard) {
    var stripeLength = StripeLength(this.Length, stripe);
    var size = ShardBytesFor(stripeLength);
    var start = StripeStart(stripe) + (long)shard * size;
    return (start, (int)Math.Min(size, StripeStart(stripe) + stripeLength - start));
  }

  public ulong ExpectedHash(int stripe, int shard) => this._hashes[_FirstHash(this.Length, stripe) + shard];

  /// <summary>Every shard overlapping [offset, offset + count), as (stripe, shard).</summary>
  public IEnumerable<(int Stripe, int Shard)> ShardsOverlapping(long offset, long count) {
    if (count <= 0 || offset >= this.Length)
      yield break;

    var end = Math.Min(this.Length, offset + count);
    for (var stripe = (int)(offset / StripeBytes); stripe < this.StripeCount && StripeStart(stripe) < end; ++stripe) {
      var stripeLength = StripeLength(this.Length, stripe);
      var size = ShardBytesFor(stripeLength);
      var shards = ShardCountFor(stripeLength);
      var from = (int)(Math.Max(0, offset - StripeStart(stripe)) / size);
      var to = (int)Math.Min(shards - 1, (end - 1 - StripeStart(stripe)) / size);
      for (var shard = from; shard <= to; ++shard)
        yield return (stripe, shard);
    }
  }

  /// <summary>
  /// Reads the copy once, start to end, and records a hash per shard and parity per stripe.
  /// <paramref name="parityShards"/> 0 records hashes only.
  /// </summary>
  public static BlockIndex Build(Stream source, long length, long mtimeTicks, int parityShards) {
    if (parityShards is < 0 or > 16)
      throw new ArgumentOutOfRangeException(nameof(parityShards));

    var stripes = StripeCountFor(length);
    var hashes = new ulong[Math.Max(0, (stripes - 1) * MaxDataShards + (stripes == 0 ? 0 : ShardCountFor(StripeLength(length, stripes - 1))))];
    var parity = new byte[stripes][][];
    var buffer = new byte[StripeBytes];
    for (var stripe = 0; stripe < stripes; ++stripe) {
      var stripeLength = StripeLength(length, stripe);
      ReadExactly(source, buffer.AsSpan(0, stripeLength));
      var size = ShardBytesFor(stripeLength);
      var count = ShardCountFor(stripeLength);
      var first = _FirstHash(length, stripe);
      for (var shard = 0; shard < count; ++shard) {
        var start = shard * size;
        hashes[first + shard] = Hash(buffer.AsSpan(start, Math.Min(size, stripeLength - start)));
      }

      parity[stripe] = parityShards == 0 ? [] : _EncodeStripe(buffer, stripeLength, parityShards);
    }

    if (source.Read(new byte[1]) != 0)
      throw new IOException("The copy grew while it was indexed");

    return new(length, mtimeTicks, parityShards, hashes, parity);
  }

  private static byte[][] _EncodeStripe(byte[] stripeData, int stripeLength, int parityShards) {
    var size = ShardBytesFor(stripeLength);
    var count = ShardCountFor(stripeLength);
    var shards = new byte[count + parityShards][];
    for (var shard = 0; shard < count; ++shard) {
      var bytes = new byte[size]; // zero-padded at the tail
      var start = shard * size;
      stripeData.AsSpan(start, Math.Min(size, stripeLength - start)).CopyTo(bytes);
      shards[shard] = bytes;
    }

    for (var p = 0; p < parityShards; ++p)
      shards[count + p] = new byte[size];

    _Codec(count, parityShards).EncodeParity(shards, 0, size);
    return shards[count..];
  }

  private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), ReedSolomon> _codecs = new();

  private static ReedSolomon _Codec(int data, int parity) => _codecs.GetOrAdd((data, parity), static key => new(key.Item1, key.Item2));

  /// <summary>
  /// Rebuilds the damaged shards of one stripe. <paramref name="readShard"/> returns a shard's bytes
  /// as the copy holds them, or null when it cannot be read; a shard is trusted only when it
  /// matches its hash. Returns every data shard of the stripe, correct, or null when more shards
  /// are damaged than the stripe has parity for. Rebuilt shards are checked against their hashes
  /// too: a reconstruction that does not reproduce the recorded content is not returned.
  /// </summary>
  public byte[][]? RepairStripe(int stripe, Func<int, byte[]?> readShard) {
    var stripeLength = StripeLength(this.Length, stripe);
    var size = ShardBytesFor(stripeLength);
    var count = ShardCountFor(stripeLength);
    var shards = new byte[count + this.ParityShards][];
    var present = new bool[shards.Length];
    var missing = 0;
    for (var shard = 0; shard < count; ++shard) {
      var range = this.ShardRange(stripe, shard);
      var bytes = readShard(shard);
      var buffer = new byte[size];
      if (bytes != null && bytes.Length == range.Length && Hash(bytes) == this.ExpectedHash(stripe, shard)) {
        bytes.CopyTo(buffer, 0);
        present[shard] = true;
      } else
        ++missing;

      shards[shard] = buffer;
    }

    if (missing == 0)
      return [.. shards[..count].Select((s, i) => s[..this.ShardRange(stripe, i).Length])];

    if (missing > this.ParityShards)
      return null;

    for (var p = 0; p < this.ParityShards; ++p) {
      shards[count + p] = (byte[])this._parity[stripe][p].Clone();
      present[count + p] = true;
    }

    _Codec(count, this.ParityShards).DecodeMissing(shards, present, 0, size);
    var result = new byte[count][];
    for (var shard = 0; shard < count; ++shard) {
      var length = this.ShardRange(stripe, shard).Length;
      result[shard] = shards[shard][..length];
      if (Hash(result[shard]) != this.ExpectedHash(stripe, shard))
        return null; // the parity does not describe this content: never hand back a guess
    }

    return result;
  }

  // ---- persistence -------------------------------------------------------------------------------

  /// <summary>
  /// Binary form: header, hashes, parity, then a hash of all of it, so an index that rotted on disk
  /// is recognised and ignored rather than trusted.
  /// </summary>
  public byte[] Serialize() {
    var parityBytes = this._parity.Sum(stripe => stripe.Sum(p => (long)p.Length));
    var total = _HEADER_BYTES + this._hashes.Length * 8L + parityBytes + 8;
    var bytes = new byte[total];
    var span = bytes.AsSpan();
    BinaryPrimitives.WriteUInt32LittleEndian(span, _MAGIC);
    BinaryPrimitives.WriteInt64LittleEndian(span[4..], this.Length);
    BinaryPrimitives.WriteInt64LittleEndian(span[12..], this.MTimeTicks);
    BinaryPrimitives.WriteInt32LittleEndian(span[20..], this.ParityShards);
    var at = _HEADER_BYTES;
    foreach (var hash in this._hashes) {
      BinaryPrimitives.WriteUInt64LittleEndian(span[at..], hash);
      at += 8;
    }

    foreach (var stripe in this._parity)
      foreach (var shard in stripe) {
        shard.CopyTo(span[at..]);
        at += shard.Length;
      }

    BinaryPrimitives.WriteUInt64LittleEndian(span[at..], Hash(span[..at]));
    return bytes;
  }

  /// <summary>Null when the bytes are not an intact index.</summary>
  public static BlockIndex? Deserialize(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < _HEADER_BYTES + 8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != _MAGIC)
      return null;

    var body = bytes[..^8];
    if (BinaryPrimitives.ReadUInt64LittleEndian(bytes[^8..]) != Hash(body))
      return null;

    var length = BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]);
    var mtime = BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]);
    var parityShards = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
    if (length < 0 || parityShards is < 0 or > 16)
      return null;

    var stripes = StripeCountFor(length);
    var hashCount = stripes == 0 ? 0 : (stripes - 1) * MaxDataShards + ShardCountFor(StripeLength(length, stripes - 1));
    var expected = _HEADER_BYTES + hashCount * 8L;
    for (var stripe = 0; stripe < stripes; ++stripe)
      expected += (long)parityShards * ShardBytesFor(StripeLength(length, stripe));
    if (expected != body.Length)
      return null;

    var hashes = new ulong[hashCount];
    var at = _HEADER_BYTES;
    for (var i = 0; i < hashCount; ++i, at += 8)
      hashes[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);

    var parity = new byte[stripes][][];
    for (var stripe = 0; stripe < stripes; ++stripe) {
      var size = ShardBytesFor(StripeLength(length, stripe));
      parity[stripe] = new byte[parityShards][];
      for (var p = 0; p < parityShards; ++p, at += size)
        parity[stripe][p] = bytes.Slice(at, size).ToArray();
    }

    return new(length, mtime, parityShards, hashes, parity);
  }

  private static void ReadExactly(Stream source, Span<byte> into) {
    var read = 0;
    while (read < into.Length) {
      var got = source.Read(into[read..]);
      if (got == 0)
        throw new IOException("The copy shrank while it was indexed");
      read += got;
    }
  }

}
