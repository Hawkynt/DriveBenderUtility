using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Swapping a disk out by way of an IDLE member: the new disk joins the pool as idle, takes nothing
/// while the pool carries on, and receives the old disk's files when the old disk is retired.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class IdleMemberEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);

  private const string _SINGLE_COPY = """{ "duplication": 1, "placement": { "shadowNeverSamePhysical": false } }""";

  private static byte[] _Payload(int length, int seed) {
    var content = new byte[length];
    new Random(seed).NextBytes(content);
    return content;
  }

  private static string[] _PoolFilesIn(string folder)
    => [.. Directory.EnumerateFiles(folder, "*.bin", SearchOption.AllDirectories)
      .Where(f => !f.Contains(".drivebenderutility", StringComparison.OrdinalIgnoreCase))
      .Select(Path.GetFileName)!];

  [Test]
  [Category("HappyPath")]
  [Description("A disk joined as idle takes no new files, and retiring another disk moves that disk's files onto it.")]
  public void Idle_GivenADiskJoinedAsIdle_ThenItTakesNothingUntilAnotherDiskIsRetiredOntoIt() {
    using var pool = MountedPool.Create(members: 2, poolDefaults: _SINGLE_COPY);
    var idle = Path.Combine(pool.Root, "idle-disk");
    Directory.CreateDirectory(idle);
    pool.WhileUnmounted(() => DbMount.RunExpectingSuccess(_CLI, "pool-add-member", pool.PoolName, "-m", idle, "--role", "idle"));

    var contents = new Dictionary<string, byte[]>();
    for (var i = 0; i < 12; ++i) {
      var name = $"doc{i}.bin";
      contents[name] = _Payload(24 * 1024, 700 + i);
      File.WriteAllBytes(pool.PathTo(name), contents[name]);
    }

    _PoolFilesIn(idle).Should().BeEmpty(
      $"an idle disk takes no new files — it is waiting for a disk to be retired onto it.{Environment.NewLine}{pool.DescribeMembers()}");

    // retire whichever of the two original disks holds more
    var retiring = pool.MemberPaths.OrderByDescending(m => _PoolFilesIn(m).Length).First();
    var retiredFiles = _PoolFilesIn(retiring);
    retiredFiles.Should().NotBeEmpty("the retired disk must hold files for this to show anything");
    var staying = pool.MemberPaths.Single(m => m != retiring);
    var stayingBefore = _PoolFilesIn(staying);

    pool.WhileUnmounted(() => DbMount.RunExpectingSuccess(_CLI, "pool-remove-media", pool.PoolName, "--member", retiring));

    _PoolFilesIn(idle).Should().BeEquivalentTo(retiredFiles,
      $"the retired disk's files go onto the idle disk, which is what it joined for.{Environment.NewLine}{pool.DescribeMembers()}");
    _PoolFilesIn(staying).Should().BeEquivalentTo(stayingBefore, "the other capacity disk is left as it was");

    foreach (var (name, content) in contents)
      File.ReadAllBytes(pool.PathTo(name)).Should().Equal(content,
        $"'{name}' must be served whole after the swap.{Environment.NewLine}{pool.MountLog}");
  }

}
