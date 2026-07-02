using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class ReadAheadTests {

  [Test]
  [Category("HappyPath")]
  public void OnRead_GivenSequentialReads_WhenSustained_ThenWindowRampsToMax() {
    var state = new ReadAheadState(minWindowBytes: 1024, maxWindowBytes: 8192, adaptive: true);

    state.OnRead(0, 512).Should().Be(1024, "the first read prefetches the min window");
    state.OnRead(512, 512).Should().Be(2048, "a sequential hit doubles the window");
    state.OnRead(1024, 512).Should().Be(4096);
    state.OnRead(1536, 512).Should().Be(8192);
    state.OnRead(2048, 512).Should().Be(8192, "the window is capped at maxWindow");
  }

  [Test]
  [Category("EdgeCase")]
  public void OnRead_GivenRandomAccess_WhenDetected_ThenPrefetchStopsAndWindowCollapses() {
    var state = new ReadAheadState(1024, 8192, adaptive: true);
    state.OnRead(0, 512);
    state.OnRead(512, 512);

    state.OnRead(100_000, 512).Should().Be(0, "random access must not prefetch");
    state.CurrentWindowBytes.Should().Be(1024, "the window collapses back to min");
  }

  [Test]
  [Category("EdgeCase")]
  public void OnRead_GivenNonAdaptiveConfig_WhenSequential_ThenWindowStaysAtMin() {
    var state = new ReadAheadState(1024, 8192, adaptive: false);
    state.OnRead(0, 512);
    state.OnRead(512, 512).Should().Be(1024);
    state.CurrentWindowBytes.Should().Be(1024);
  }

  [Test]
  [Category("HappyPath")]
  public void OnRead_GivenResumeAfterRandomJump_WhenSequentialAgain_ThenRampsFromMin() {
    var state = new ReadAheadState(1024, 8192, adaptive: true);
    state.OnRead(0, 512);
    state.OnRead(512, 512);
    state.OnRead(9000, 512); // jump
    state.OnRead(9512, 512).Should().Be(2048, "sequential flow after the jump ramps again from min");
  }

}
