namespace DivisonM.Vfs.Engine;

/// <summary>
/// Publishes whole-file content honouring backend capability gaps (FR-CAP-ADAPT): a
/// backend with <see cref="BackendCaps.AtomicRename"/> gets temp + rename (SAFE-ATOMIC);
/// one without gets a direct write followed by a read-back verification — the journal
/// intent open around the call covers the non-atomic gap.
///
/// Every publish path is STREAMING (chunked, no whole-file <c>byte[]</c>/<c>MemoryStream</c>):
/// a multi-GB file is copied through a fixed buffer, so file size is bounded only by the
/// destination volume, never by RAM (SAFE-BIGFILE).
/// </summary>
public static class WholeFilePublisher {

  /// <summary>Copy chunk for streaming publishes — large enough to amortise syscalls, small enough to stay off the LOH per-copy.</summary>
  public const int CopyBufferSize = 1 << 20; // 1 MiB

  /// <summary>
  /// Copies <paramref name="source"/> into <paramref name="destination"/> through a pair of fixed
  /// buffers and returns the byte count.
  ///
  /// DOUBLE-BUFFERED: chunk N+1 is read while chunk N is being written. Every caller that matters
  /// here moves bytes BETWEEN TWO STORAGES — a landing-zone drain down to capacity, a duplication
  /// heal onto a second member, a media move — so with one buffer the two devices took turns and
  /// the transfer cost read + write per chunk while each device was idle for the other's half. It
  /// now costs max(read, write), which is what "combine their throughput" means in practice: on a
  /// drain from a fast tier to a slow one the fast side stays ahead instead of waiting.
  ///
  /// The buffers are RENTED, not allocated: at 1 MiB each, allocating would put arrays straight on
  /// the Large Object Heap, and heal/drain/scrub run this once per file — a pool-wide heal would
  /// churn the LOH per file for no reason. They are transient and never retained, so pooling is safe.
  /// </summary>
  /// <param name="blockingSource">
  /// True when reading the source PARKS a thread for a network round trip. The read-ahead then runs
  /// on the engine's own bounded threads rather than the shared pool, for the same reason every
  /// other remote read does (see <see cref="BlockingIoScheduler"/>).
  /// </param>
  /// <param name="admit">
  /// Called with each chunk's size just before it is written, so the pool's own bulk transfers pass
  /// through the SAME per-device admission and rate limit as everything else.
  ///
  /// They did not, and that made <c>maxThroughput</c> miss the case it exists for. The limit is
  /// applied in <see cref="VolumeQueues.Enter"/>, which the engine's block paths call and this one
  /// never did — so a member the operator had told the pool to go easy on was still saturated by the
  /// pool's own drains, heals and media moves, which are the largest thing it ever does to a disk.
  /// Measured: a 24 MiB heal onto a member limited to 1 MiB/s completed in under six seconds.
  /// </param>
  public static long CopyCounted(Stream source, Stream destination, int bufferSize = CopyBufferSize, bool blockingSource = false,
    Action<long>? admit = null) {
    // A rented array is at LEAST the size asked for and routinely LARGER — the pool rounds up to
    // its bucket. So its Length is never the size we asked for, and using it would make the
    // transfer size depend on pool internals rather than on `bufferSize`, silently ignoring what
    // the caller specified. Every read and write below is pinned to the requested size instead.
    var front = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
    var back = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
    Task<int>? pending = null;
    try {
      long total = 0;
      var filled = _ReadFully(source, front, bufferSize);
      while (filled > 0) {
        // A short chunk means the source is genuinely EXHAUSTED — _ReadFully has already absorbed
        // the dribbling reads a network stream hands back — so there is nothing left to overlap
        // and no reason to pay for a thread. That covers every small file and every in-memory
        // source, which is most of the callers by count.
        pending = filled == bufferSize ? _ReadAhead(source, back, bufferSize, blockingSource) : null;
        admit?.Invoke(filled); // waits out the destination's rate limit before the chunk lands
        destination.Write(front, 0, filled);
        total += filled;
        if (pending == null)
          break;

        filled = pending.GetAwaiter().GetResult(); // the read-ahead's own exception surfaces here
        pending = null;
        (front, back) = (back, front);
      }

      return total;
    } finally {
      // An outstanding read still OWNS one of these buffers. Returning it to the pool while a
      // thread is filling it hands a live array to the next renter, which is the one bug a
      // double-buffered copy can introduce that a single-buffered one cannot.
      if (pending != null)
        try {
          pending.GetAwaiter().GetResult();
        } catch (Exception) {
          // the copy is already unwinding; this read's failure is not the one worth reporting
        }

      System.Buffers.ArrayPool<byte>.Shared.Return(front);
      System.Buffers.ArrayPool<byte>.Shared.Return(back);
    }
  }

  private static Task<int> _ReadAhead(Stream source, byte[] buffer, int count, bool blockingSource)
    => blockingSource
      ? Task.Factory.StartNew(() => _ReadFully(source, buffer, count), CancellationToken.None,
        TaskCreationOptions.DenyChildAttach, BlockingIoScheduler.Shared)
      : Task.Run(() => _ReadFully(source, buffer, count));

  /// <summary>
  /// Fills the buffer, or returns less only at the real end of the stream.
  ///
  /// One <see cref="Stream.Read(byte[],int,int)"/> is allowed to return fewer bytes than asked for
  /// at ANY point — a network source routinely hands back what one packet carried — so treating a
  /// short read as the end of the file is the classic way a copy loop truncates silently. It also
  /// matters for the overlap above: without this, "short chunk" would not mean "exhausted", and
  /// the read-ahead would have to be started for every dribble.
  /// </summary>
  private static int _ReadFully(Stream source, byte[] buffer, int count) {
    var total = 0;
    while (total < count) {
      var read = source.Read(buffer, total, count - total);
      if (read == 0)
        break;

      total += read;
    }

    return total;
  }

  /// <summary>Publishes a small, already-materialised payload (empty markers, checksum-verified small copies).</summary>
  public static void Publish(IVolumeIO member, string normalizedPath, bool shadow, byte[] content)
    => PublishStream(member, normalizedPath, shadow, () => new MemoryStream(content, false), content.LongLength);

  /// <summary>
  /// Streaming publish: opens the source lazily, copies it into a staged temp through a fixed
  /// buffer, and (where the backend supports it) atomically renames temp → final — never
  /// materialising the whole file. The temp is truncated first so a stale longer temp left by
  /// a previous interrupted publish can never leave a corrupt tail (SAFE-ATOMIC). Size is
  /// verified after publication; a mismatch throws before the caller completes its intent.
  /// </summary>
  public static void PublishStream(IVolumeIO member, string normalizedPath, bool shadow, Func<Stream> openSource, long? expectedLength = null, bool blockingSource = false,
    Action<long>? admit = null) {
    long written;
    if ((member.Caps & BackendCaps.AtomicRename) != 0) {
      var temp = normalizedPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
      using (var source = openSource())
      using (var stream = member.OpenWrite(temp, shadow, true)) {
        stream.SetLength(0); // never inherit a stale temp's tail
        written = CopyCounted(source, stream, blockingSource: blockingSource, admit: admit);
        stream.Flush();
      }

      member.AtomicReplace(temp, normalizedPath, shadow);
      _VerifySize(member, normalizedPath, shadow, expectedLength ?? written, written);
      return;
    }

    // no atomic rename (FTP/WebDAV-style): put whole, then verify the object landed intact
    using (var source = openSource())
    using (var stream = member.OpenWrite(normalizedPath, shadow, true)) {
      stream.SetLength(0);
      written = CopyCounted(source, stream, blockingSource: blockingSource, admit: admit);
      stream.Flush();
    }

    _VerifySize(member, normalizedPath, shadow, expectedLength ?? written, written);
  }

  private static void _VerifySize(IVolumeIO member, string normalizedPath, bool shadow, long expected, long written) {
    var meta = member.Stat(normalizedPath, shadow);
    if (meta == null || meta.Value.Length != expected || written != expected)
      throw new PoolFsException(PoolFsError.IoError,
        $"Publish of '{normalizedPath}' on '{member.DisplayName}' failed verification: wrote {written}, expected {expected}, on-disk {meta?.Length.ToString() ?? "missing"}");
  }

  /// <summary>
  /// Streams one physical copy onto another member (heal, drain, media ops, recovery resync):
  /// the whole file flows through a fixed buffer, temp + atomic rename on the target, never
  /// buffered in RAM — so a 40 GB file relocates in 1 MiB steps (SAFE-BIGFILE).
  /// </summary>
  public static void CopyBetween(IVolumeIO source, string sourcePath, bool sourceShadow, IVolumeIO target, string targetPath, bool targetShadow,
    Action<long>? admit = null) {
    var expected = source.Stat(sourcePath, sourceShadow)?.Length;
    PublishStream(target, targetPath, targetShadow, () => source.OpenRead(sourcePath, sourceShadow), expected,
      blockingSource: source.BlocksCallingThread, admit: admit);
  }

  /// <summary>
  /// Turns a per-member admission callback into the per-chunk one <see cref="CopyBetween"/> takes,
  /// charging BOTH ends of the copy.
  ///
  /// Charging only the target — which is what the drain and heal paths used to do — reads the
  /// operator's limit as "how fast may this disk be written", when what they set it for is "leave
  /// this disk some capacity for everything else". The disk an exchange empties, or the landing
  /// zone a drain reads from, is under exactly as much load as the one receiving, and it is very
  /// often the one that was limited: it is the tired member being evacuated. Charging both makes a
  /// copy run at the slower of the two allowances, which is the only reading under which a limit
  /// on a member actually bounds what that member does.
  ///
  /// A copy whose two ends are the same member (promoting a shadow in place) is charged once — the
  /// alternative silently halves the rate the operator asked for.
  /// </summary>
  public static Action<long>? Pace(Action<IVolumeIO, long>? admit, IVolumeIO source, IVolumeIO target) {
    if (admit == null)
      return null;

    return source.MemberId == target.MemberId
      ? bytes => admit(source, bytes)
      : bytes => {
        admit(source, bytes);
        admit(target, bytes);
      };
  }

  /// <summary>A member can hold an acknowledged durable copy only when its flush is a real durability barrier (SAFE-REMOTE).</summary>
  public static bool CanSatisfyAckQuorum(IVolumeIO member) => (member.Caps & BackendCaps.DurableFlush) != 0;

}
