using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// Clicks and types at random through the dashboard and its dialogs — pool settings above all — and
/// fails the moment the page loses its live connection to the daemon, a request hangs, the daemon
/// stops answering, or the page throws. That is how the lost connection was met in the field:
/// "I opened pool settings and randomly clicked buttons and entered stuff until I lost them."
///
/// Runs against a daemon with a registry of its own (DBMOUNT_CONFIG_ROOT), so nothing it clicks can
/// touch the machine's real pools, and it never types anything that could name a real path: no drive
/// letters, no slashes. Buttons that reach outside the sandbox — folder pickers, adding members,
/// creating pools, installing prerequisites, deleting — are never pressed.
///
/// Seeded and logged: a failure prints the seed and every action, and <c>DBE2E_FUZZ_SEED</c> replays
/// it. <c>DBE2E_FUZZ_SECONDS</c> sets how long to fuzz (default 90).
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Driver")]
[NonParallelizable]
public class UiFuzzEndToEndTests {

  private string _configRoot = "";
  private string? _previousConfigRoot;
  private ManagementDaemon _daemon = null!;
  private MountedPool _pool = null!;
  private IPlaywright _playwright = null!;
  private IBrowser _browser = null!;

  /// <summary>Anything whose label matches this is never pressed: it reaches outside the sandbox or ends the session.</summary>
  private static readonly Regex _UNSAFE = new(
    @"delete|purge|forget|install|remove|replace|retire|browse|📁|add member|new pool|create|adopt|take over|exchange|credential",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

  [OneTimeSetUp]
  public async Task StartEverything() {
    this._configRoot = Path.Combine(Path.GetTempPath(), "dbe2e-fuzzroot-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._configRoot);
    this._previousConfigRoot = Environment.GetEnvironmentVariable("DBMOUNT_CONFIG_ROOT");
    Environment.SetEnvironmentVariable("DBMOUNT_CONFIG_ROOT", this._configRoot); // every dbmount this test starts inherits it

    this._daemon = ManagementDaemon.Start();
    this._pool = MountedPool.Create(members: 2, poolDefaults: MountedPool.DuplicatedOnOneDisk);

    try {
      this._playwright = await Playwright.CreateAsync();
      this._browser = await this._playwright.Chromium.LaunchAsync(new() { Headless = true });
    } catch (Exception e) {
      var message = $"Chromium is not available to Playwright on {DbMount.Platform}: {e.Message}.";
      if (DriverPrerequisite.Required)
        Assert.Fail(message + " DBE2E_REQUIRE_DRIVER=1 says this environment was supposed to be complete.");

      Assert.Ignore(message);
    }
  }

  [OneTimeTearDown]
  public async Task StopEverything() {
    if (this._browser != null)
      await this._browser.CloseAsync();

    this._playwright?.Dispose();
    this._pool?.Dispose();
    this._daemon?.Dispose();
    Environment.SetEnvironmentVariable("DBMOUNT_CONFIG_ROOT", this._previousConfigRoot);
    try {
      Directory.Delete(this._configRoot, true);
    } catch (IOException) {
      // a mount record still held open; it is in a temp folder
    }
  }

  #region what the fuzzer types

  private static readonly string[] _SETTING_PATHS = [
    "duplication", "write.policy", "write.minCopiesBeforeAck", "write.striping", "write.acceptVolatileAck",
    "cache.size", "cache.blockSize", "readAhead.enabled", "readAhead.minWindow", "readAhead.maxWindow",
    "io.queueDepthPerVolume", "io.mirrorReadSplitThreshold", "resilience.memberPollSeconds", "resilience.onMemberLoss",
    "resilience.acceptDegradedWrites", "integrity.verifyReads", "integrity.checksumDb", "trash.enabled", "trash.retention",
    "trash.maxSize", "tiers.fast.lowWatermark", "tiers.fast.drainConcurrency", "placement.strategy", "background.maxThroughput",
  ];

  private static readonly string[] _VALUES = [
    "", " ", "0", "1", "2", "-1", "3", "64", "999999999999", "1e309", "-0", "0.5", "NaN", "true", "false", "null",
    "abc", "ÿ😀ü", "\"", "'", "<b>x</b>", "%00", "\u0000", "1TB", "1KiB", "16", "100%", "0%", "150%", "5s", "0s", "-5s",
    "7d", "never", "before", "after", "write-back", "performance", "deferred", "write-through", "round-robin",
    "most-free-space", "lowest-latency", "retain-metadata", "4GiB", "64GiB", "1B",
  ];

  /// <summary>Text for a field: an edge value, a number, or noise — never a drive letter or a slash.</summary>
  private static string _Text(Random random) => random.Next(10) switch {
    < 5 => _VALUES[random.Next(_VALUES.Length)],
    < 7 => random.Next(-100, 100_000).ToString(),
    < 8 => new string('9', random.Next(1, 400)),
    < 9 => new string('a', random.Next(1, 5000)),
    _ => new string([.. Enumerable.Range(0, random.Next(1, 40)).Select(_ => (char)random.Next(0x20, 0x7F)).Where(c => c is not (':' or '\\' or '/'))]),
  };

  /// <summary>A settings document for the raw-JSON editor: plausible keys, hostile values, and sometimes plain broken JSON.</summary>
  private static string _Json(Random random) {
    if (random.Next(6) == 0)
      return random.Next(3) switch {
        0 => "{ \"write\": ",
        1 => new string('[', 5000) + new string(']', 5000),
        _ => _Text(random),
      };

    var root = new Dictionary<string, object?>();
    for (var i = random.Next(1, 6); i > 0; --i) {
      var parts = _SETTING_PATHS[random.Next(_SETTING_PATHS.Length)].Split('.');
      var node = root;
      for (var p = 0; p < parts.Length - 1; ++p) {
        if (node.GetValueOrDefault(parts[p]) is not Dictionary<string, object?> child)
          node[parts[p]] = child = [];
        node = child;
      }

      var raw = _VALUES[random.Next(_VALUES.Length)];
      node[parts[^1]] = random.Next(3) switch {
        0 => long.TryParse(raw, out var n) ? n : raw,
        1 => raw == "true" || raw == "false" ? raw == "true" : raw,
        _ => raw,
      };
    }

    return System.Text.Json.JsonSerializer.Serialize(root);
  }

  #endregion

  [Test]
  [Category("EdgeCase")]
  [Description("Random clicks and input through the dashboard and pool settings never cost the page its live connection to the daemon.")]
  public async Task Fuzz_GivenRandomClicksAndInputThroughTheSettings_ThenTheConnectionIsNeverLost() {
    var seed = int.TryParse(Environment.GetEnvironmentVariable("DBE2E_FUZZ_SEED"), out var s) ? s : Environment.TickCount;
    var seconds = int.TryParse(Environment.GetEnvironmentVariable("DBE2E_FUZZ_SECONDS"), out var t) ? t : 90;
    var random = new Random(seed);
    var log = new List<string>();
    var problems = new List<string>();

    var context = await this._browser.NewContextAsync();
    var page = await context.NewPageAsync();
    page.Dialog += (_, dialog) => _ = random.Next(2) == 0 ? dialog.AcceptAsync(_Text(random)) : dialog.DismissAsync(); // confirm()/prompt()
    page.PageError += (_, error) => problems.Add($"uncaught script error after '{log.LastOrDefault()}': {error}");

    // every request the page makes, and when it started — one that never finishes is a hang
    var pending = new System.Collections.Concurrent.ConcurrentDictionary<IRequest, (string Url, DateTime Started)>();
    page.Request += (_, request) => {
      if (!request.Url.Contains("/api/stream", StringComparison.Ordinal))
        pending[request] = (request.Url, DateTime.UtcNow);
    };
    page.RequestFinished += (_, request) => pending.TryRemove(request, out var _);
    page.RequestFailed += (_, request) => pending.TryRemove(request, out var _);

    await page.GotoAsync($"{this._daemon.BaseAddress}?token={this._daemon.Token}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    var card = page.Locator("#pools .card", new() { HasTextString = this._pool.PoolName }).First;
    await card.WaitForAsync(new() { Timeout = 30_000 });
    await page.Locator("#live-text", new() { HasTextString = "live" }).WaitForAsync(new() { Timeout = 30_000 });

    var clock = Stopwatch.StartNew();
    DateTime? disconnectedSince = null;
    var actions = 0;
    while (clock.Elapsed < TimeSpan.FromSeconds(seconds) && problems.Count == 0) {
      ++actions;
      try {
        await this._OneAction(page, card, random, log);
      } catch (PlaywrightException) {
        // the element moved or vanished under the click — normal for a page being fuzzed
      } catch (TimeoutException) {
      }

      await Task.Delay(random.Next(0, 250));

      // the live indicator: a blip while the daemon restarts a stream is fine, a lost connection is not
      var live = await page.Locator("#live-text").TextContentAsync(new() { Timeout = 2_000 }) ?? "";
      if (live.Contains("reconnect", StringComparison.OrdinalIgnoreCase)) {
        disconnectedSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - disconnectedSince > TimeSpan.FromSeconds(5))
          problems.Add($"the page lost its live connection after action {actions} ('{log.LastOrDefault()}') and did not get it back within 5s");
      } else
        disconnectedSince = null;

      foreach (var (url, started) in pending.Values.Where(p => DateTime.UtcNow - p.Started > TimeSpan.FromSeconds(30)))
        problems.Add($"a request never finished (30s+): {url}");

      if (actions % 15 == 0) {
        var answer = Stopwatch.StartNew();
        try {
          using var response = this._daemon.Get("api/pools");
          if (!response.IsSuccessStatusCode)
            problems.Add($"the daemon answered /api/pools with {(int)response.StatusCode} after action {actions}");
        } catch (Exception e) {
          problems.Add($"the daemon stopped answering after action {actions}: {e.Message}");
        }

        if (answer.Elapsed > TimeSpan.FromSeconds(5))
          problems.Add($"the daemon took {answer.Elapsed.TotalSeconds:F1}s to answer /api/pools after action {actions}");
      }
    }

    TestContext.Out.WriteLine($"[fuzz] seed {seed}: {actions} actions in {clock.Elapsed.TotalSeconds:F0}s");
    problems.Should().BeEmpty(
      $"random use of the UI must never cost it the daemon. Replay with DBE2E_FUZZ_SEED={seed}.{Environment.NewLine}"
      + $"Last actions:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", log.TakeLast(40))}{Environment.NewLine}"
      + this._daemon.Log);
    actions.Should().BeGreaterThan(20, "the fuzzer has to actually do something");
    await page.CloseAsync();
  }

  /// <summary>One random action: open something from the pool card, or work a control of the open dialog.</summary>
  private async Task _OneAction(IPage page, ILocator card, Random random, List<string> log) {
    var modal = page.Locator(".modal:visible, dialog[open]").Last;
    var modalOpen = await modal.CountAsync() > 0;

    if (random.Next(40) == 0) {
      log.Add("reload the page");
      await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
      return;
    }

    if (modalOpen && random.Next(12) == 0) {
      log.Add("press Escape");
      await page.Keyboard.PressAsync("Escape");
      return;
    }

    var scope = modalOpen ? modal : card;
    var controls = scope.Locator("button:visible, input:visible, select:visible, textarea:visible, summary:visible");
    var count = await controls.CountAsync();
    if (count == 0)
      return;

    // pool settings is what the field report was about, so the card's Settings button is favoured
    if (!modalOpen && random.Next(3) == 0) {
      var settings = card.Locator("button", new() { HasTextRegex = new Regex("settings", RegexOptions.IgnoreCase) });
      if (await settings.CountAsync() > 0) {
        log.Add("open Settings");
        await settings.First.ClickAsync(new() { Timeout = 2_000 });
        return;
      }
    }

    var control = controls.Nth(random.Next(count));
    var tag = (await control.EvaluateAsync<string>("e => e.tagName")).ToLowerInvariant();
    var label = ((await control.TextContentAsync(new() { Timeout = 1_000 })) ?? "").Trim();
    var title = await control.GetAttributeAsync("title", new() { Timeout = 1_000 }) ?? "";
    switch (tag) {
      case "button":
      case "summary":
        if (_UNSAFE.IsMatch(label) || _UNSAFE.IsMatch(title))
          return;
        log.Add($"click '{(label.Length > 0 ? label : title)}'");
        await control.ClickAsync(new() { Timeout = 2_000 });
        break;

      case "select":
        var options = await control.Locator("option").AllTextContentsAsync();
        if (options.Count == 0)
          return;
        var index = random.Next(options.Count);
        log.Add($"select option {index} ('{options[index]}')");
        await control.SelectOptionAsync(new SelectOptionValue { Index = index }, new() { Timeout = 2_000 });
        break;

      case "textarea": {
        var text = _Json(random);
        log.Add($"type JSON ({text.Length} chars): {(text.Length > 120 ? text[..120] + "…" : text)}");
        await control.FillAsync(text, new() { Timeout = 5_000 });
        break;
      }

      default: {
        var type = (await control.GetAttributeAsync("type", new() { Timeout = 1_000 }) ?? "text").ToLowerInvariant();
        if (type is "checkbox" or "radio") {
          log.Add($"toggle {type}");
          await control.ClickAsync(new() { Timeout = 2_000 });
          return;
        }

        var text = _Text(random);
        if (type == "number" && !double.TryParse(text, out _))
          text = random.Next(-5, 100_000).ToString();
        log.Add($"type ({text.Length} chars): {(text.Length > 60 ? text[..60] + "…" : text)}");
        await control.FillAsync(text, new() { Timeout = 5_000 });
        break;
      }
    }
  }

}
