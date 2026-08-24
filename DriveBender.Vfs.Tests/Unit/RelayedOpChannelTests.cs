using DivisonM.Mount;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The channel between the manager and a MOUNTED pool's own process.
///
/// Pool work never runs inside the manager — the manager is a reload-safe UI shell, and a mounted
/// pool owns its engine — so a health scan or a duplication restore on a mounted pool is FILED as a
/// request and executed over there. That relay used to be one-way: once filed, the manager had
/// nothing more to say and nothing to hear. So such work reported itself un-cancellable, which was
/// honest and useless — a deep scrub of a mounted pool could not be stopped for however many hours
/// it took — and showed no progress beyond "still running".
///
/// Both directions ride the same channel directory as the request, with the same file-drop shape.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RelayedOpChannelTests {

  private static readonly Guid _pool = Guid.Parse("0d17e5a0-0000-4000-8000-00000000c0de");

  private static (MountRegistry registry, FakeHostEnvironment host) _Registry() {
    var host = new FakeHostEnvironment();
    return (new MountRegistry(host), host);
  }

  [Test]
  [Category("HappyPath")]
  public void Cancel_GivenAnOperationRelayedToThePoolsProcess_ThenTheRequestReachesIt() {
    var (registry, _) = _Registry();
    const string id = "op-1";

    registry.RequestOp(_pool, id, "health-deep");
    registry.IsOpCancelRequested(_pool, id).Should().BeFalse("nothing has asked it to stop yet");

    registry.RequestOpCancel(_pool, id);

    registry.IsOpCancelRequested(_pool, id).Should().BeTrue(
      "the pool's process polls this between items — without it, work relayed into a mounted pool "
      + "could only be stopped by killing the mount, which is the one thing a user must not have to do");
  }

  [Test]
  [Category("HappyPath")]
  public void Progress_GivenThePoolsProcessPublishesIt_ThenTheManagerCanRead()  {
    var (registry, _) = _Registry();
    const string id = "op-2";

    registry.ReadOpProgress(_pool, id).Should().BeNull("nothing published yet");

    registry.WriteOpProgress(_pool, id, """{"completed":1204,"total":18932,"item":"Photos/DSC.NEF","text":"1,204 of 18,932"}""");

    var published = registry.ReadOpProgress(_pool, id);
    published.Should().NotBeNull().And.Contain("18932", "the manager pumps this into the job ticket the browser polls");
  }

  [Test]
  [Category("EdgeCase")]
  public void Channel_WhenTheOperationEnds_ThenItsSideFilesAreCleanedUp() {
    var (registry, _) = _Registry();
    const string id = "op-3";

    registry.RequestOpCancel(_pool, id);
    registry.WriteOpProgress(_pool, id, """{"completed":1,"total":2,"item":"x","text":"1 of 2"}""");

    registry.ClearOp(_pool, id);

    // a channel directory that accumulated a stop marker and a progress file per operation would
    // grow without bound on a long-lived daemon, and a stale stop marker is worse than clutter
    registry.IsOpCancelRequested(_pool, id).Should().BeFalse();
    registry.ReadOpProgress(_pool, id).Should().BeNull();
  }

  [Test]
  [Category("EdgeCase")]
  public void Result_GivenNoneFiledYet_ThenTakingItReportsNotFinishedRatherThanBlocking() {
    var (registry, _) = _Registry();
    const string id = "op-4";

    registry.TryTakeOpResult(_pool, id).Should().BeNull("the operation has not answered yet");

    registry.WriteOpResult(_pool, id, """{"ok":true}""");
    registry.TryTakeOpResult(_pool, id).Should().Contain("ok", "the result is collected exactly once");
    registry.TryTakeOpResult(_pool, id).Should().BeNull("and is consumed, so a later poll does not re-report it");
  }

  [Test]
  [Category("EdgeCase")]
  public void Cancel_GivenTwoOperationsOnOnePool_ThenStoppingOneLeavesTheOtherRunning() {
    var (registry, _) = _Registry();

    registry.RequestOpCancel(_pool, "op-a");

    registry.IsOpCancelRequested(_pool, "op-a").Should().BeTrue();
    registry.IsOpCancelRequested(_pool, "op-b").Should().BeFalse(
      "the marker is per TICKET, not per pool — cancelling a scan must not also stop a restore");
  }

}
