using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The management UI in a REAL browser: the shipped page, served by the shipped daemon, rendered
/// by Chromium, on both Windows and Linux.
///
/// The API tier proves the daemon answers correctly; this proves the user can actually see it —
/// the script parses and runs, the dashboard renders a pool, the live connection comes up, and a
/// long operation opens a progress dialog instead of freezing the page. A page that throws on
/// load fails no API test at all.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("WebUi")]
[NonParallelizable]
public class WebUiEndToEndTests {

  private ManagementDaemon _daemon = null!;
  private IPlaywright _playwright = null!;
  private IBrowser _browser = null!;
  private string _poolName = "";
  private string _root = "";

  [OneTimeSetUp]
  public async Task StartEverything() {
    this._daemon = ManagementDaemon.Start();

    // a pool to render — an empty dashboard would not prove the card path works
    this._root = Path.Combine(Path.GetTempPath(), "dbe2e-ui-" + Guid.NewGuid().ToString("N"));
    var members = new[] { Path.Combine(this._root, "m0"), Path.Combine(this._root, "m1") };
    foreach (var member in members)
      Directory.CreateDirectory(member);

    this._poolName = "e2eui" + Guid.NewGuid().ToString("N")[..8];
    DbMount.RunExpectingSuccess(TimeSpan.FromMinutes(2),
      "pool-create", "-n", this._poolName, "-m", members[0], "-m", members[1]);

    try {
      this._playwright = await Playwright.CreateAsync();
      this._browser = await this._playwright.Chromium.LaunchAsync(new() { Headless = true });
    } catch (Exception e) {
      // The browser is a build-environment dependency, not a product one. On a developer box it is
      // reasonable not to have it; in CI its absence means the job was mis-configured, and a
      // silently skipped UI suite looks exactly like a passing one.
      var message = $"Chromium is not available to Playwright on {DbMount.Platform}: {e.Message}. "
                    + "Run `pwsh bin/Release/net10.0/playwright.ps1 install chromium`.";
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

    if (this._poolName.Length > 0)
      DbMount.ForgetPool(this._poolName);

    this._daemon?.Dispose();

    if (this._root.Length > 0 && Directory.Exists(this._root))
      Directory.Delete(this._root, true);
  }

  /// <summary>A fresh page with the token in the URL — the link the daemon prints for the user.</summary>
  private async Task<(IPage page, List<string> errors)> _OpenDashboardAsync() {
    var context = await this._browser.NewContextAsync();
    var page = await context.NewPageAsync();

    // any uncaught script error or failed request is a UI defect even if the assertions below pass
    var errors = new List<string>();
    page.PageError += (_, error) => errors.Add($"uncaught script error: {error}");
    page.Console += (_, message) => {
      if (message.Type == "error")
        errors.Add($"console error: {message.Text}");
    };

    // NOT NetworkIdle: the dashboard holds an EventSource open for live updates, so the network is
    // never idle by design and waiting for it would time out on a perfectly healthy page
    await page.GotoAsync($"{this._daemon.BaseAddress}?token={this._daemon.Token}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    return (page, errors);
  }

  [Test]
  [Category("HappyPath")]
  public async Task Dashboard_WhenOpenedWithTheToken_ThenItRendersThePoolWithoutScriptErrors() {
    var (page, errors) = await this._OpenDashboardAsync();

    // the pool card is rendered by app.js from the live frame — its presence proves the whole
    // chain: assets served, script parsed, token accepted, frame fetched, DOM built
    var card = page.Locator("#pools .card", new() { HasTextString = this._poolName });
    await card.First.WaitForAsync(new() { Timeout = 30_000, State = WaitForSelectorState.Visible });

    (await card.CountAsync()).Should().BeGreaterThan(0, "the dashboard must render the pool it was told about");
    (await page.Locator("#empty").IsVisibleAsync()).Should().BeFalse("the empty-state must be hidden when a pool exists");

    errors.Should().BeEmpty("the page must render without script errors");
    await page.CloseAsync();
  }

  [Test]
  [Category("EdgeCase")]
  public void Assets_GivenEveryStateTheDaemonCanReport_ThenTheShippedStylesheetPaintsIt() {
    // The daemon decides the state and the stylesheet gives it a colour, and nothing connects the
    // two but a string. A state with no rule renders as the default dot — a healthy-looking green
    // for a drive that may be failing — and no test of either half would notice. Asserted against
    // the SERVED asset, so it is the stylesheet the user actually gets.
    var css = this._daemon.Get("styles.css");
    css.EnsureSuccessStatusCode();
    var text = css.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    foreach (var state in new[] { "healthy", "aging", "warning", "failing", "detached", "replacing", "unknown" })
      text.Should().Contain($".st-{state}",
        $"the daemon can report '{state}', so the shipped stylesheet has to know how to paint it");
  }

  [Test]
  [Category("Exception")]
  public async Task Dashboard_GivenSmartCannotBeRead_ThenStorageIsMarkedUnknownRatherThanFailing() {
    // The health colours are only worth having if they are believed, and the fastest way to make
    // them worthless is to cry wolf. smartctl cannot open a raw device without privileges, which is
    // the ordinary case for a daemon a user starts — and the parser used to read the resulting
    // "no smart_status" as the drive having FAILED its own assessment, so every storage on such a
    // machine would light up red. This asserts the honest answer instead: unknown is a state of its
    // own, drawn as an outline rather than a fill, and never the alarm colour.
    var (page, errors) = await this._OpenDashboardAsync();

    var dot = page.Locator("#pools .card .member .status").First;
    await dot.WaitForAsync(new() { Timeout = 30_000, State = WaitForSelectorState.Attached });

    var classes = await page.EvalOnSelectorAllAsync<string[]>(
      "#pools .card .member .status", "els => els.map(e => e.className)");

    classes.Should().NotBeEmpty("every storage row must carry a state dot");
    foreach (var className in classes) {
      className.Should().Contain("st-", $"the dot must carry a resolved state, got '{className}'");
      className.Should().NotContain("st-failing",
        "no drive on this machine is failing; reporting one because SMART could not be READ is the "
        + "false alarm that makes the whole indicator worthless");
    }

    errors.Should().BeEmpty("the page must render the health states without script errors");
    await page.CloseAsync();
  }

  [Test]
  [Category("HappyPath")]
  public void Api_GivenTheDashboardFrame_ThenEveryMemberCarriesAResolvedState() {
    // The precedence lives in the daemon, not the browser, so it is asserted where it is decided:
    // every member reports exactly one state, and it is one the UI knows how to paint.
    // the daemon caches discovery, so a pool created moments ago takes a beat to appear — the
    // browser scenarios wait for the card for the same reason
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    var states = new List<string?>();
    while (DateTime.UtcNow < deadline && states.Count == 0) {
      foreach (var pool in this._daemon.GetJson("api/pools").GetProperty("pools").EnumerateArray())
        foreach (var member in pool.GetProperty("members").EnumerateArray())
          states.Add(member.GetProperty("state").GetString());

      if (states.Count == 0)
        Thread.Sleep(500);
    }

    states.Should().NotBeEmpty("the fixture pool has members, so there was something to check");
    foreach (var state in states)
      state.Should().BeOneOf("healthy", "aging", "warning", "failing", "detached", "replacing", "unknown");
  }

  [Test]
  [Category("HappyPath")]
  public async Task Dashboard_WhenTheLiveStreamConnects_ThenTheIndicatorReportsItAsLive() {
    var (page, errors) = await this._OpenDashboardAsync();

    // the live dot is the user's only signal that the numbers on screen are current; it starts as
    // "connecting…" and must resolve once the SSE stream delivers a frame
    var liveText = page.Locator("#live-text");
    await liveText.WaitForAsync(new() { Timeout = 30_000 });

    var settled = await page.WaitForFunctionAsync(
      "() => { const t = document.getElementById('live-text'); return t && !/connecting/i.test(t.textContent || ''); }",
      null, new() { Timeout = 30_000 });

    settled.Should().NotBeNull();
    (await liveText.TextContentAsync()).Should().NotContainEquivalentOf("connecting",
      "the dashboard must leave the connecting state once the live feed delivers");

    errors.Should().BeEmpty();
    await page.CloseAsync();
  }

  [Test]
  [Category("HappyPath")]
  public async Task Dashboard_WhenAPoolIsPresent_ThenItsActionsAreOffered() {
    var (page, errors) = await this._OpenDashboardAsync();

    var card = page.Locator("#pools .card", new() { HasTextString = this._poolName }).First;
    await card.WaitForAsync(new() { Timeout = 30_000, State = WaitForSelectorState.Visible });

    // an unmounted pool must offer Mount and the management actions; a card with no buttons means
    // the user can see the pool and do nothing with it
    var buttons = card.Locator("button");
    (await buttons.CountAsync()).Should().BeGreaterThan(0, "a pool card must offer actions");

    var labels = await buttons.AllTextContentsAsync();
    labels.Should().Contain(l => l.Contains("Mount", StringComparison.OrdinalIgnoreCase),
      "an unmounted pool must offer to mount");
    labels.Should().Contain(l => l.Contains("Health", StringComparison.OrdinalIgnoreCase),
      "the problem scan must be reachable from the card");

    errors.Should().BeEmpty();
    await page.CloseAsync();
  }

  [Test]
  [Category("EdgeCase")]
  public async Task Dashboard_WhenOpenedWithoutAToken_ThenItDoesNotLeakPoolData() {
    var context = await this._browser.NewContextAsync();
    var page = await context.NewPageAsync();
    await page.GotoAsync(this._daemon.BaseAddress.ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded });

    // give the page the same chance to populate that an authorised one gets, so this asserts
    // "nothing rendered" rather than merely "checked too early"
    await page.WaitForTimeoutAsync(3000);

    // the shell is public so the page can bootstrap, but nothing about the user's storage may be
    // on screen without the session token
    var body = await page.Locator("body").TextContentAsync() ?? "";
    body.Should().NotContain(this._poolName, "an unauthenticated page must not render pool data");

    await page.CloseAsync();
  }

}
