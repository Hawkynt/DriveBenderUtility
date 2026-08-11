using DivisonM.Vfs;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class LocalVolumeOnlineProbeTests {

  [Test]
  [Category("HappyPath")]
  public void IsOnline_GivenAFreshlyConstructedMemberWhoseFolderExists_ThenItReportsOnlineImmediately() {
    var root = Path.Combine(Path.GetTempPath(), "dbprobe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
      var volume = new LocalVolumeIO(Guid.NewGuid(), "m1", root, "PHYS-1");

      // The very FIRST query is the one that matters: a mount asks it before serving anything, and
      // refuses outright when no member answers. A member whose directory is plainly there must
      // never report offline.
      volume.IsOnline.Should().BeTrue("a member whose root directory exists is online");
    } finally {
      Directory.Delete(root, true);
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void IsOnline_GivenTheFolderDisappears_ThenItIsNoticedWithinTheProbeWindow() {
    var root = Path.Combine(Path.GetTempPath(), "dbprobe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var volume = new LocalVolumeIO(Guid.NewGuid(), "m1", root, "PHYS-1");
    volume.IsOnline.Should().BeTrue();

    Directory.Delete(root, true);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (volume.IsOnline && DateTime.UtcNow < deadline)
      Thread.Sleep(50);

    volume.IsOnline.Should().BeFalse("a member that vanished must be noticed within the probe window, not cached as online forever");
  }

}
