using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The recoverable delete (FR-TRASH, §6.14): with <c>trash.enabled</c> a deleted file's bytes are
/// moved into a hidden per-member trash tree instead of being destroyed.
///
/// Opt-in, off by default, and until now with no end-to-end cover at all — which matters more here
/// than for most settings, because the whole point of it is to be there on the day somebody deletes
/// the wrong thing. A feature nobody has watched work is not a safety net.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class TrashEndToEndTests {

  /// <summary>Deletes are recoverable, and one copy is enough to recover from.</summary>
  private const string _TRASH_ON =
    """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "trash": { "enabled": true, "retention": "7d", "maxSize": "50%", "dropDuplicatesInTrash": true } }""";

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  /// <summary>Every file under a member's hidden trash tree, with its bytes.</summary>
  private static List<(string where, byte[] content)> _InTheTrash(MountedPool pool) {
    var found = new List<(string, byte[])>();
    foreach (var member in pool.MemberPaths) {
      var trash = Path.Combine(member, ".drivebenderutility", "trash");
      if (!Directory.Exists(trash))
        continue;

      foreach (var file in Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories)) {
        // each trashed file is a pair: the data at "<name>.<stamp>.trashver" and a ".trashinfo"
        // sidecar recording what it used to be called. Only the first is the file.
        if (file.EndsWith(".trashinfo", StringComparison.OrdinalIgnoreCase))
          continue;

        try {
          using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
          using var buffer = new MemoryStream();
          stream.CopyTo(buffer);
          found.Add((file, buffer.ToArray()));
        } catch (IOException) {
          // being written; the caller polls
        }
      }
    }

    return found;
  }

  [Test]
  [Category("HappyPath")]
  [Description("With the trash on, a deleted file leaves the pool but its bytes are kept, whole, on a member.")]
  public void Trash_GivenItIsEnabled_ThenADeletedFilesBytesAreKeptIntact() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);
    var content = _Payload(512 * 1024, 1);

    File.WriteAllBytes(pool.PathTo("precious.bin"), content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("precious.bin").Count >= 2, TimeSpan.FromMinutes(2));

    File.Delete(pool.PathTo("precious.bin"));

    // gone from the pool the user sees…
    File.Exists(pool.PathTo("precious.bin")).Should().BeFalse(
      $"a deleted file must leave the namespace whatever happens to its bytes.{Environment.NewLine}{pool.MountLog}");
    pool.PhysicalCopies("precious.bin").Should().BeEmpty("and it must not still be sitting at its old path");

    // …and kept, byte for byte, where it can be recovered from
    var trashed = MountedPool.WaitUntil(() => _InTheTrash(pool).Count > 0, TimeSpan.FromMinutes(1))
      ? _InTheTrash(pool)
      : _InTheTrash(pool);

    trashed.Should().NotBeEmpty(
      $"the whole point of the trash is that the bytes survive the delete."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");

    foreach (var (where, kept) in trashed)
      kept.Should().Equal(content,
        $"the copy kept at {where} must be the file as it was — a truncated or torn one is not a recovery");
  }

  [Test]
  [Category("HappyPath")]
  [Description("With the trash off — the default — a delete really is permanent and leaves nothing behind.")]
  public void Trash_GivenItIsOff_ThenADeleteIsPermanent() {
    // the default, and worth pinning from this side too: a pool that quietly retained every deleted
    // file would fill its members up for a reason the operator never asked for
    using var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    var content = _Payload(256 * 1024, 2);

    File.WriteAllBytes(pool.PathTo("temporary.bin"), content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("temporary.bin").Count >= 2, TimeSpan.FromMinutes(2));

    File.Delete(pool.PathTo("temporary.bin"));

    File.Exists(pool.PathTo("temporary.bin")).Should().BeFalse();
    MountedPool.WaitUntil(() => pool.PhysicalCopies("temporary.bin").Count == 0, TimeSpan.FromSeconds(30));
    pool.PhysicalCopies("temporary.bin").Should().BeEmpty("every copy goes when the trash is off");

    _InTheTrash(pool).Should().BeEmpty(
      $"nothing may be retained when the operator did not ask for a trash."
      + $"{Environment.NewLine}{pool.DescribeMembers()}");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A trashed file's name is free again immediately: creating a new file at the same path is not confused by the deleted one.")]
  public void Trash_GivenAFileWasTrashed_ThenItsNameCanBeUsedAgainAtOnce() {
    // the trash moves bytes aside under a name of its own, and if any of that leaked back into the
    // pool's namespace a recreated file would read as the deleted one — which is the same class of
    // fault as a resurrected delete, arriving from the other direction
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);

    var first = _Payload(128 * 1024, 3);
    File.WriteAllBytes(pool.PathTo("reused.bin"), first);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("reused.bin").Count >= 2, TimeSpan.FromMinutes(2));
    File.Delete(pool.PathTo("reused.bin"));

    var second = _Payload(128 * 1024, 4);
    var writing = () => File.WriteAllBytes(pool.PathTo("reused.bin"), second);
    writing.Should().NotThrow(
      $"the name is free the moment the file is deleted, trash or no trash.{Environment.NewLine}{pool.MountLog}");

    File.ReadAllBytes(pool.PathTo("reused.bin")).Should().Equal(second,
      "and the new file must be the NEW content — never the one that went to the trash");

    // and the trashed bytes are still the OLD ones, not overwritten by the reuse
    foreach (var (where, kept) in _InTheTrash(pool))
      kept.Should().Equal(first, $"what was trashed at {where} must still be what was deleted");
  }

}
