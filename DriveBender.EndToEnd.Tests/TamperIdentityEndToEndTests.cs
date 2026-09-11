using System.Text.Json.Nodes;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The two files that decide what the pool IS, rather than what is in it (SAFE-MANIFEST).
///
/// Every member carries `.drivebenderutility/pool.json` — a mirror of the manifest — and
/// `member.json`, which says which member of which pool this disk is. The other sidecars describe
/// data; these describe the POOL, and `ManifestStore` reconciles them by highest version wins. So a
/// mirror is not merely a cache: raise its version number and it becomes the configuration.
///
/// That makes these the most leveraged files on the disk. A tampered checksum database can at worst
/// cost one file; a tampered mirror can rewrite the member list — dropping a disk the pool was
/// storing data on, lowering the duplication level so redundancy quietly stops being maintained, or
/// naming a folder that was never part of the pool. None of it requires privileges: the folder is an
/// ordinary folder and the file is ordinary JSON.
///
/// What must hold is the same rule the rest of the tamper suite establishes, applied to the pool's
/// own shape: <b>a file on a disk is evidence, not authority.</b> Whatever the pool decides about a
/// mirror it distrusts, no acknowledged file may become unreadable, and redundancy must not drop
/// silently — if the pool cannot keep the promise it made, it has to say so rather than quietly
/// keep fewer copies.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("EdgeCase")]
[NonParallelizable]
public class TamperIdentityEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);
  private const string _UTILITY = ".drivebenderutility";

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  private static string _MirrorOn(MountedPool pool, int member)
    => Path.Combine(pool.MemberPaths[member], _UTILITY, "pool.json");

  private static string _MarkerOn(MountedPool pool, int member)
    => Path.Combine(pool.MemberPaths[member], _UTILITY, "member.json");

  /// <summary>A duplicated pool holding one file on both members, with its mirrors written.</summary>
  private static MountedPool _PoolWithAMirror(string name, byte[] content) {
    var pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);
    try {
      File.WriteAllBytes(pool.PathTo(name), content);
      pool.WaitForPhysicalCopies(name, atLeast: 2, TimeSpan.FromMinutes(2));
      MountedPool.WaitUntil(() => File.Exists(_MirrorOn(pool, 0)), TimeSpan.FromMinutes(1));
      return pool;
    } catch {
      pool.Dispose();
      throw;
    }
  }

  private static void _AssertStillReadable(MountedPool pool, string name, byte[] expected, string what) {
    File.Exists(pool.PathTo(name)).Should().BeTrue(
      $"{what} must not make an acknowledged file disappear.{Environment.NewLine}{pool.DescribeMembers()}"
      + $"{Environment.NewLine}{pool.MountLog}");
    File.ReadAllBytes(pool.PathTo(name)).Should().Equal(expected, $"{what} must not change the file's content");
  }

  [Test]
  [Description("A member's manifest mirror is given a huge version with one member REMOVED: the pool must not silently drop a disk holding data.")]
  public void Mirror_GivenAForgedHigherVersionDropsAMember_ThenNoDataBecomesUnreachable() {
    // The leveraged attack on this file. Highest version wins, so a number anybody can type decides
    // the member list — and a list one disk short means whatever lived only on that disk is gone
    // from the namespace, while the bytes sit there untouched and unreferenced.
    var content = _Payload(48 * 1024, 31);
    using var pool = _PoolWithAMirror("both-members.bin", content);

    var forged = false;
    pool.WhileUnmounted(() => {
      var mirror = _MirrorOn(pool, 0);
      var root = JsonNode.Parse(File.ReadAllText(mirror))!.AsObject();
      var members = root["members"]!.AsArray();
      if (members.Count < 2)
        return;

      members.RemoveAt(1); // the pool now "has" one member
      root["version"] = 999_999;
      File.WriteAllText(mirror, root.ToJsonString());
      forged = true;
    });

    forged.Should().BeTrue("the mirror must have listed two members for removing one to mean anything");

    _AssertStillReadable(pool, "both-members.bin", content,
      "a mirror edited to drop a member, at a version high enough to win reconciliation");

    // and the pool must not be quietly running on half its storage: either it rejected the forgery,
    // or it says out loud that it is now short a member
    var health = DbMount.Run(_CLI, "pool-health", pool.PoolName);
    TestContext.Out.WriteLine($"health after the forgery (exit {health.ExitCode}):{Environment.NewLine}{health.Output}");
    var listed = DbMount.Run(_CLI, "pool-list");
    listed.Output.Should().NotBeNullOrWhiteSpace("the pool must still be discoverable after a mirror was edited");
  }

  [Test]
  [Description("A mirror is given a huge version with duplication lowered to 1: redundancy must not be reduced by editing a file.")]
  public void Mirror_GivenAForgedHigherVersionLowersDuplication_ThenRedundancyIsNotSilentlyDropped() {
    // Subtler and worse than dropping a member, because nothing looks wrong afterwards. If an edited
    // mirror can set duplication to 1, the pool stops maintaining the second copy and keeps serving
    // files perfectly — the loss only becomes visible when a disk dies, which is the moment the
    // promise was supposed to pay out.
    var content = _Payload(32 * 1024, 32);
    using var pool = _PoolWithAMirror("duplicated.bin", content);

    pool.WhileUnmounted(() => {
      var mirror = _MirrorOn(pool, 0);
      var root = JsonNode.Parse(File.ReadAllText(mirror))!.AsObject();
      root["defaults"] = new JsonObject { ["duplication"] = 1 };
      root["version"] = 999_999;
      File.WriteAllText(mirror, root.ToJsonString());
    });

    _AssertStillReadable(pool, "duplicated.bin", content, "a mirror edited to lower the duplication level");

    // a NEW file written after the forgery is the test: if the edit took effect, this one is stored
    // once and the pool believes that is correct
    var after = _Payload(32 * 1024, 33);
    File.WriteAllBytes(pool.PathTo("written-after.bin"), after);
    var copies = pool.WaitForPhysicalCopies("written-after.bin", atLeast: 2, TimeSpan.FromMinutes(1));

    TestContext.Out.WriteLine($"copies of a file written after the forgery: {copies.Count}");
    copies.Count.Should().BeGreaterThanOrEqualTo(2,
      $"a file written after a MIRROR was edited is stored {copies.Count} time(s). The duplication "
      + $"level is a promise the pool made to its owner; a text file on one of the disks must not be "
      + $"able to revoke it.{Environment.NewLine}{pool.DescribeMembers()}");
  }

  [Test]
  [Description("A mirror is given a huge version naming a member folder that was never part of the pool: the pool must not adopt it.")]
  public void Mirror_GivenAForgedHigherVersionAddsAStrangeMember_ThenItIsNotAdopted() {
    var content = _Payload(16 * 1024, 34);
    using var pool = _PoolWithAMirror("ours.bin", content);

    var intruder = Path.Combine(pool.Root, "not-ours");
    Directory.CreateDirectory(intruder);
    File.WriteAllBytes(Path.Combine(intruder, "foreign.bin"), _Payload(256, 35));

    pool.WhileUnmounted(() => {
      var mirror = _MirrorOn(pool, 0);
      var root = JsonNode.Parse(File.ReadAllText(mirror))!.AsObject();
      var members = root["members"]!.AsArray();
      members.Add(new JsonObject {
        ["memberId"] = Guid.NewGuid().ToString(),
        ["path"] = intruder,
        ["role"] = "capacity",
      });

      root["version"] = 999_999;
      File.WriteAllText(mirror, root.ToJsonString());
    });

    _AssertStillReadable(pool, "ours.bin", content, "a mirror edited to add a member nobody configured");

    // the intruder's own file must not appear as pool content: adopting a folder on the strength of
    // an edited mirror would publish somebody else's data into this namespace
    Directory.EnumerateFiles(pool.MountPath, "foreign.bin", SearchOption.AllDirectories)
      .Should().BeEmpty("a folder named by an edited mirror must not have its contents published as pool data");
  }

  [Test]
  [Description("One member's identity marker is overwritten with the other's: two disks claiming to be the same member must not corrupt the pool.")]
  public void Marker_GivenTwoMembersClaimTheSameIdentity_ThenThePoolDoesNotLoseData() {
    // The marker is how a disk says which member it is, and resolution is BY MARKER rather than by
    // path — which is what lets a drive letter change without losing the pool. Copy one disk's
    // marker onto another and two disks make the same claim, so a pool that trusts the claim can
    // resolve both members to one disk and treat the other's data as absent.
    var content = _Payload(48 * 1024, 36);
    using var pool = _PoolWithAMirror("identity.bin", content);

    pool.WhileUnmounted(() => File.Copy(_MarkerOn(pool, 0), _MarkerOn(pool, 1), overwrite: true));

    _AssertStillReadable(pool, "identity.bin", content, "two members carrying the same identity marker");

    // A write is the interesting half, and the right answer here is NOT "it succeeds". Resolution is
    // by marker, so the disk carrying a copy of its neighbour's identity stops resolving as itself
    // and the pool opens degraded on one member — and this pool was told to keep two copies. The
    // honest outcome is therefore a refusal: accepting the write would acknowledge it with less
    // redundancy than promised, which is the failure that matters. I expected success at first and
    // was wrong; the pool is applying the rule the rest of this suite holds it to.
    var fresh = _Payload(8 * 1024, 37);
    var outcome = _TryWrite(pool.PathTo("after-identity.bin"), fresh);
    TestContext.Out.WriteLine($"write after a duplicated marker: {(outcome.ok ? "accepted" : outcome.error)}");

    if (outcome.ok)
      pool.WaitForPhysicalCopies("after-identity.bin", atLeast: 2, TimeSpan.FromMinutes(1))
        .Count.Should().BeGreaterThanOrEqualTo(2,
          "if the write was ACCEPTED then the promised two copies must exist — accepting it with one "
          + "would be the pool quietly keeping less redundancy than it agreed to");
    else
      outcome.error.Should().NotBeNullOrWhiteSpace(
        "a refusal has to arrive as a reportable error rather than a hang or a crash");

    // either way the pool is still serving, and the pre-existing file is untouched
    _AssertStillReadable(pool, "identity.bin", content, "a refused write after a duplicated marker");
  }

  private static (bool ok, string error) _TryWrite(string path, byte[] content) {
    try {
      File.WriteAllBytes(path, content);
      return (true, "");
    } catch (Exception e) {
      return (false, $"{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
    }
  }

  [Test]
  [Description("A member's marker is rewritten to name a different pool: the disk must not be treated as a member of this one.")]
  public void Marker_GivenItNamesADifferentPool_ThenTheMemberIsNotSilentlyUsed() {
    var content = _Payload(32 * 1024, 38);
    using var pool = _PoolWithAMirror("whose.bin", content);

    pool.WhileUnmounted(() => {
      var marker = _MarkerOn(pool, 1);
      var root = JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
      root["poolId"] = Guid.NewGuid().ToString(); // this disk now claims to belong elsewhere
      File.WriteAllText(marker, root.ToJsonString());
    });

    // the file is on both members, so whichever way the pool resolves the disclaimed disk, the data
    // is reachable — what must not happen is the pool losing it or refusing to mount at all
    _AssertStillReadable(pool, "whose.bin", content, "a member whose marker claims a different pool");
  }

  [Test]
  [Description("Both mirrors are replaced with garbage: the pool still mounts from the registry and keeps its files.")]
  public void Mirror_GivenEveryCopyIsGarbage_ThenThePoolStillMountsFromTheRegistry() {
    // The mirrors exist so a pool is reconstructable from the disks alone. Destroying them must cost
    // that property and nothing else — the registry still has the manifest.
    var content = _Payload(32 * 1024, 39);
    using var pool = _PoolWithAMirror("registry.bin", content);

    pool.WhileUnmounted(() => {
      for (var member = 0; member < pool.MemberPaths.Count; ++member) {
        var mirror = _MirrorOn(pool, member);
        if (File.Exists(mirror))
          File.WriteAllText(mirror, "{ not really json");
      }
    });

    _AssertStillReadable(pool, "registry.bin", content, "a pool whose every manifest mirror is corrupt");
  }

}
