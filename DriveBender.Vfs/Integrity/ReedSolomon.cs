// Systematic Reed-Solomon erasure codec over GF(2^8).
//
// The Galois field (generator polynomial 29, i.e. x^8+x^4+x^3+x^2+1), the Vandermonde-derived
// encoding matrix whose top square is the identity, and the Gauss-Jordan inversion used for
// decoding are ported from Backblaze's JavaReedSolomon
// (https://github.com/Backblaze/JavaReedSolomon, commit d3c481dc69471e0c47ff6f67f33d53bde941675e).
// The vectorised coding loops, the direct parity reconstruction and the decode-plan cache are
// not part of the original.
//
// The MIT License (MIT)
//
// Copyright (c) 2015 Backblaze
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DivisonM.Vfs.Integrity;

/// <summary>
/// Systematic Reed-Solomon erasure codec: <c>dataShards</c> data shards followed by
/// <c>parityShards</c> parity shards; any <c>dataShards</c> of the total reconstruct the rest.
///
/// Only erasures are handled — the caller knows which shards are missing or bad (per-block
/// checksums detect corruption), so there is no error location. Shards are passed as one array
/// of buffers in shard order; every buffer must hold at least <c>offset + byteCount</c> bytes and
/// only that range is read or written. A shard that is logically shorter (the tail of a file) must
/// be zero-padded by the caller over the range being coded.
///
/// Instances are thread-safe: the coding matrices are immutable and the decode-plan cache is
/// locked. The output is byte-identical to JavaReedSolomon for the same shard counts.
/// </summary>
public sealed class ReedSolomon {

  public const int MaxDataShards = 32;
  public const int MaxParityShards = 16;

  /// <summary>Selects the coding loop; <see cref="Auto"/> picks the widest the CPU supports.</summary>
  internal enum KernelKind { Auto, Scalar, Vector128, Vector256 }

  private const int _DECODE_CACHE_CAPACITY = 32;

  // Columns are coded in blocks so the inputs of one block stay in L1/L2 while every group of
  // outputs is produced from them; must be a multiple of the widest vector.
  private const int _BLOCK_BYTES = 4096;

  // Each coefficient's split-nibble tables: low-nibble products (16 bytes, repeated for the
  // second 128-bit lane), then high-nibble products likewise.
  private const int _TABLE_BYTES = 64;
  private const int _HIGH_TABLE = 32;

  private readonly byte[][] _matrix; // totalShards x dataShards, top square = identity
  private readonly CodingPlan _encodePlan;

  private readonly Lock _cacheLock = new();
  private readonly Dictionary<ulong, LinkedListNode<KeyValuePair<ulong, CodingPlan>>> _decodePlans = [];
  private readonly LinkedList<KeyValuePair<ulong, CodingPlan>> _decodeLru = new();
  private int _decodePlanBuilds;

  public ReedSolomon(int dataShards, int parityShards) {
    if (dataShards is < 1 or > MaxDataShards)
      throw new ArgumentOutOfRangeException(nameof(dataShards), dataShards, $"must be 1..{MaxDataShards}");
    if (parityShards is < 1 or > MaxParityShards)
      throw new ArgumentOutOfRangeException(nameof(parityShards), parityShards, $"must be 1..{MaxParityShards}");
    if (dataShards + parityShards > 255)
      throw new ArgumentOutOfRangeException(nameof(parityShards), "at most 255 shards in total");

    this.DataShards = dataShards;
    this.ParityShards = parityShards;
    this._matrix = BuildMatrix(dataShards, this.TotalShards);

    var parityRows = new byte[parityShards][];
    for (var i = 0; i < parityShards; ++i)
      parityRows[i] = this._matrix[dataShards + i];

    this._encodePlan = new CodingPlan(dataShards, parityRows);
  }

  public int DataShards { get; }
  public int ParityShards { get; }
  public int TotalShards => this.DataShards + this.ParityShards;

  /// <summary>Coding loop in use; tests force the narrower paths to prove they agree.</summary>
  internal KernelKind Kernel {
    get;
    set {
      if (!IsSupported(value))
        throw new PlatformNotSupportedException($"{value} coding loop is not supported on this CPU");
      field = value;
    }
  } = KernelKind.Auto;

  internal static bool IsSupported(KernelKind kind) => kind switch {
    KernelKind.Auto or KernelKind.Scalar => true,
    KernelKind.Vector128 => Ssse3.IsSupported || AdvSimd.Arm64.IsSupported,
    KernelKind.Vector256 => Avx2.IsSupported,
    _ => false
  };

  /// <summary>Number of decode plans currently cached.</summary>
  internal int CachedDecodePlans {
    get {
      lock (this._cacheLock)
        return this._decodePlans.Count;
    }
  }

  /// <summary>Number of decode plans built (matrix inversions) over this instance's life.</summary>
  internal int DecodePlanBuilds => Volatile.Read(ref this._decodePlanBuilds);

  /// <summary>
  /// Computes the parity shards from the data shards over <paramref name="byteCount"/> bytes
  /// starting at <paramref name="offset"/> in every shard.
  /// </summary>
  public void EncodeParity(byte[][] shards, int offset, int byteCount) {
    this.CheckShards(shards, offset, byteCount);
    for (var i = this.DataShards; i < this.TotalShards; ++i)
      CheckOutputsNotShared(shards, i);

    var outputs = new byte[this.ParityShards][];
    Array.Copy(shards, this.DataShards, outputs, 0, this.ParityShards);
    this.Code(this._encodePlan, shards, outputs, offset, byteCount);
  }

  /// <summary>True when every parity shard matches the data shards over the given range.</summary>
  public bool IsParityCorrect(byte[][] shards, int offset, int byteCount) {
    this.CheckShards(shards, offset, byteCount);

    var chunk = Math.Min(byteCount, 64 * 1024);
    var temp = new byte[this.ParityShards][];
    for (var i = 0; i < temp.Length; ++i)
      temp[i] = ArrayPool<byte>.Shared.Rent(Math.Max(chunk, 1));

    try {
      // parity is computed a chunk at a time into the temp buffers and compared in place
      for (var done = 0; done < byteCount; done += chunk) {
        var length = Math.Min(chunk, byteCount - done);
        this.CodeShifted(this._encodePlan, shards, offset + done, temp, length);

        for (var p = 0; p < this.ParityShards; ++p)
          if (!temp[p].AsSpan(0, length).SequenceEqual(shards[this.DataShards + p].AsSpan(offset + done, length)))
            return false;
      }

      return true;
    } finally {
      foreach (var buffer in temp)
        ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <summary>
  /// Reconstructs every shard whose <paramref name="shardPresent"/> flag is false — data and parity
  /// alike — from the present ones, over the given range. Present shards are only read.
  /// </summary>
  /// <exception cref="ArgumentException">More shards are missing than there are parity shards.</exception>
  public void DecodeMissing(byte[][] shards, bool[] shardPresent, int offset, int byteCount) {
    this.CheckShards(shards, offset, byteCount);
    ArgumentNullException.ThrowIfNull(shardPresent);
    if (shardPresent.Length != this.TotalShards)
      throw new ArgumentException($"expected {this.TotalShards} presence flags, got {shardPresent.Length}", nameof(shardPresent));

    ulong presentMask = 0;
    var missing = 0;
    for (var i = 0; i < this.TotalShards; ++i)
      if (shardPresent[i])
        presentMask |= 1UL << i;
      else
        ++missing;

    if (missing == 0)
      return;

    if (missing > this.ParityShards)
      throw new ArgumentException($"{missing} shards missing but only {this.ParityShards} parity shards: not recoverable", nameof(shardPresent));

    var plan = this.GetDecodePlan(presentMask);

    var inputs = new byte[this.DataShards][];
    for (var i = 0; i < inputs.Length; ++i)
      inputs[i] = shards[plan.InputShards[i]];

    var outputs = new byte[plan.OutputShards.Length][];
    for (var i = 0; i < outputs.Length; ++i) {
      CheckOutputsNotShared(shards, plan.OutputShards[i]);
      outputs[i] = shards[plan.OutputShards[i]];
    }

    this.Code(plan, inputs, outputs, offset, byteCount);
  }

  private void CheckShards(byte[][] shards, int offset, int byteCount) {
    ArgumentNullException.ThrowIfNull(shards);
    if (shards.Length != this.TotalShards)
      throw new ArgumentException($"expected {this.TotalShards} shards, got {shards.Length}", nameof(shards));
    ArgumentOutOfRangeException.ThrowIfNegative(offset);
    ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

    var needed = (long)offset + byteCount;
    for (var i = 0; i < shards.Length; ++i) {
      var shard = shards[i] ?? throw new ArgumentException($"shard {i} is null", nameof(shards));
      if (shard.Length < needed)
        throw new ArgumentException($"shard {i} holds {shard.Length} bytes, {needed} needed", nameof(shards));
    }
  }

  /// <summary>
  /// A shard being written must not share its buffer with any other shard. Inputs may share one
  /// (several all-zero tail shards backed by a single zero buffer, say).
  /// </summary>
  private static void CheckOutputsNotShared(byte[][] shards, int index) {
    for (var j = 0; j < shards.Length; ++j)
      if (j != index && ReferenceEquals(shards[j], shards[index]))
        throw new ArgumentException($"shard {index} is written but shares its buffer with shard {j}", nameof(shards));
  }

  #region decode plans

  private CodingPlan GetDecodePlan(ulong presentMask) {
    lock (this._cacheLock) {
      if (this._decodePlans.TryGetValue(presentMask, out var hit)) {
        this._decodeLru.Remove(hit);
        this._decodeLru.AddFirst(hit);
        return hit.Value.Value;
      }
    }

    // built outside the lock: two threads racing on the same new pattern both invert, harmlessly
    var plan = this.BuildDecodePlan(presentMask);
    Interlocked.Increment(ref this._decodePlanBuilds);

    lock (this._cacheLock) {
      if (this._decodePlans.TryGetValue(presentMask, out var raced))
        return raced.Value.Value;

      var node = this._decodeLru.AddFirst(new KeyValuePair<ulong, CodingPlan>(presentMask, plan));
      this._decodePlans.Add(presentMask, node);
      if (this._decodePlans.Count > _DECODE_CACHE_CAPACITY) {
        var oldest = this._decodeLru.Last!;
        this._decodeLru.RemoveLast();
        this._decodePlans.Remove(oldest.Value.Key);
      }

      return plan;
    }
  }

  private CodingPlan BuildDecodePlan(ulong presentMask) {
    var k = this.DataShards;

    // the rows of the encoding matrix for the first k present shards form a square matrix that
    // maps the original data to those shards; its inverse maps them back to the data
    var inputShards = new int[k];
    var subMatrix = new byte[k][];
    for (int shard = 0, row = 0; row < k; ++shard)
      if ((presentMask & (1UL << shard)) != 0) {
        inputShards[row] = shard;
        subMatrix[row] = (byte[])this._matrix[shard].Clone();
        ++row;
      }

    var dataDecode = Invert(subMatrix);

    var outputShards = new List<int>();
    var rows = new List<byte[]>();
    for (var shard = 0; shard < this.TotalShards; ++shard) {
      if ((presentMask & (1UL << shard)) != 0)
        continue;

      outputShards.Add(shard);

      // a missing data shard is one row of the inverse; a missing parity shard is its encoding
      // row applied to the decoded data, i.e. that row times the inverse — so it comes straight
      // from the present shards without first materialising the missing data
      rows.Add(shard < k ? dataDecode[shard] : MultiplyRow(this._matrix[shard], dataDecode));
    }

    return new CodingPlan(k, [.. rows], inputShards, [.. outputShards]);
  }

  #endregion

  #region coding loops

  /// <summary>
  /// outputs[r][offset..] = sum over i of rows[r][i] * inputs[i][offset..] for every output row.
  /// </summary>
  private void Code(CodingPlan plan, byte[][] inputs, byte[][] outputs, int offset, int byteCount)
    => this.CodeCore(plan, inputs, offset, outputs, offset, byteCount);

  /// <summary>Like <see cref="Code"/> but writes the outputs at position 0.</summary>
  private void CodeShifted(CodingPlan plan, byte[][] inputs, int inputOffset, byte[][] outputs, int byteCount)
    => this.CodeCore(plan, inputs, inputOffset, outputs, 0, byteCount);

  private void CodeCore(CodingPlan plan, byte[][] inputs, int inputOffset, byte[][] outputs, int outputOffset, int byteCount) {
    var kernel = this.Kernel;
    if (kernel == KernelKind.Auto)
      kernel = IsSupported(KernelKind.Vector256) ? KernelKind.Vector256
        : IsSupported(KernelKind.Vector128) ? KernelKind.Vector128
        : KernelKind.Scalar;

    var shift = outputOffset - inputOffset;
    var end = inputOffset + byteCount;
    for (var blockStart = inputOffset; blockStart < end; blockStart += _BLOCK_BYTES) {
      var blockEnd = Math.Min(end, blockStart + _BLOCK_BYTES);
      for (var row = 0; row < plan.OutputCount;) {
        var group = Math.Min(4, plan.OutputCount - row);
        var position = kernel switch {
          KernelKind.Vector256 => Vector256Group(plan, row, group, inputs, outputs, shift, blockStart, blockEnd),
          KernelKind.Vector128 => Vector128Group(plan, row, group, inputs, outputs, shift, blockStart, blockEnd),
          _ => blockStart
        };
        ScalarRows(plan, row, group, inputs, outputs, shift, position, blockEnd);
        row += group;
      }
    }
  }

  private static void ScalarRows(CodingPlan plan, int firstRow, int rowCount, byte[][] inputs, byte[][] outputs, int shift, int start, int end) {
    if (start >= end)
      return;

    var table = Galois.MultiplicationTable;
    var length = end - start;
    for (var r = firstRow; r < firstRow + rowCount; ++r) {
      var output = outputs[r].AsSpan(start + shift, length);
      var coefficients = plan.Rows[r];

      var multiply = table.AsSpan(coefficients[0] << 8, 256);
      var input = inputs[0].AsSpan(start, length);
      for (var j = 0; j < output.Length; ++j)
        output[j] = multiply[input[j]];

      for (var i = 1; i < plan.InputCount; ++i) {
        multiply = table.AsSpan(coefficients[i] << 8, 256);
        input = inputs[i].AsSpan(start, length);
        for (var j = 0; j < output.Length; ++j)
          output[j] ^= multiply[input[j]];
      }
    }
  }

  private static int Vector256Group(CodingPlan plan, int row, int group, byte[][] inputs, byte[][] outputs, int shift, int start, int end) => group switch {
    1 => Vector256Kernel<One>(plan, row, inputs, outputs, shift, start, end),
    2 => Vector256Kernel<Two>(plan, row, inputs, outputs, shift, start, end),
    3 => Vector256Kernel<Three>(plan, row, inputs, outputs, shift, start, end),
    _ => Vector256Kernel<Four>(plan, row, inputs, outputs, shift, start, end)
  };

  private static int Vector128Group(CodingPlan plan, int row, int group, byte[][] inputs, byte[][] outputs, int shift, int start, int end) => group switch {
    1 => Vector128Kernel<One>(plan, row, inputs, outputs, shift, start, end),
    2 => Vector128Kernel<Two>(plan, row, inputs, outputs, shift, start, end),
    3 => Vector128Kernel<Three>(plan, row, inputs, outputs, shift, start, end),
    _ => Vector128Kernel<Four>(plan, row, inputs, outputs, shift, start, end)
  };

  // Split-nibble multiply: c*x = c*(x & 15) ^ c*(x >> 4 << 4), each half one 16-entry byte
  // shuffle. Up to four outputs accumulate in registers, so every input vector is loaded once per
  // group and every output vector stored once.
  private static int Vector256Kernel<TGroup>(CodingPlan plan, int firstRow, byte[][] inputs, byte[][] outputs, int shift, int position, int end)
    where TGroup : struct, IGroup {
    var mask = Vector256.Create((byte)0x0F);
    ref var tables = ref MemoryMarshal.GetArrayDataReference(plan.Tables);
    var inputCount = plan.InputCount;
    var rowStride = (nuint)(inputCount * _TABLE_BYTES);
    var firstTable = (nuint)firstRow * rowStride;

    for (; position + Vector256<byte>.Count <= end; position += Vector256<byte>.Count) {
      Vector256<byte> a0 = default, a1 = default, a2 = default, a3 = default;
      var t = firstTable;
      for (var i = 0; i < inputCount; ++i, t += _TABLE_BYTES) {
        var data = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(inputs[i]), (nuint)position);
        var low = data & mask;
        var high = Vector256.ShiftRightLogical(data.AsUInt16(), 4).AsByte() & mask;

        a0 ^= Avx2.Shuffle(Vector256.LoadUnsafe(ref tables, t), low)
              ^ Avx2.Shuffle(Vector256.LoadUnsafe(ref tables, t + _HIGH_TABLE), high);
        if (TGroup.Count > 1)
          a1 ^= Avx2.Shuffle(Vector256.LoadUnsafe(ref tables, t + rowStride), low)
                ^ Avx2.Shuffle(Vector256.LoadUnsafe(ref tables, t + rowStride + _HIGH_TABLE), high);
        if (TGroup.Count > 2)
          a2 ^= Avx2.Shuffle(Vector256.LoadUnsafe(ref tables, t + 2 * rowStride), low)
                ^ Avx2.Shuffle(Vector256.LoadUnsafe(ref tables, t + 2 * rowStride + _HIGH_TABLE), high);
        if (TGroup.Count > 3)
          a3 ^= Avx2.Shuffle(Vector256.LoadUnsafe(ref tables, t + 3 * rowStride), low)
                ^ Avx2.Shuffle(Vector256.LoadUnsafe(ref tables, t + 3 * rowStride + _HIGH_TABLE), high);
      }

      var at = (nuint)(position + shift);
      a0.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(outputs[firstRow]), at);
      if (TGroup.Count > 1)
        a1.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(outputs[firstRow + 1]), at);
      if (TGroup.Count > 2)
        a2.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(outputs[firstRow + 2]), at);
      if (TGroup.Count > 3)
        a3.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(outputs[firstRow + 3]), at);
    }

    return position;
  }

  private static int Vector128Kernel<TGroup>(CodingPlan plan, int firstRow, byte[][] inputs, byte[][] outputs, int shift, int position, int end)
    where TGroup : struct, IGroup {
    var mask = Vector128.Create((byte)0x0F);
    ref var tables = ref MemoryMarshal.GetArrayDataReference(plan.Tables);
    var inputCount = plan.InputCount;
    var rowStride = (nuint)(inputCount * _TABLE_BYTES);
    var firstTable = (nuint)firstRow * rowStride;

    for (; position + Vector128<byte>.Count <= end; position += Vector128<byte>.Count) {
      Vector128<byte> a0 = default, a1 = default, a2 = default, a3 = default;
      var t = firstTable;
      for (var i = 0; i < inputCount; ++i, t += _TABLE_BYTES) {
        var data = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(inputs[i]), (nuint)position);
        var low = data & mask;
        var high = Vector128.ShiftRightLogical(data.AsUInt16(), 4).AsByte() & mask;

        a0 ^= Lookup(Vector128.LoadUnsafe(ref tables, t), low)
              ^ Lookup(Vector128.LoadUnsafe(ref tables, t + _HIGH_TABLE), high);
        if (TGroup.Count > 1)
          a1 ^= Lookup(Vector128.LoadUnsafe(ref tables, t + rowStride), low)
                ^ Lookup(Vector128.LoadUnsafe(ref tables, t + rowStride + _HIGH_TABLE), high);
        if (TGroup.Count > 2)
          a2 ^= Lookup(Vector128.LoadUnsafe(ref tables, t + 2 * rowStride), low)
                ^ Lookup(Vector128.LoadUnsafe(ref tables, t + 2 * rowStride + _HIGH_TABLE), high);
        if (TGroup.Count > 3)
          a3 ^= Lookup(Vector128.LoadUnsafe(ref tables, t + 3 * rowStride), low)
                ^ Lookup(Vector128.LoadUnsafe(ref tables, t + 3 * rowStride + _HIGH_TABLE), high);
      }

      var at = (nuint)(position + shift);
      a0.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(outputs[firstRow]), at);
      if (TGroup.Count > 1)
        a1.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(outputs[firstRow + 1]), at);
      if (TGroup.Count > 2)
        a2.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(outputs[firstRow + 2]), at);
      if (TGroup.Count > 3)
        a3.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(outputs[firstRow + 3]), at);
    }

    return position;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static Vector128<byte> Lookup(Vector128<byte> table, Vector128<byte> indices) {
    if (Ssse3.IsSupported)
      return Ssse3.Shuffle(table, indices);
    if (AdvSimd.Arm64.IsSupported)
      return AdvSimd.Arm64.VectorTableLookup(table, indices);
    throw new PlatformNotSupportedException();
  }

  // Compile-time group sizes: each kernel instantiation keeps only the accumulators it uses.
  private interface IGroup { static abstract int Count { get; } }
  private readonly struct One : IGroup { public static int Count => 1; }
  private readonly struct Two : IGroup { public static int Count => 2; }
  private readonly struct Three : IGroup { public static int Count => 3; }
  private readonly struct Four : IGroup { public static int Count => 4; }

  /// <summary>A coefficient matrix ready for the coding loops, with its split-nibble tables.</summary>
  private sealed class CodingPlan {

    public CodingPlan(int inputCount, byte[][] rows, int[]? inputShards = null, int[]? outputShards = null) {
      this.InputCount = inputCount;
      this.Rows = rows;
      this.InputShards = inputShards ?? [];
      this.OutputShards = outputShards ?? [];

      var tables = new byte[rows.Length * inputCount * _TABLE_BYTES];
      var multiply = Galois.MultiplicationTable;
      for (var r = 0; r < rows.Length; ++r)
      for (var i = 0; i < inputCount; ++i) {
        var c = rows[r][i] << 8;
        var t = (r * inputCount + i) * _TABLE_BYTES;
        for (var x = 0; x < 16; ++x) {
          tables[t + x] = tables[t + 16 + x] = multiply[c + x];
          tables[t + _HIGH_TABLE + x] = tables[t + _HIGH_TABLE + 16 + x] = multiply[c + (x << 4)];
        }
      }

      this.Tables = tables;
    }

    public int InputCount { get; }
    public int OutputCount => this.Rows.Length;
    public byte[][] Rows { get; }
    public byte[] Tables { get; }
    public int[] InputShards { get; }
    public int[] OutputShards { get; }
  }

  #endregion

  #region matrix algebra (ported)

  /// <summary>
  /// The encoding matrix: a totalShards x dataShards Vandermonde matrix multiplied by the inverse
  /// of its top square, which makes the top square the identity (data shards pass through
  /// unchanged) while keeping every square subset of rows invertible.
  /// </summary>
  private static byte[][] BuildMatrix(int dataShards, int totalShards) {
    var vandermonde = new byte[totalShards][];
    for (var r = 0; r < totalShards; ++r) {
      vandermonde[r] = new byte[dataShards];
      for (var c = 0; c < dataShards; ++c)
        vandermonde[r][c] = Galois.Exp((byte)r, c);
    }

    var top = new byte[dataShards][];
    for (var r = 0; r < dataShards; ++r)
      top[r] = (byte[])vandermonde[r].Clone();

    var topInverse = Invert(top);
    var result = new byte[totalShards][];
    for (var r = 0; r < totalShards; ++r)
      result[r] = MultiplyRow(vandermonde[r], topInverse);

    return result;
  }

  /// <summary>row (1 x n) times matrix (n x n).</summary>
  private static byte[] MultiplyRow(byte[] row, byte[][] matrix) {
    var columns = matrix[0].Length;
    var result = new byte[columns];
    for (var c = 0; c < columns; ++c) {
      byte value = 0;
      for (var i = 0; i < row.Length; ++i)
        value ^= Galois.Multiply(row[i], matrix[i][c]);
      result[c] = value;
    }

    return result;
  }

  /// <summary>Gauss-Jordan inversion of a square matrix; the input is consumed.</summary>
  private static byte[][] Invert(byte[][] matrix) {
    var size = matrix.Length;
    var work = new byte[size][];
    for (var r = 0; r < size; ++r) {
      work[r] = new byte[size * 2];
      Array.Copy(matrix[r], work[r], size);
      work[r][size + r] = 1;
    }

    // clear below the diagonal and scale the diagonal to 1
    for (var r = 0; r < size; ++r) {
      if (work[r][r] == 0)
        for (var below = r + 1; below < size; ++below)
          if (work[below][r] != 0) {
            (work[r], work[below]) = (work[below], work[r]);
            break;
          }

      if (work[r][r] == 0)
        throw new InvalidOperationException("matrix is singular");

      if (work[r][r] != 1) {
        var scale = Galois.Divide(1, work[r][r]);
        for (var c = 0; c < size * 2; ++c)
          work[r][c] = Galois.Multiply(work[r][c], scale);
      }

      for (var below = r + 1; below < size; ++below) {
        var scale = work[below][r];
        if (scale == 0)
          continue;
        for (var c = 0; c < size * 2; ++c)
          work[below][c] ^= Galois.Multiply(scale, work[r][c]);
      }
    }

    // clear above the diagonal
    for (var d = 0; d < size; ++d)
    for (var above = 0; above < d; ++above) {
      var scale = work[above][d];
      if (scale == 0)
        continue;
      for (var c = 0; c < size * 2; ++c)
        work[above][c] ^= Galois.Multiply(scale, work[d][c]);
    }

    var inverse = new byte[size][];
    for (var r = 0; r < size; ++r)
      inverse[r] = work[r][size..];

    return inverse;
  }

  #endregion

  /// <summary>GF(2^8) with generator polynomial 29 (0x11D), as in JavaReedSolomon.</summary>
  internal static class Galois {

    private const int _FIELD_SIZE = 256;
    private const int _GENERATING_POLYNOMIAL = 29;

    private static readonly short[] _log = GenerateLogTable(_GENERATING_POLYNOMIAL);

    // repeated once so the sum of two logarithms needs no reduction
    private static readonly byte[] _exp = GenerateExpTable(_log);

    /// <summary>MultiplicationTable[(a &lt;&lt; 8) | b] = a * b.</summary>
    public static readonly byte[] MultiplicationTable = GenerateMultiplicationTable();

    public static byte Multiply(byte a, byte b)
      => a == 0 || b == 0 ? (byte)0 : _exp[_log[a] + _log[b]];

    public static byte Divide(byte a, byte b) {
      if (a == 0)
        return 0;
      if (b == 0)
        throw new DivideByZeroException();

      var log = _log[a] - _log[b];
      if (log < 0)
        log += 255;
      return _exp[log];
    }

    public static byte Exp(byte a, int n) {
      if (n == 0)
        return 1;
      if (a == 0)
        return 0;

      var log = _log[a] * n;
      while (log >= 255)
        log -= 255;
      return _exp[log];
    }

    private static short[] GenerateLogTable(int polynomial) {
      var result = new short[_FIELD_SIZE];
      Array.Fill(result, (short)-1);
      var b = 1;
      for (short log = 0; log < _FIELD_SIZE - 1; ++log) {
        if (result[b] != -1)
          throw new InvalidOperationException("duplicate logarithm: bad polynomial");
        result[b] = log;
        b <<= 1;
        if (b >= _FIELD_SIZE)
          b = (b - _FIELD_SIZE) ^ polynomial;
      }

      return result;
    }

    private static byte[] GenerateExpTable(short[] logTable) {
      var result = new byte[_FIELD_SIZE * 2 - 2];
      for (var i = 1; i < _FIELD_SIZE; ++i) {
        var log = logTable[i];
        result[log] = (byte)i;
        result[log + _FIELD_SIZE - 1] = (byte)i;
      }

      return result;
    }

    private static byte[] GenerateMultiplicationTable() {
      var result = new byte[_FIELD_SIZE * _FIELD_SIZE];
      for (var a = 0; a < _FIELD_SIZE; ++a)
      for (var b = 0; b < _FIELD_SIZE; ++b)
        result[(a << 8) | b] = Multiply((byte)a, (byte)b);
      return result;
    }
  }
}
