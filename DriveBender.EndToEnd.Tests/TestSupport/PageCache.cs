using System.Runtime.InteropServices;

namespace DivisonM.EndToEnd.Tests.TestSupport;

/// <summary>
/// Evicts a file's contents from the HOST's page cache.
///
/// Any scenario that claims to measure a device is measuring RAM until this runs. Write 32 MiB to
/// an SD card and the kernel keeps every byte of it in memory; the read that follows never touches
/// the card and comes back at gigabytes per second, so an assertion like "reads are not held to the
/// slow disk's pace" passes on a machine where the engine does exactly the wrong thing. Dropping the
/// cache first is what makes the number mean the device.
///
/// <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> does this per file and needs no privileges, unlike
/// <c>/proc/sys/vm/drop_caches</c>. It only evicts CLEAN pages, so the file must be flushed first —
/// which for a pool member means doing this while nothing is mounted.
/// </summary>
public static class PageCache {

  private const int _POSIX_FADV_DONTNEED = 4;

  [DllImport("libc", SetLastError = true)]
  private static extern int posix_fadvise(int fd, long offset, long len, int advice);

  [DllImport("libc")]
  private static extern void sync();

  /// <summary>Whether this platform can evict at all; scenarios that measure a device need it.</summary>
  public static bool CanDrop => !OperatingSystem.IsWindows();

  /// <summary>
  /// Drops every regular file under <paramref name="directory"/> from the page cache, and returns
  /// how many were dropped — zero means nothing was measured against a real device.
  /// </summary>
  public static int DropTree(string directory) {
    if (!CanDrop || !Directory.Exists(directory))
      return 0;

    // DONTNEED skips DIRTY pages, so anything still waiting to be written back would survive the
    // eviction and serve the next read out of RAM regardless. One sync settles the whole tree.
    try {
      sync();
    } catch (Exception) {
      // no libc sync here; the fadvise below is still worth attempting
    }

    var dropped = 0;
    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
      if (Drop(file))
        ++dropped;

    return dropped;
  }

  /// <summary>Drops one file from the page cache. False when the platform cannot, or the file is gone.</summary>
  public static bool Drop(string path) {
    if (!CanDrop)
      return false;

    try {
      using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      var descriptor = handle.DangerousGetHandle().ToInt32(); // on Unix the SafeFileHandle IS the fd
      return posix_fadvise(descriptor, 0, 0, _POSIX_FADV_DONTNEED) == 0;
    } catch (Exception) {
      return false;
    }
  }

}
