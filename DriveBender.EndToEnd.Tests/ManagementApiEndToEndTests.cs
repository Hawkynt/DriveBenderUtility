using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The management API as the page and the desktop shell use it: a real <c>dbmount serve</c>
/// process on loopback, driven over HTTP.
///
/// Needs no filesystem driver, so it runs everywhere — this is the tier that proves the UI's
/// back end works on both targets even where a driver is unavailable.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("ManagementApi")]
[NonParallelizable]
public class ManagementApiEndToEndTests {

  private ManagementDaemon _daemon = null!;

  [OneTimeSetUp]
  public void StartDaemon() => this._daemon = ManagementDaemon.Start();

  [OneTimeTearDown]
  public void StopDaemon() => this._daemon?.Dispose();

  [Test]
  [Category("HappyPath")]
  public void Assets_GivenAFreshBrowser_ThenThePageAndItsScriptAndStylesAreServed() {
    foreach (var (path, fragment) in new[] { ("/", "<html"), ("/app.js", "function"), ("/styles.css", "{") }) {
      using var response = this._daemon.Get(path);
      response.StatusCode.Should().Be(HttpStatusCode.OK, $"'{path}' must be served");
      response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        .Should().Contain(fragment, $"'{path}' must actually carry its content");
    }
  }

  [Test]
  [Category("Exception")]
  public void Api_GivenNoToken_ThenEveryEndpointRefuses() {
    using var anonymous = new HttpClient { BaseAddress = this._daemon.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };

    foreach (var path in new[] { "api/pools", "api/prereqs", "api/fs/list" }) {
      using var response = anonymous.GetAsync(path).GetAwaiter().GetResult();
      response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
        $"'{path}' is management surface — it must not answer without the session token");
    }

    // static assets stay public so the page can bootstrap and then present the token
    using var page = anonymous.GetAsync("/").GetAwaiter().GetResult();
    page.StatusCode.Should().Be(HttpStatusCode.OK);
  }

  [Test]
  [Category("HappyPath")]
  public void Pools_GivenTheDashboardFrame_ThenItIsWellFormedAndCarriesTheJobList() {
    var frame = this._daemon.GetJson("api/pools");

    frame.TryGetProperty("pools", out var pools).Should().BeTrue("the frame must carry the pool list");
    pools.ValueKind.Should().Be(JsonValueKind.Array);
    frame.TryGetProperty("jobs", out var jobs).Should().BeTrue("running jobs ride the same frame the live feed ships");
    jobs.ValueKind.Should().Be(JsonValueKind.Array);
  }

  [Test]
  [Category("HappyPath")]
  public void Stream_GivenAConnectedClient_ThenLiveFramesArrive() {
    // the dashboard's liveness is an SSE stream; if it never emits, the UI shows stale numbers
    // forever without any error
    var frame = this._daemon.ReadFirstStreamFrame(TimeSpan.FromSeconds(30));

    var parsing = () => JsonDocument.Parse(frame);
    parsing.Should().NotThrow("every SSE data frame must be valid JSON");
    JsonDocument.Parse(frame).RootElement.TryGetProperty("pools", out _).Should().BeTrue();
  }

  [Test]
  [Category("HappyPath")]
  public void Prereqs_GivenThisMachine_ThenTheDriverStatusIsReportedHonestly() {
    var payload = this._daemon.GetJson("api/prereqs");
    payload.TryGetProperty("ok", out var ok).Should().BeTrue();

    var (available, detail) = DriverPrerequisite.Probe();
    if (!available)
      return; // nothing to cross-check against on an unequipped machine

    ok.GetBoolean().Should().BeTrue(
      $"this machine has a driver ({detail}) but the daemon reports the prerequisite as unmet — "
      + "the UI would demand an install the user does not need");
  }

  [Test]
  [Category("EdgeCase")]
  public void Job_GivenAnUnknownTicket_ThenItIsReportedRatherThanHanging() {
    var status = this._daemon.PostJson("api/job?id=does-not-exist");
    status.GetProperty("ok").GetBoolean().Should().BeFalse();

    var cancel = this._daemon.PostJson("api/job/cancel?id=does-not-exist");
    cancel.GetProperty("ok").GetBoolean().Should().BeFalse("cancelling a ticket the daemon has forgotten must say so");
  }

  [Test]
  [Category("HappyPath")]
  public void LongOperation_GivenTheDriverInstallEndpoint_ThenItAnswersImmediatelyWithATicket() {
    // NFR-UI-LIVE: the guarantee is that a long operation never holds the request open. Installing
    // the driver takes minutes; the response must still come back in milliseconds.
    var elapsed = Stopwatch.StartNew();
    var started = this._daemon.PostJson("api/prereqs/install");
    elapsed.Stop();

    elapsed.ElapsedMilliseconds.Should().BeLessThan(5000,
      $"a long operation held the HTTP request for {elapsed.ElapsedMilliseconds} ms — it must answer with a ticket at once");

    started.GetProperty("ok").GetBoolean().Should().BeTrue();
    var result = started.GetProperty("result");
    result.GetProperty("jobId").GetString().Should().NotBeNullOrEmpty("the caller needs a ticket to poll");
    result.GetProperty("state").GetString().Should().Be("running");

    // an installer part-way through cannot be un-begun, and the UI is told so rather than being
    // offered a button that does nothing
    result.GetProperty("cancellable").GetBoolean().Should().BeFalse();

    var jobId = result.GetProperty("jobId").GetString()!;
    var status = this._daemon.PostJson($"api/job?id={jobId}");
    status.GetProperty("ok").GetBoolean().Should().BeTrue("the ticket must be pollable");

    var refused = this._daemon.PostJson($"api/job/cancel?id={jobId}");
    refused.GetProperty("ok").GetBoolean().Should().BeFalse("an unstoppable job must refuse cancellation honestly");
  }

  [Test]
  [Category("HappyPath")]
  public void PoolLifecycle_GivenCreateThenForget_ThenTheDashboardReflectsBothWithoutAMount() {
    var root = Path.Combine(Path.GetTempPath(), "dbe2e-api-" + Guid.NewGuid().ToString("N"));
    var members = new[] { Path.Combine(root, "m0"), Path.Combine(root, "m1") };
    foreach (var member in members)
      Directory.CreateDirectory(member);

    var poolName = "e2eapi" + Guid.NewGuid().ToString("N")[..8];
    try {
      DbMount.RunExpectingSuccess(TimeSpan.FromMinutes(2),
        "pool-create", "-n", poolName, "-m", members[0], "-m", members[1]);

      // the dashboard frame is sampled on a timer, so give it a tick to pick the pool up
      var deadline = DateTime.UtcNow.AddSeconds(30);
      var seen = false;
      while (!seen && DateTime.UtcNow < deadline) {
        seen = this._daemon.GetJson("api/pools").GetProperty("pools").EnumerateArray()
          .Any(p => p.GetProperty("name").GetString() == poolName);
        if (!seen)
          Thread.Sleep(500);
      }

      seen.Should().BeTrue($"a pool created through the CLI must appear in the dashboard.{Environment.NewLine}{this._daemon.Log}");

      var pool = this._daemon.GetJson("api/pools").GetProperty("pools").EnumerateArray()
        .First(p => p.GetProperty("name").GetString() == poolName);
      pool.GetProperty("members").GetArrayLength().Should().Be(members.Length, "both members must be reported");
      pool.GetProperty("mounted").ValueKind.Should().Be(JsonValueKind.Null, "the pool is not mounted");
    } finally {
      DbMount.ForgetPool(poolName);
      if (Directory.Exists(root))
        Directory.Delete(root, true);
    }
  }

}
