using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Who counts as "having the file open" (SAFE-DUP).
///
/// The background drainer and the owed-copy healer both skip files that are open, on the sound
/// principle that a file someone is actively writing should converge through the write path
/// instead. The question is what "open" means. On Windows it cannot mean "a kernel file object
/// still exists": WinFsp sends CLEANUP when the application closes its last handle and CLOSE only
/// when the kernel releases the file object, and the FSD defers that — often until unmount. So an
/// ordinary file, written and closed by a program that keeps running, stayed permanently "open",
/// and neither the drainer nor the healer ever touched it again.
///
/// That is a durability loss and not a delay: with duplication on, the owed second copy is never
/// made for as long as the writing application lives. Pinned here at the unit level because the
/// end-to-end proof needs a real WinFsp mount and a long-lived writer, which is exactly the
/// combination that hid this for so long.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Regression")]
public class ApplicationCloseTests {

  private const string _PATH = "folder/file.bin";

  [Test]
  [Category("HappyPath")]
  public void IsOpen_GivenAHandleIsOpen_ThenThePathCountsAsOpen() {
    var table = new HandleTable();
    var handle = table.Open(_PATH, AccessMode.Write);

    table.IsOpen(_PATH).Should().BeTrue("a handle the application still holds must block background moves");
    table.Close(handle.Handle);
    table.IsOpen(_PATH).Should().BeFalse("and stop blocking them once it is gone");
  }

  [Test]
  [Category("EdgeCase")]
  public void IsOpen_GivenTheApplicationClosedButTheDriverHasNot_ThenThePathIsNoLongerOpen() {
    var table = new HandleTable();
    var handle = table.Open(_PATH, AccessMode.Write);

    // CLEANUP arrived; CLOSE has not, and may not for a long time
    table.MarkApplicationClosed(handle.Handle);

    table.IsOpen(_PATH).Should().BeFalse(
      "the application is done with the file, so the drainer and the healer must be free to work on "
      + "it — waiting for the kernel to release the file object can mean waiting until unmount");
  }

  [Test]
  [Category("EdgeCase")]
  public void IsOpen_GivenTheApplicationClosedOneOfTwoHandles_ThenThePathStaysOpen() {
    var table = new HandleTable();
    var first = table.Open(_PATH, AccessMode.Write);
    var second = table.Open(_PATH, AccessMode.Read);

    table.MarkApplicationClosed(first.Handle);
    table.IsOpen(_PATH).Should().BeTrue("another application handle is still open on the same file");

    table.MarkApplicationClosed(second.Handle);
    table.IsOpen(_PATH).Should().BeFalse("now every application handle is gone");
  }

  [Test]
  [Category("Exception")]
  public void MarkApplicationClosed_GivenItArrivesTwice_ThenTheCountIsNotDecrementedTwice() {
    var table = new HandleTable();
    var first = table.Open(_PATH, AccessMode.Write);
    var second = table.Open(_PATH, AccessMode.Read);

    // an adapter is free to send cleanup more than once; double-counting it would report a file
    // with a live handle as closed, and the drainer would move it out from under a writer
    table.MarkApplicationClosed(first.Handle);
    table.MarkApplicationClosed(first.Handle);
    table.MarkApplicationClosed(first.Handle);

    table.IsOpen(_PATH).Should().BeTrue("the second handle is still open, whatever the first one was told");
    table.Close(second.Handle);
    table.IsOpen(_PATH).Should().BeFalse();
  }

  [Test]
  [Category("Exception")]
  public void Close_GivenTheApplicationAlreadyClosed_ThenTheCountDoesNotGoNegative() {
    var table = new HandleTable();
    var handle = table.Open(_PATH, AccessMode.Write);

    table.MarkApplicationClosed(handle.Handle);
    table.Close(handle.Handle); // the deferred kernel close, finally

    table.IsOpen(_PATH).Should().BeFalse();

    // a fresh handle must still read as open — it would not if the count had gone negative
    var next = table.Open(_PATH, AccessMode.Write);
    table.IsOpen(_PATH).Should().BeTrue("a new handle on the same path is a new application holding it");
    table.Close(next.Handle);
    table.IsOpen(_PATH).Should().BeFalse();
  }

  [Test]
  [Category("Exception")]
  public void MarkApplicationClosed_GivenAHandleThatIsAlreadyGone_ThenItIsIgnored() {
    var table = new HandleTable();
    var handle = table.Open(_PATH, AccessMode.Write);
    table.Close(handle.Handle);

    // a cleanup racing behind a close must not throw, and must not disturb anything
    FluentActions.Invoking(() => table.MarkApplicationClosed(handle.Handle)).Should().NotThrow();
    table.IsOpen(_PATH).Should().BeFalse();
  }

  [Test]
  [Category("EdgeCase")]
  public void Close_WithoutAnyCleanup_ThenThePathStillStopsCountingAsOpen() {
    // FUSE's release is a prompt close and sends no separate cleanup, so Close alone has to be
    // enough — otherwise the Linux adapter would leak "open" files forever
    var table = new HandleTable();
    var handle = table.Open(_PATH, AccessMode.Write);

    table.Close(handle.Handle);

    table.IsOpen(_PATH).Should().BeFalse("an adapter that closes promptly needs no cleanup call");
  }

}
