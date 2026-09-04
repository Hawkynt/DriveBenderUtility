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
  private string _poolName = "";
  private string _root = "";

  [OneTimeSetUp]
  public void StartDaemon() {
    this._daemon = ManagementDaemon.Start();

    // a real pool for the endpoints that act ON one. It is never mounted: everything asserted here
    // is manifest state the daemon reads and writes, and mounting would drag the driver into an
    // API test for nothing.
    this._root = Path.Combine(Path.GetTempPath(), "dbe2e-api-" + Guid.NewGuid().ToString("N"));
    var members = new[] { Path.Combine(this._root, "m0"), Path.Combine(this._root, "m1") };
    foreach (var member in members)
      Directory.CreateDirectory(member);

    this._poolName = "e2eapi" + Guid.NewGuid().ToString("N")[..8];
    DbMount.RunExpectingSuccess(TimeSpan.FromMinutes(2),
      "pool-create", "-n", this._poolName, "-m", members[0], "-m", members[1]);
  }

  [OneTimeTearDown]
  public void StopDaemon() {
    this._daemon?.Dispose();
    if (this._poolName.Length > 0)
      DbMount.ForgetPool(this._poolName);

    try {
      if (Directory.Exists(this._root))
        Directory.Delete(this._root, true);
    } catch (Exception) {
      // teardown is best effort
    }
  }

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
  public void MemberLimits_GivenEachShapeInTurn_ThenTheDashboardReportsWhatWasSet() {
    // The three shapes the advanced dialog offers, through the endpoint it posts to. Worth covering
    // here rather than only in the engine, because the useful part of this feature is that an
    // operator can change it from the UI on a pool that is running — the limit exists precisely for
    // the disk you cannot take offline right now.
    var member = _FirstMember(out var poolId);

    // ADVANCED — a separate rate per kind of work, and per-kind time limits
    this._daemon.PostJson($"api/pool/member-limits?pool={poolId}&member={member}"
                          + "&readThroughput=209715200&writeThroughput=104857600&backgroundThroughput=10485760"
                          + "&maxIops=500&readTimeoutMs=250&backgroundTimeoutMs=2000");

    var limits = this._LimitsOf(member, l => l.GetProperty("readThroughput").GetInt64() > 0);
    limits.GetProperty("readThroughput").GetInt64().Should().Be(200L * 1024 * 1024);
    limits.GetProperty("writeThroughput").GetInt64().Should().Be(100L * 1024 * 1024);
    limits.GetProperty("backgroundThroughput").GetInt64().Should().Be(10L * 1024 * 1024,
      "healing and exchanging is the one an operator most wants to hold down on its own");
    limits.GetProperty("maxIops").GetInt32().Should().Be(500);
    limits.GetProperty("readTimeoutMs").GetInt32().Should().Be(250);
    limits.GetProperty("backgroundTimeoutMs").GetInt32().Should().Be(2000);

    // SIMPLE — one rate covering everything, background work included
    this._daemon.PostJson($"api/pool/member-limits?pool={poolId}&member={member}&maxThroughput=52428800&timeoutMs=400");
    limits = this._LimitsOf(member, l => l.GetProperty("maxThroughput").GetInt64() > 0);
    limits.GetProperty("maxThroughput").GetInt64().Should().Be(50L * 1024 * 1024);
    limits.GetProperty("readThroughput").GetInt64().Should().Be(0,
      "the simple shape sets one rate and leaves the per-kind ones unset, so they fall back to it");
    limits.GetProperty("timeoutMs").GetInt32().Should().Be(400);

    // NONE — switching the limit off has to CLEAR it, not leave the old numbers behind
    this._daemon.PostJson($"api/pool/member-limits?pool={poolId}&member={member}");
    limits = this._LimitsOf(member, l => l.GetProperty("maxThroughput").GetInt64() == 0 && l.GetProperty("maxIops").GetInt32() == 0);
    foreach (var field in new[] { "maxThroughput", "readThroughput", "writeThroughput", "backgroundThroughput" })
      limits.GetProperty(field).GetInt64().Should().Be(0, $"'{field}' must be cleared when limits are turned off");

    limits.GetProperty("maxIops").GetInt32().Should().Be(0);
    limits.GetProperty("timeoutMs").GetInt32().Should().Be(0);
  }

  [Test]
  [Category("Exception")]
  public void MemberLimits_GivenAnUnknownMember_ThenItIsRefusedRatherThanSilentlyIgnored() {
    var _ = _FirstMember(out var poolId);
    var response = this._daemon.Post($"api/pool/member-limits?pool={poolId}&member={Guid.NewGuid()}&maxThroughput=1048576");
    var body = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;

    body.GetProperty("ok").GetBoolean().Should().BeFalse(
      "a limit posted at a member that does not exist must say so — silently accepting it would "
      + "leave an operator believing a disk had been eased off when nothing had changed");
  }

  /// <summary>The first member of the fixture pool, waiting for discovery to notice the pool at all.</summary>
  private string _FirstMember(out string poolId) {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    while (true) {
      foreach (var pool in this._daemon.GetJson("api/pools").GetProperty("pools").EnumerateArray())
        if (pool.GetProperty("name").GetString() == this._poolName) {
          poolId = pool.GetProperty("id").GetString()!;
          return pool.GetProperty("members")[0].GetProperty("id").GetString()!;
        }

      if (DateTime.UtcNow > deadline)
        throw new TimeoutException($"The daemon never listed pool '{this._poolName}'");

      Thread.Sleep(500);
    }
  }

  /// <summary>
  /// The limits the dashboard reports for a member, once they match what was just set.
  ///
  /// Waits for the VALUE rather than for the property to exist: the daemon caches discovery for a
  /// few seconds, so a read straight after a write returns the old numbers — and a helper that
  /// returned the first thing it saw would assert against them and fail for a reason that has
  /// nothing to do with whether the write worked.
  /// </summary>
  private JsonElement _LimitsOf(string memberId, Func<JsonElement, bool> until) {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    JsonElement last = default;
    var sawAny = false;

    while (DateTime.UtcNow < deadline) {
      foreach (var pool in this._daemon.GetJson("api/pools").GetProperty("pools").EnumerateArray())
        foreach (var member in pool.GetProperty("members").EnumerateArray())
          if (member.GetProperty("id").GetString() == memberId && member.TryGetProperty("limits", out var found)) {
            last = found.Clone();
            sawAny = true;
            if (until(last))
              return last;
          }

      Thread.Sleep(250);
    }

    throw new TimeoutException(sawAny
      ? $"The dashboard never reported the limits that were just set for member '{memberId}'; it last said {last}"
      : $"The dashboard never reported limits for member '{memberId}' at all");
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

    // The invariant is that an unstoppable job never claims to be stopping. WHICH honest answer
    // comes back — "cannot be stopped" or "already finished" — depends on whether the installer
    // has got there yet, and on a machine where the prerequisite is already satisfied it finishes
    // in microseconds. Asserting one of the two specifically was therefore a race against the
    // host's speed rather than a statement about the daemon, and it lost that race here.
    var refused = this._daemon.PostJson($"api/job/cancel?id={jobId}");
    var answer = refused.GetProperty("ok").GetBoolean()
      ? refused.GetProperty("result").GetString()
      : refused.GetProperty("error").GetString();

    answer.Should().NotBe("cancelling",
      $"the ticket reported itself un-stoppable, so the daemon must not turn round and say it is "
      + $"stopping it — a Cancel button that silently does nothing is the thing 'cancellable: false' "
      + $"exists to prevent. It answered '{answer}'");
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
