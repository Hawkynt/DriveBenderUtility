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

  [Test]
  [Category("HappyPath")]
  [Description("A deleted file can be listed in the recycle bin and put back where it came from.")]
  public void Recover_GivenAFileWasDeleted_ThenItCanBeListedAndRestored() {
    // The engine has kept a recycle bin since deletes were first journalled, and until now nothing
    // could open it: no verb, no endpoint, no screen. Keeping the bytes is only half a recoverable
    // delete — the half that costs disk space. This is the other half.
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);
    var content = _Payload(64 * 1024, 611);
    var path = pool.PathTo("accounts/2026-q1.xlsx");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, content);

    File.Delete(path);
    File.Exists(path).Should().BeFalse("the delete must take it out of the namespace");

    // the bin is readable while the pool is mounted: listing touches nothing
    var listed = DbMount.RunExpectingSuccess(TimeSpan.FromMinutes(2), "pool-trash-list", pool.PoolName, "--json");
    listed.StandardOutput.Should().Contain("accounts/2026-q1.xlsx",
      $"a deleted file has to be FINDABLE before it can be recovered.{Environment.NewLine}{listed.Output}");

    // restoring writes to the members, so it runs with the pool unmounted — and says so if not
    var refused = DbMount.Run(TimeSpan.FromMinutes(2), "pool-trash-restore", pool.PoolName, "accounts/2026-q1.xlsx");
    refused.Succeeded.Should().BeFalse("restoring rewrites members, which a second engine must not do while mounted");

    pool.WhileUnmounted(() => DbMount.RunExpectingSuccess(TimeSpan.FromMinutes(2),
      "pool-trash-restore", pool.PoolName, "accounts/2026-q1.xlsx"));

    File.Exists(path).Should().BeTrue(
      $"the file must come back at its original path.{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
    File.ReadAllBytes(path).Should().Equal(content, "and with the bytes it was deleted with");

    var after = DbMount.RunExpectingSuccess(TimeSpan.FromMinutes(2), "pool-trash-list", pool.PoolName, "--json");
    after.StandardOutput.Should().NotContain("2026-q1.xlsx",
      $"a restored file is no longer in the bin — leaving it there would keep a second copy of every "
      + $"recovered file forever.{Environment.NewLine}{after.Output}");
  }

  [Test]
  [Category("HappyPath")]
  [Description("Through the manager, on a MOUNTED pool: a deleted file is listed and restored without unmounting anything.")]
  public void Recover_GivenThePoolIsMounted_ThenTheManagerListsAndRestoresIt() {
    // The journey a person actually takes. Everything else about the recycle bin is exercised with
    // the pool down; this is the case that matters, because a backup target is mounted, and it is
    // the only one that goes through the relay — the manager hands the work to the process that
    // owns the pool rather than opening the members itself.
    //
    // It also gets the better restore: the engine's, which puts the file back AND re-establishes
    // its duplication level, where the offline CLI path can only return it as a single copy.
    using var daemon = ManagementDaemon.Start();
    using var pool = MountedPool.Create(members: 2, poolDefaults: _TRASH_ON);

    var content = _Payload(96 * 1024, 612);
    var path = pool.PathTo("ledgers/2026-invoices.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, content);
    MountedPool.WaitUntil(() => pool.PhysicalCopies("ledgers/2026-invoices.db").Count >= 2, TimeSpan.FromMinutes(2))
      .Should().BeTrue($"the file must start duplicated.{Environment.NewLine}{pool.DescribeMembers()}");

    File.Delete(path);

    var listed = daemon.GetJson($"api/pool/trash?pool={pool.PoolName}");
    listed.GetProperty("ok").GetBoolean().Should().BeTrue($"the bin of a mounted pool must be readable: {listed}");
    var entries = listed.GetProperty("result").GetProperty("entries");
    entries.EnumerateArray().Select(e => e.GetProperty("path").GetString())
      .Should().Contain("ledgers/2026-invoices.db",
        $"the manager has to find the deleted file before anyone can ask for it back: {listed}");

    var restored = daemon.PostJson(
      $"api/pool/trash/restore?pool={pool.PoolName}&path={Uri.EscapeDataString("ledgers/2026-invoices.db")}");
    restored.GetProperty("ok").GetBoolean().Should().BeTrue($"the restore must succeed on a mounted pool: {restored}");

    File.Exists(path).Should().BeTrue(
      $"the file must be back at its original path, in a pool that never stopped serving."
      + $"{Environment.NewLine}{pool.DescribeMembers()}{Environment.NewLine}{pool.MountLog}");
    File.ReadAllBytes(path).Should().Equal(content, "with the bytes it was deleted with");

    MountedPool.WaitUntil(() => pool.PhysicalCopies("ledgers/2026-invoices.db").Count >= 2, TimeSpan.FromMinutes(2))
      .Should().BeTrue(
        $"and back at its duplication level: a recovered file that is one bad sector from being lost "
        + $"again is only half recovered.{Environment.NewLine}{pool.DescribeMembers()}");
  }

}
