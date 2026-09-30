using System.Text.Json;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Space savings through the real driver on a real disk (docs/SpaceSavings.md): identical files come
/// to share their data and runs of zeros are released — and writing one of two shared files through
/// the pool still leaves the other exactly as it was.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class SpaceSavingEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(3);

  [Test]
  [Category("HappyPath")]
  public void Space_GivenIdenticalFilesAndARunOfZeros_ThenAFixSavesSpaceAndWritingOneTwinLeavesTheOtherAlone() {
    using var pool = MountedPool.Create(members: 1);
    var content = new byte[512 * 1024];
    new Random(42).NextBytes(content);
    var stamp = new DateTime(2020, 5, 17, 8, 30, 0, DateTimeKind.Utc);
    foreach (var name in new[] { "photos/original.raw", "backup/copy.raw" }) {
      Directory.CreateDirectory(Path.GetDirectoryName(pool.PathTo(name))!);
      File.WriteAllBytes(pool.PathTo(name), content);
      File.SetCreationTimeUtc(pool.PathTo(name), stamp);
      File.SetLastWriteTimeUtc(pool.PathTo(name), stamp); // same content AND same metadata: may be hard-linked
    }

    var image = new byte[5 * 1024 * 1024];
    image[0] = 1;
    image[^1] = 2;
    File.WriteAllBytes(pool.PathTo("disk.img"), image);

    string output = "";
    pool.WhileUnmounted(() => output = DbMount.RunExpectingSuccess(_CLI, "pool-health", pool.PoolName, "--fix", "--json").StandardOutput);
    var json = JsonDocument.Parse(output.Trim().Split('\n').Last(l => l.TrimStart().StartsWith('{'))).RootElement;
    var saved = json.GetProperty("spaceSaved");
    TestContext.Out.WriteLine($"[space] {saved}");

    saved.GetProperty("filesDeduplicated").GetInt32().Should().Be(1, $"the two identical files share their data now.{Environment.NewLine}{output}");
    saved.GetProperty("bytesReleased").GetInt64().Should().BeGreaterThanOrEqualTo(3L * 1024 * 1024, "the zeros in the middle of the image are released");

    // copy-on-write through the pool: write one twin, the other must not move
    using (var stream = new FileStream(pool.PathTo("backup/copy.raw"), FileMode.Open, FileAccess.ReadWrite)) {
      stream.Position = 100;
      stream.Write([0xEE, 0xEE, 0xEE]);
    }

    File.ReadAllBytes(pool.PathTo("photos/original.raw")).Should().Equal(content,
      $"writing one of two shared files must never reach the other.{Environment.NewLine}{pool.MountLog}");
    File.ReadAllBytes(pool.PathTo("backup/copy.raw"))[100].Should().Be(0xEE);
    File.GetLastWriteTimeUtc(pool.PathTo("photos/original.raw")).Should().Be(stamp, "and its time did not move either");
    File.ReadAllBytes(pool.PathTo("disk.img")).Should().Equal(image, "a released range reads back as the zeros it held");
  }

}
