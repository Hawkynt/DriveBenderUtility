namespace DivisonM.Vfs.Engine;

/// <summary>
/// Publishes whole-file content honouring backend capability gaps (FR-CAP-ADAPT): a
/// backend with <see cref="BackendCaps.AtomicRename"/> gets temp + rename (SAFE-ATOMIC);
/// one without gets a direct write followed by a read-back verification — the journal
/// intent open around the call covers the non-atomic gap.
/// </summary>
public static class WholeFilePublisher {

  public static void Publish(IVolumeIO member, string normalizedPath, bool shadow, byte[] content) {
    if ((member.Caps & BackendCaps.AtomicRename) != 0) {
      var temp = normalizedPath + "." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
      using (var stream = member.OpenWrite(temp, shadow, true)) {
        stream.Write(content, 0, content.Length);
        stream.Flush();
      }

      member.AtomicReplace(temp, normalizedPath, shadow);
      return;
    }

    // no atomic rename (FTP/WebDAV-style): put whole, then verify the object landed intact
    using (var stream = member.OpenWrite(normalizedPath, shadow, true)) {
      stream.SetLength(0);
      stream.Write(content, 0, content.Length);
      stream.Flush();
    }

    var meta = member.Stat(normalizedPath, shadow);
    if (meta == null || meta.Value.Length != content.Length)
      throw new PoolFsException(PoolFsError.IoError, $"Put-and-verify failed on '{member.DisplayName}' for '{normalizedPath}': size mismatch after upload");
  }

  /// <summary>A member can hold an acknowledged durable copy only when its flush is a real durability barrier (SAFE-REMOTE).</summary>
  public static bool CanSatisfyAckQuorum(IVolumeIO member) => (member.Caps & BackendCaps.DurableFlush) != 0;

}
