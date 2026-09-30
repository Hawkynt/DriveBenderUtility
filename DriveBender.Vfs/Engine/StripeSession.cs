namespace DivisonM.Vfs.Engine;

/// <summary>
/// The stripe session of one file being written (docs/IncomingFiles.md): it spreads the file's
/// blocks over every disk of its group while the file is open, and later fills the FINAL disks —
/// the ones that must end up holding the whole file — with the blocks they are missing.
///
/// Every disk writes into a temp: the finals into the file's staged temp (primary and shadow
/// namespace, exactly as an unstriped staged file), the HELPERS into a temp of their own, named so
/// that nothing else in the pool ever mistakes it for a copy of the file. Nothing a session writes
/// carries the file's real name; the engine renames a final only once <see cref="Fill"/> made it
/// complete and it was flushed.
///
/// Each block goes to the <see cref="CopiesPerBlock"/> disks that can take it first — so an
/// acknowledged block exists on as many disks as the folder's ack count demands, striped or not —
/// and stays with them (a later partial write of the same block goes to the disks that hold it).
/// On a tie a final is preferred to a helper, so an uncontended pool writes straight into its
/// finals and has nothing to fill.
///
/// Not thread-safe against itself by design: the engine drives a session only under the file's
/// write lease (writes, fill, truncate) or read lease (reads), which already serialise it. The lock
/// here only guards the block map against the parallel dispatch inside one <see cref="Write"/>.
/// </summary>
public sealed class StripeSession {

  /// <summary>One disk of the session and the temp it writes into.</summary>
  public sealed record Member(IVolumeIO Volume, string Path, bool Shadow, bool IsFinal);

  /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/> of a member's temp, creating it when <paramref name="create"/>.</summary>
  public delegate void WriteAt(Member member, long offset, ReadOnlySpan<byte> data, bool create);

  /// <summary>Reads up to <c>buffer.Length</c> bytes at <paramref name="offset"/> of a member's temp; returns the count read.</summary>
  public delegate int ReadAt(Member member, long offset, Span<byte> buffer);

  private readonly bool _allowDegraded;
  private readonly List<Member> _members;
  private bool[] _created;
  private bool[] _failed;
  private readonly Dictionary<long, ulong> _holders = [];
  private readonly Func<IVolumeIO, double> _loadOf;
  private readonly WriteAt _writeAt;
  private readonly ReadAt _readAt;
  private readonly Lock _lock = new();

  /// <summary>The largest group a session spreads over; the block map keeps one bit per disk.</summary>
  public const int MaxMembers = 64;

  /// <param name="allowDegraded">
  /// Whether a block may be acknowledged on fewer disks than <paramref name="copiesPerBlock"/> when
  /// fewer are reachable — the pool's degraded-write rule (SAFE-DEGRADE), applied per block exactly
  /// as the unstriped write path applies it per write.
  /// </param>
  public StripeSession(IReadOnlyList<Member> members, int blockSize, int copiesPerBlock, Func<IVolumeIO, double>? loadOf = null,
    WriteAt? writeAt = null, ReadAt? readAt = null, bool allowDegraded = true) {
    this._allowDegraded = allowDegraded;
    if (members.Count == 0 || members.Count > MaxMembers)
      throw new ArgumentOutOfRangeException(nameof(members), members.Count, $"A stripe session spans 1..{MaxMembers} disks");
    if (!members.Any(m => m.IsFinal))
      throw new ArgumentException("A stripe session needs at least one final disk", nameof(members));

    this._members = [.. members];
    this._created = [.. members.Select(m => m.IsFinal)]; // the finals' temps exist from Create on
    this._failed = new bool[members.Count];
    this.BlockSize = Math.Max(1, blockSize);
    this.CopiesPerBlock = Math.Clamp(copiesPerBlock, 1, members.Count);
    this._loadOf = loadOf ?? (static _ => 0);
    this._writeAt = writeAt ?? _DirectWrite;
    this._readAt = readAt ?? _DirectRead;
  }

  /// <summary>
  /// Times and mode an application set on the file while it was open (<c>cp -p</c> stamps the file
  /// before closing it). Filling writes to the finals after that, which moves their modification
  /// time, so the engine applies these again once they are filled. A write after the stamp clears the
  /// times: then the write's time is the right one.
  /// </summary>
  public DateTime? PendingCreationTimeUtc { get; set; }
  public DateTime? PendingLastWriteTimeUtc { get; set; }
  public UnixFileMode? PendingPermissions { get; set; }

  public int BlockSize { get; }
  public int CopiesPerBlock { get; }
  public long Length { get; private set; }

  public IReadOnlyList<Member> Members => this._members;
  public IEnumerable<Member> Finals => this._members.Where(m => m.IsFinal);

  /// <summary>How many blocks a final still lacks — what <see cref="Fill"/> would copy onto it.</summary>
  public int MissingOn(Member final) {
    var bit = 1UL << this._members.IndexOf(final);
    lock (this._lock)
      return this._holders.Count(pair => (pair.Value & bit) == 0);
  }

  /// <summary>Which disks hold the current bytes of a block (by member index) — for tests and diagnostics.</summary>
  public IReadOnlyList<Member> HoldersOf(long block) {
    lock (this._lock)
      return this._holders.TryGetValue(block, out var mask) ? [.. this._Members(mask)] : [];
  }

  private IEnumerable<Member> _Members(ulong mask) {
    for (var i = 0; i < this._members.Count; ++i)
      if ((mask & (1UL << i)) != 0)
        yield return this._members[i];
  }

  #region writing

  /// <summary>
  /// Lands <paramref name="data"/> at <paramref name="offset"/>: every block it touches goes to the
  /// disks that hold that block already, or — a block written for the first time — to the
  /// <see cref="CopiesPerBlock"/> readiest disks. The disks work in parallel, each writing its share
  /// in contiguous runs. Returns once every block is on its disks; a disk that fails is left out and
  /// its share goes to the next readiest, so the write only fails when too few disks are left.
  /// </summary>
  public void Write(long offset, ReadOnlySpan<byte> data) {
    if (data.Length > 0)
      this.Write(offset, data.ToArray()); // copied once: the dispatch runs on several threads, which a span cannot cross
  }

  /// <summary>As <see cref="Write(long, ReadOnlySpan{byte})"/>, taking an array the caller no longer changes — no copy.</summary>
  public void Write(long offset, byte[] bytes) {
    if (bytes.Length == 0)
      return;

    this.PendingLastWriteTimeUtc = null; // written after it was stamped: the write's time is the file's

    var first = offset / this.BlockSize;
    var last = (offset + bytes.Length - 1) / this.BlockSize;

    PoolFsException? lastError = null;
    for (var attempt = 0; ; ++attempt) {
      var plan = this._Plan(first, last, this._CopiesNow(lastError));
      var failures = this._Dispatch(plan, offset, bytes);
      if (failures.Count == 0) {
        lock (this._lock) {
          foreach (var (block, mask) in plan.Assignments)
            this._holders[block] = mask;

          this.Length = Math.Max(this.Length, offset + bytes.Length);
        }

        return;
      }

      // A disk that refused gets no more new blocks, and the write is planned again without it.
      // What it already holds stays valid: refusing a write — a full disk, above all — is not
      // losing data, and forgetting its blocks threw away bytes that were still sitting on it.
      // If it really lost them, reading them fails, and filling falls back to another holder.
      lock (this._lock)
        foreach (var (index, error) in failures) {
          this._failed[index] = true;
          lastError = error;
        }

      if (attempt >= this._members.Count)
        throw _TooFew(lastError, this._Usable().Count());
    }
  }

  /// <summary>Disks still taking blocks: neither failed during this session nor gone offline.</summary>
  private IEnumerable<int> _Usable() => Enumerable.Range(0, this._members.Count).Where(i => !this._failed[i] && this._members[i].Volume.IsOnline);

  /// <summary>How many disks each block goes to right now: the ack count, or fewer when fewer are reachable and degraded writes are allowed.</summary>
  private int _CopiesNow(PoolFsException? lastError) {
    var usable = this._Usable().Count();
    if (usable >= this.CopiesPerBlock)
      return this.CopiesPerBlock;
    if (this._allowDegraded && usable >= 1)
      return usable;

    throw _TooFew(lastError, usable);
  }

  /// <summary>The refusal, keeping the kind a disk reported: a full disk says NoSpace, not a generic I/O error.</summary>
  private PoolFsException _TooFew(PoolFsException? lastError, int usable)
    => new(lastError?.Error ?? PoolFsError.Offline,
      $"Only {usable} disk(s) can take the file's blocks, {this.CopiesPerBlock} needed per block{(lastError == null ? "" : $": {lastError.Message}")}");

  /// <summary>Whether a disk stopped taking new blocks (it refused a write, or went offline) — what it holds may still be good.</summary>
  public bool IsFailed(Member member) {
    var index = this._members.IndexOf(member);
    return index < 0 || this._failed[index] || !member.Volume.IsOnline;
  }

  /// <summary>
  /// Moves the session to another group of disks (docs/IncomingFiles.md: a file that outgrows the
  /// landing zone continues in the storage group). The disks it had stop taking blocks and stop being
  /// finals — they keep what they hold, which filling copies across — and the new finals (whose
  /// temps the engine has just created) and helpers take every block from here on.
  /// </summary>
  public void Rehome(IReadOnlyList<Member> finals, IReadOnlyList<Member> helpers) {
    if (finals.Count == 0 || !finals.All(f => f.IsFinal) || helpers.Any(h => h.IsFinal))
      throw new ArgumentException("A new home needs at least one final, and helpers that are not finals");

    lock (this._lock) {
      var total = this._members.Count + finals.Count + helpers.Count;
      if (total > MaxMembers)
        throw new PoolFsException(PoolFsError.NoSpace, $"A stripe session spans at most {MaxMembers} disks; re-homing would need {total}");

      for (var i = 0; i < this._members.Count; ++i) {
        this._failed[i] = true; // no new blocks for the old home
        if (this._members[i].IsFinal)
          this._members[i] = this._members[i] with { IsFinal = false }; // its temp is deleted, not published
      }

      var old = this._members.Count;
      this._members.AddRange(finals);
      this._members.AddRange(helpers);
      Array.Resize(ref this._created, total);
      Array.Resize(ref this._failed, total);
      for (var i = old; i < old + finals.Count; ++i)
        this._created[i] = true; // the engine created the new finals' temps
    }
  }

  /// <summary>
  /// Makes a helper a final, once the engine has renamed its temp to the file's staged name — for
  /// when every final failed and the acknowledged blocks live on helpers only.
  /// </summary>
  public Member Promote(Member helper, string finalPath) {
    var index = this._members.IndexOf(helper);
    if (index < 0 || helper.IsFinal)
      throw new ArgumentException("Only a helper of this session can be promoted", nameof(helper));

    var promoted = helper with { Path = finalPath, IsFinal = true };
    lock (this._lock)
      this._members[index] = promoted;

    return promoted;
  }

  private sealed record Plan(List<(long Block, ulong Mask)> Assignments);

  /// <summary>
  /// What one more block costs a disk in this call, in milliseconds: its load (what is already
  /// queued there, times what an operation there currently takes) plus the block's transfer time at
  /// a nominal 256 MiB/s. Blocks queued on one disk coalesce into one contiguous write, so a further
  /// block costs transfer time, not another operation — scoring it as a whole operation made a disk
  /// measured at 500 ms look worth using once a few hundred small blocks were queued elsewhere.
  /// </summary>
  private double _Cost(double load, double queued) => load + (queued + 1) * this._perBlockMs;

  private readonly double _nominalBytesPerMs = 256.0 * 1024 * 1024 / 1000;
  private double _perBlockMs => this.BlockSize / this._nominalBytesPerMs;

  /// <summary>
  /// Chooses each block's disks. Plain loops over the handful of disks a group has, run once per
  /// block: the sort-and-allocate version of this ran per block too, and a single write of a few
  /// hundred small blocks turned into a few hundred sorts.
  /// </summary>
  private Plan _Plan(long first, long last, int copies) {
    var count = this._members.Count;
    var usable = new bool[count];
    var baseLoad = new double[count];
    var domain = new int[count];
    var domains = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var i in this._Usable()) {
      usable[i] = true;
      baseLoad[i] = this._loadOf(this._members[i].Volume);
    }

    for (var i = 0; i < count; ++i) {
      var id = this._members[i].Volume.PhysicalVolumeId;
      if (!domains.TryGetValue(id, out domain[i]))
        domains[id] = domain[i] = domains.Count;
    }

    var queued = new double[count]; // blocks this call has handed each disk so far
    var assignments = new List<(long, ulong)>((int)Math.Min(int.MaxValue, last - first + 1));
    var usedDomains = new bool[domains.Count];

    lock (this._lock)
      for (var block = first; block <= last; ++block) {
        var mask = 0UL;

        // a block keeps its disks: they hold its other bytes, so a partial write must land there
        if (this._holders.TryGetValue(block, out var held) && held != 0)
          for (var taken = 0; taken < copies; ++taken) {
            var best = -1;
            for (var i = 0; i < count; ++i)
              if (usable[i] && (held & (1UL << i)) != 0 && (mask & (1UL << i)) == 0
                  && (best < 0 || this._Cost(baseLoad[i], queued[i]) < this._Cost(baseLoad[best], queued[best])))
                best = i;

            if (best < 0)
              break;

            mask |= 1UL << best;
            ++queued[best];
          }

        if (mask == 0) {
          // a new block: the readiest disks, each on a different physical disk (the whole point of
          // a second copy is that it does not share the first one's fate); a final wins a tie
          Array.Clear(usedDomains);
          for (var taken = 0; taken < copies; ++taken) {
            var best = -1;
            for (var i = 0; i < count; ++i) {
              if (!usable[i] || (mask & (1UL << i)) != 0 || usedDomains[domain[i]])
                continue;

              if (best < 0 || this._Before(i, best, baseLoad, queued))
                best = i;
            }

            // fewer physical disks than copies per block: co-locate rather than refuse
            if (best < 0)
              for (var i = 0; i < count; ++i)
                if (usable[i] && (mask & (1UL << i)) == 0 && (best < 0 || queued[i] < queued[best]))
                  best = i;

            if (best < 0)
              break;

            mask |= 1UL << best;
            usedDomains[domain[best]] = true;
            ++queued[best];
          }
        }

        assignments.Add((block, mask));
      }

    return new(assignments);
  }

  /// <summary>Whether disk <paramref name="a"/> should take the next block before <paramref name="b"/>: cheaper first, then a final, then the lower index.</summary>
  private bool _Before(int a, int b, double[] baseLoad, double[] queued) {
    var costA = this._Cost(baseLoad[a], queued[a]);
    var costB = this._Cost(baseLoad[b], queued[b]);
    if (costA != costB)
      return costA < costB;
    if (this._members[a].IsFinal != this._members[b].IsFinal)
      return this._members[a].IsFinal;

    return a < b;
  }

  /// <summary>Writes each disk's share of the call — contiguous runs, disks in parallel; returns the disks that failed, and why.</summary>
  private List<(int Index, PoolFsException Error)> _Dispatch(Plan plan, long offset, byte[] bytes) {
    var perMember = new List<(long From, int Start, int Length)>[this._members.Count];
    var end = offset + bytes.Length;
    foreach (var (block, mask) in plan.Assignments) {
      var from = Math.Max(offset, block * this.BlockSize);
      var to = Math.Min(end, (block + 1) * this.BlockSize);
      for (var i = 0; i < this._members.Count; ++i) {
        if ((mask & (1UL << i)) == 0)
          continue;

        var runs = perMember[i] ??= [];
        if (runs.Count > 0 && runs[^1].From + runs[^1].Length == from)
          runs[^1] = runs[^1] with { Length = runs[^1].Length + (int)(to - from) };
        else
          runs.Add((from, (int)(from - offset), (int)(to - from)));
      }
    }

    var failed = new System.Collections.Concurrent.ConcurrentDictionary<int, PoolFsException>();
    var work = Enumerable.Range(0, this._members.Count).Where(i => perMember[i] != null).ToArray();
    void Run(int i) {
      try {
        foreach (var (from, start, length) in perMember[i]) {
          bool create;
          lock (this._lock) {
            create = !this._created[i];
            this._created[i] = true;
          }

          this._writeAt(this._members[i], from, bytes.AsSpan(start, length), create);
        }
      } catch (PoolFsException e) {
        failed[i] = e;
      } catch (IOException e) {
        failed[i] = new(PoolFsError.IoError, e.Message);
      }
    }

    if (work.Length == 1)
      Run(work[0]);
    else
      Parallel.ForEach(work, Run);

    return [.. failed.Select(pair => (pair.Key, pair.Value))];
  }

  #endregion

  #region reading

  /// <summary>Reads the file as written so far: each block from a disk that holds it, zeros where nothing was written.</summary>
  public int Read(long offset, Span<byte> buffer) {
    var length = this.Length;
    if (offset >= length)
      return 0;

    var count = (int)Math.Min(buffer.Length, length - offset);
    var done = 0;
    while (done < count) {
      var position = offset + done;
      var block = position / this.BlockSize;
      var take = (int)Math.Min(count - done, (block + 1) * this.BlockSize - position);
      var target = buffer.Slice(done, take);

      ulong mask;
      lock (this._lock)
        mask = this._holders.TryGetValue(block, out var held) ? held : 0;

      if (mask == 0)
        target.Clear(); // never written: a hole reads as zeros
      else
        this._ReadFromAny(mask, position, target);

      done += take;
    }

    return count;
  }

  /// <summary>Reads a block from the least loaded disk holding it, falling back to the others.</summary>
  private void _ReadFromAny(ulong holders, long position, Span<byte> target) {
    PoolFsException? last = null;
    var tried = 0UL;
    while (true) {
      var best = -1;
      var bestLoad = double.MaxValue;
      for (var i = 0; i < this._members.Count; ++i) {
        var bit = 1UL << i;
        if ((holders & bit) == 0 || (tried & bit) != 0)
          continue;

        var load = this._loadOf(this._members[i].Volume);
        if (best < 0 || load < bestLoad) {
          best = i;
          bestLoad = load;
        }
      }

      if (best < 0)
        throw last ?? new PoolFsException(PoolFsError.IoError, $"No disk could serve the block at {position}");

      tried |= 1UL << best;
      try {
        var got = this._ReadFully(this._members[best], position, target);
        target[got..].Clear(); // written sparsely: the unwritten tail of a block is zeros
        return;
      } catch (PoolFsException e) {
        last = e;
      }
    }
  }

  private int _ReadFully(Member member, long position, Span<byte> target) {
    var total = 0;
    while (total < target.Length) {
      var got = this._readAt(member, position + total, target[total..]);
      if (got <= 0)
        break;

      total += got;
    }

    return total;
  }

  #endregion

  #region length

  /// <summary>
  /// Sets the file's length. Growing only moves the end (the new range reads as zeros). Shrinking
  /// cuts every temp that exists at the new end too — so that growing it AGAIN later reads zeros
  /// there rather than bringing back bytes that were cut away — and forgets the blocks past it.
  /// </summary>
  public void SetLength(long length, Action<Member, long> truncate) {
    if (length >= this.Length) {
      this.Length = length;
      return;
    }

    for (var i = 0; i < this._members.Count; ++i)
      if (this._created[i] && !this._failed[i])
        truncate(this._members[i], length);

    lock (this._lock) {
      var firstGone = (length + this.BlockSize - 1) / this.BlockSize;
      foreach (var block in this._holders.Keys.Where(b => b >= firstGone).ToArray())
        this._holders.Remove(block);

      this.Length = length;
    }
  }

  #endregion

  #region filling

  /// <summary>
  /// Makes <paramref name="final"/> hold every block of the file: copies each block it lacks from a
  /// disk that has it, then sets its temp to the file's exact length. Afterwards the final's temp IS
  /// the whole file, ready to be flushed and renamed.
  /// </summary>
  public void Fill(Member final, Action<Member, long> truncate) {
    var index = this._members.IndexOf(final);
    if (index < 0 || !final.IsFinal)
      throw new ArgumentException("Only a final disk of this session is filled", nameof(final));

    var bit = 1UL << index;
    (long Block, ulong Mask)[] missing;
    lock (this._lock)
      missing = [.. this._holders.Where(pair => (pair.Value & bit) == 0).Select(pair => (pair.Key, pair.Value)).OrderBy(pair => pair.Key)];

    // Copied in RUNS: consecutive blocks one disk holds travel as one read and one write, up to
    // _FILL_RUN bytes. Copying block by block cost an open and a write per block — measured, a slow
    // final took six seconds per small file that way, all of it per-operation overhead.
    var blocksPerRun = (int)Math.Max(1, _FILL_RUN / this.BlockSize);
    var buffer = new byte[(long)blocksPerRun * this.BlockSize];
    for (var at = 0; at < missing.Length;) {
      var (block, mask) = missing[at];
      if (mask == 0)
        throw new PoolFsException(PoolFsError.IoError, $"Block {block} of '{final.Path}' is on no disk any more");

      var source = this._LeastLoaded(mask);
      var run = 1;
      while (at + run < missing.Length && run < blocksPerRun
             && missing[at + run].Block == block + run && (missing[at + run].Mask & (1UL << source)) != 0)
        ++run;

      var position = block * this.BlockSize;
      var length = (int)Math.Min((long)run * this.BlockSize, this.Length - position);
      if (length > 0) {
        var span = buffer.AsSpan(0, length);
        try {
          var got = this._ReadFully(this._members[source], position, span);
          span[got..].Clear(); // written sparsely: an unwritten tail reads as zeros
        } catch (PoolFsException) {
          // that disk cannot serve the run: block by block, from whichever other disk holds each
          for (var i = 0; i < run; ++i) {
            var from = i * this.BlockSize;
            var take = Math.Min(this.BlockSize, length - from);
            if (take > 0)
              this._ReadFromAny(missing[at + i].Mask & ~(1UL << source), position + from, span.Slice(from, take));
          }
        }

        this._writeAt(final, position, span, false);
      }

      lock (this._lock)
        for (var i = 0; i < run; ++i)
          this._holders[block + i] |= bit;

      at += run;
    }

    truncate(final, this.Length);
  }

  /// <summary>The most one fill step reads and writes at once.</summary>
  private const long _FILL_RUN = 4L * 1024 * 1024;

  private int _LeastLoaded(ulong holders) {
    var best = -1;
    var bestLoad = double.MaxValue;
    for (var i = 0; i < this._members.Count; ++i)
      if ((holders & (1UL << i)) != 0 && this._loadOf(this._members[i].Volume) is var load && (best < 0 || load < bestLoad)) {
        best = i;
        bestLoad = load;
      }

    return best;
  }

  /// <summary>The helpers' temps — to delete once no final needs their blocks.</summary>
  public IEnumerable<Member> CreatedHelpers() {
    for (var i = 0; i < this._members.Count; ++i)
      if (!this._members[i].IsFinal && this._created[i])
        yield return this._members[i];
  }

  #endregion

  private static void _DirectWrite(Member member, long offset, ReadOnlySpan<byte> data, bool create) {
    using var stream = member.Volume.OpenWrite(member.Path, member.Shadow, create);
    stream.Seek(offset, SeekOrigin.Begin);
    stream.Write(data);
  }

  private static int _DirectRead(Member member, long offset, Span<byte> buffer) {
    using var stream = member.Volume.OpenRead(member.Path, member.Shadow);
    if (offset >= stream.Length)
      return 0;

    stream.Seek(offset, SeekOrigin.Begin);
    return stream.Read(buffer);
  }

}
