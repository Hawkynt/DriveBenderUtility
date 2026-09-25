using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Saving a mounted pool's settings from the dashboard, the way the page does it.
///
/// Reported from real use: saving settings in the UI lost the connection to the service. Saving a
/// mounted pool writes the manifest and asks the running mount to reload live — two processes and
/// a live stream are involved, and nothing covered them together: the API tier never mounts, and
/// the mount tier never goes through the daemon.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[Category("ManagementApi")]
[NonParallelizable]
public class SettingsSaveEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(2);

  private ManagementDaemon _daemon = null!;
  private string _poolName = "";
  private string _root = "";
  private string _mountPath = "";

  [SetUp]
  public void MountThroughTheDaemon() {
    DriverPrerequisite.RequireAvailable();
    this._root = Path.Combine(Path.GetTempPath(), "dbe2e-settings-" + Guid.NewGuid().ToString("N")[..8]);
    var members = new[] { Path.Combine(this._root, "m0"), Path.Combine(this._root, "m1") };
    foreach (var member in members)
      Directory.CreateDirectory(member);

    this._poolName = "e2eset" + Guid.NewGuid().ToString("N")[..8];
    DbMount.RunExpectingSuccess(_CLI, "pool-create", "-n", this._poolName, "-m", members[0], "-m", members[1]);

    string target;
    if (OperatingSystem.IsWindows()) {
      var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
      var letter = "XYZWVUTSRQPONM".First(l => !taken.Contains(l));
      target = $"{letter}:";
      this._mountPath = $"{letter}:\\";
    } else {
      target = this._mountPath = Path.Combine(this._root, "mnt");
      Directory.CreateDirectory(target);
    }

    this._daemon = ManagementDaemon.Start();
    var mounted = this._daemon.PostJson($"api/pool/mount?pool={this._poolName}&target={Uri.EscapeDataString(target)}");
    mounted.GetProperty("ok").GetBoolean().Should().BeTrue($"the daemon must mount the pool: {mounted}{Environment.NewLine}{this._daemon.Log}");
  }

  [TearDown]
  public void Tidy() {
    try {
      this._daemon?.PostJson($"api/pool/unmount?pool={this._poolName}");
    } catch (Exception) {
      // teardown is best effort
    }

    this._daemon?.Dispose();
    if (this._poolName.Length > 0) {
      DbMount.Run(_CLI, "unmount", this._mountPath);
      DbMount.ForgetPool(this._poolName);
    }

    try {
      Directory.Delete(this._root, true);
    } catch (Exception) {
      // best effort
    }
  }

  private bool _IsMounted() {
    var frame = this._daemon.GetJson("api/pools");
    return frame.GetProperty("pools").EnumerateArray()
      .Any(p => p.GetProperty("name").GetString() == this._poolName
                && p.TryGetProperty("mounted", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String && m.GetString()!.Length > 0);
  }

  [Test]
  [Category("HappyPath")]
  [Description("Settings are saved several times on a mounted pool that is being written to: the live stream stays up, the API keeps answering, the pool stays mounted and the writes keep succeeding.")]
  public void Save_GivenAMountedPoolUnderWrites_ThenNeitherTheServiceNorTheMountIsLost() {
    using var watch = this._daemon.WatchStream();

    // a copy in progress while the user changes a setting — the realistic moment to save
    var writeErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
    var stopWriting = false;
    var written = 0;
    var writer = new Thread(() => {
      var payload = new byte[16 * 1024];
      new Random(3).NextBytes(payload);
      while (!Volatile.Read(ref stopWriting))
        try {
          File.WriteAllBytes(Path.Combine(this._mountPath, $"during-save-{written:0000}.bin"), payload);
          ++written;
        } catch (Exception e) {
          writeErrors.Add($"{e.GetType().Name}: {e.Message}");
          Thread.Sleep(100);
        }
    }) { IsBackground = true };
    writer.Start();

    Thread.Sleep(2000);
    watch.Frames.Should().BeGreaterThan(0, "the stream has to be flowing before a drop means anything");

    // exactly what the Settings dialog sends: the current block, one value changed, posted whole
    var outcomes = new List<string>();
    for (var round = 0; round < 4; ++round) {
      var config = this._daemon.GetJson($"api/pool/config?pool={this._poolName}");
      var current = config.GetProperty("result").GetProperty("current").GetString();
      var json = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(current) ? "{}" : current)!.AsObject();
      json["readAhead"] = new System.Text.Json.Nodes.JsonObject { ["enabled"] = round % 2 == 0 };
      json["cache"] = new System.Text.Json.Nodes.JsonObject { ["size"] = round % 2 == 0 ? "256MiB" : "512MiB" };

      var saved = this._daemon.PostJson($"api/pool/config?pool={this._poolName}", new { json = json.ToJsonString() });
      outcomes.Add(saved.ToString());
      saved.GetProperty("ok").GetBoolean().Should().BeTrue($"the save must be accepted: {saved}");
      Thread.Sleep(2500);
    }

    Volatile.Write(ref stopWriting, true);
    writer.Join(TimeSpan.FromSeconds(30));
    var framesAfter = watch.Frames;
    Thread.Sleep(2500);

    var context = $"{Environment.NewLine}saves: {string.Join(" | ", outcomes)}"
                  + $"{Environment.NewLine}files written during the saves: {written}"
                  + $"{Environment.NewLine}{this._daemon.Log}";

    watch.Drops.Should().Be(0, $"the page shows \"reconnecting…\" whenever the live stream drops; last error: {watch.LastError}{context}");
    watch.Frames.Should().BeGreaterThan(framesAfter, $"the stream must still be delivering after the last save{context}");
    this._IsMounted().Should().BeTrue($"saving settings must not unmount the pool{context}");
    writeErrors.Should().BeEmpty($"writes in flight must survive a live reload{context}");
    File.WriteAllBytes(Path.Combine(this._mountPath, "after-the-saves.bin"), [1, 2, 3]);
  }

}
