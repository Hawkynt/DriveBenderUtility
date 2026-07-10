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

  /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/> through a fixed buffer and returns the byte count.</summary>
  public static long CopyCounted(Stream source, Stream destination, int bufferSize = CopyBufferSize) {
    var buffer = new byte[bufferSize];
    long total = 0;
    int read;
    while ((read = source.Read(buffer, 0, buffer.Length)) > 0) {
      destination.Write(buffer, 0, read);
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
  public static void PublishStream(IVolumeIO member, string normalizedPath, bool shadow, Func<Stream> openSource, long? expectedLength = null) {
    long written;
    if ((member.Caps & BackendCaps.AtomicRename) != 0) {
      var temp = normalizedPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
      using (var source = openSource())
      using (var stream = member.OpenWrite(temp, shadow, true)) {
        stream.SetLength(0); // never inherit a stale temp's tail
        written = CopyCounted(source, stream);
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
      written = CopyCounted(source, stream);
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
  public static void CopyBetween(IVolumeIO source, string sourcePath, bool sourceShadow, IVolumeIO target, string targetPath, bool targetShadow) {
    var expected = source.Stat(sourcePath, sourceShadow)?.Length;
    PublishStream(target, targetPath, targetShadow, () => source.OpenRead(sourcePath, sourceShadow), expected);
  }

  /// <summary>A member can hold an acknowledged durable copy only when its flush is a real durability barrier (SAFE-REMOTE).</summary>
  public static bool CanSatisfyAckQuorum(IVolumeIO member) => (member.Caps & BackendCaps.DurableFlush) != 0;

}
