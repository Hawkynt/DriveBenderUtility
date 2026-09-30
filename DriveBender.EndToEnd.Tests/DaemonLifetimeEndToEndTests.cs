using System.Diagnostics;
using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// The management daemon must outlive whatever the dashboard asks of it. The dashboard polls the
/// prerequisites every 15 seconds, and asking whether the Dokan driver is installed used to leave a
/// half-built Dokan object behind on every machine without Dokan. The garbage collector later ran its
/// finalizer, which called into the missing DLL and took the whole daemon down — the page's "lost
/// connection", arriving at a random moment after some minutes of use.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[NonParallelizable]
public class DaemonLifetimeEndToEndTests {

  [Test]
  [Category("Regression")]
  public void Daemon_GivenTheDashboardKeepsPollingThePrerequisites_ThenItStaysUp() {
    using var daemon = ManagementDaemon.Start();
    var clock = Stopwatch.StartNew();
    var polls = 0;

    // the dashboard's own rhythm, compressed: a prerequisites check, then the ordinary traffic that
    // makes the daemon allocate — and so collect, and so finalize — between them
    while (clock.Elapsed < TimeSpan.FromSeconds(40)) {
      try {
        using (var prereqs = daemon.Get("api/prereqs"))
          prereqs.IsSuccessStatusCode.Should().BeTrue();

        for (var i = 0; i < 20; ++i)
          using (daemon.Get("api/pools")) { }
      } catch (HttpRequestException e) {
        Assert.Fail($"the daemon stopped answering after {polls} prerequisite checks in {clock.Elapsed.TotalSeconds:F0}s: "
                    + $"{e.Message}{Environment.NewLine}{daemon.Log}");
      }

      ++polls;
    }

    TestContext.Out.WriteLine($"[daemon] survived {polls} prerequisite checks in {clock.Elapsed.TotalSeconds:F0}s");
  }

}
