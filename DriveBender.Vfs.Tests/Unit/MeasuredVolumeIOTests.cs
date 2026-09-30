using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Per-member latency measurement — what placement and read routing weigh. It measures; it never
/// changes a member's role. The pool used to have an "auto landing zone" that promoted the
/// measured-fastest disk to landing zone by rewriting the manifest; a landing zone is now only ever
/// a role the operator gives a disk.
/// </summary>
[TestFixture]
[Category("Unit")]
public class MeasuredVolumeIOTests {

  [Test]
  [Category("HappyPath")]
  public void MeasuredVolumeIO_GivenStreamTraffic_WhenObserved_ThenLatencyEwmaFills() {
    var fake = new FakeVolumeIO(Guid.NewGuid(), "v", "PHYS-1");
    var measured = new MeasuredVolumeIO(fake);

    using (var stream = measured.OpenWrite("f.bin", false, true)) {
      stream.Write([1, 2, 3], 0, 3);
      stream.Flush();
    }

    measured.Samples.Should().BeGreaterThan(0, "every storage-touching operation feeds the EWMA");
    measured.AverageLatencyMs.Should().BeGreaterThanOrEqualTo(0);
    fake.FileExists("f.bin", false).Should().BeTrue("the decorator is transparent");
  }

  [Test]
  [Category("EdgeCase")]
  public void MeasuredVolumeIO_GivenADiskSlowAtMovingDataButQuickAtMetadata_ThenItStillReadsSlowHoweverManyQuickOperationsFollow() {
    // placement's floor is 1 ms: a disk taking 3 ms per write must stay well above it even when
    // most of its recent operations were instant renames and truncates
    var measured = new MeasuredVolumeIO(new FakeVolumeIO(Guid.NewGuid(), "slow", "PHYS-1"));
    for (var i = 0; i < 3; ++i)
      measured.RecordLatency(3);
    for (var i = 0; i < 50; ++i)
      measured.RecordMetadataLatency(0.01);

    measured.AverageLatencyMs.Should().BeGreaterThan(1.4, "a disk slow at moving data does not become fast by renaming quickly");
  }

  [Test]
  [Category("HappyPath")]
  public void MeasuredVolumeIO_GivenOnlyOneKindMeasured_ThenThatKindIsTheReading() {
    var measured = new MeasuredVolumeIO(new FakeVolumeIO(Guid.NewGuid(), "one", "PHYS-1"));
    measured.RecordLatency(80);

    measured.AverageLatencyMs.Should().Be(80, "an unmeasured kind does not dilute the one that was measured");
  }

  [Test]
  [Category("Exception")]
  public void Config_GivenAnOldManifestStillAskingForAnAutomaticLandingZone_ThenItLoadsAndTheKeyIsIgnored() {
    // pools created while the feature existed carry the key; they must keep mounting
    var config = ConfigResolver.ResolveEffective(null, """{ "placement": { "autoLandingZone": true, "strategy": "round-robin" } }""");

    config.Placement.Should().NotBeNull();
    config.Placement!.Strategy.Should().Be(PlacementStrategy.RoundRobin, "the rest of the block still applies");
    config.Placement.ExtensionData.Should().ContainKey("autoLandingZone", "kept aside, and nothing reads it");
  }

}
