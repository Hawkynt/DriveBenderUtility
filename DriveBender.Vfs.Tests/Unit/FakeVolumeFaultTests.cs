using DivisonM.Vfs;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Verifies the fault-injection surface the SAFE-* tests build on (TST-FAULT).</summary>
[TestFixture]
[Category("Unit")]
public class FakeVolumeFaultTests {

  private FakeVolumeIO _volume = null!;

  [SetUp]
  public void SetUp() => this._volume = new(Guid.NewGuid(), "faulty", "VOL-F", capacity: 1024);

  [Test]
  [Category("Exception")]
  public void Write_GivenFullVolume_WhenWriting_ThenNoSpace() {
    using var stream = this._volume.OpenWrite("big.bin", false, true);
    var act = () => stream.Write(new byte[2048], 0, 2048);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NoSpace);
  }

  [Test]
  [Category("Exception")]
  public void AnyOp_GivenOfflineVolume_WhenCalled_ThenOffline() {
    this._volume.Seed("f.txt", false, [1]);
    this._volume.IsOnline = false;

    var act = () => this._volume.OpenRead("f.txt", false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.Offline);
  }

  [Test]
  [Category("Exception")]
  public void FailNext_GivenInjectedFsyncFault_WhenFlushing_ThenFailsOnceThenRecovers() {
    this._volume.FailNext(VolumeOp.Flush, PoolFsError.IoError);
    using var stream = this._volume.OpenWrite("f.txt", false, true);
    stream.Write([1], 0, 1);

    var act = () => stream.Flush();
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.IoError);
    act.Should().NotThrow("one-shot faults clear after firing");
  }

  [Test]
  [Category("EdgeCase")]
  public void SimulateCrash_GivenUnflushedWrite_WhenCrashed_ThenUnflushedContentLost() {
    this._volume.Seed("f.txt", false, [1, 2, 3]);
    using (var stream = this._volume.OpenWrite("f.txt", false, false)) {
      stream.Seek(0, SeekOrigin.Begin);
      stream.Write([9, 9, 9], 0, 3);
      // no flush — dirty in the page cache only
    }

    this._volume.SimulateCrash();

    this._volume.GetContent("f.txt", false).Should().Equal(new byte[] { 1, 2, 3 }, "an unflushed write must not survive power loss");
  }

  [Test]
  [Category("EdgeCase")]
  public void SimulateCrash_GivenNeverFlushedNewFile_WhenCrashed_ThenFileVanishes() {
    using (var stream = this._volume.OpenWrite("new.txt", false, true))
      stream.Write([1], 0, 1);

    this._volume.SimulateCrash();

    this._volume.FileExists("new.txt", false).Should().BeFalse();
  }

  [Test]
  [Category("HappyPath")]
  public void SimulateCrash_GivenFlushedWrite_WhenCrashed_ThenContentSurvives() {
    using (var stream = this._volume.OpenWrite("durable.txt", false, true)) {
      stream.Write([5, 5], 0, 2);
      stream.Flush();
    }

    this._volume.SimulateCrash();

    this._volume.GetContent("durable.txt", false).Should().Equal(new byte[] { 5, 5 }, "a flushed write is a durability promise (SAFE-FSYNC)");
  }

  [Test]
  [Category("HappyPath")]
  public void SimulateCrash_GivenAtomicReplace_WhenCrashed_ThenPublishedContentSurvives() {
    var temp = "f.txt." + DriveBender.DriveBenderConstants.TEMP_EXTENSION;
    using (var stream = this._volume.OpenWrite(temp, false, true))
      stream.Write([8], 0, 1);

    this._volume.AtomicReplace(temp, "f.txt", false);
    this._volume.SimulateCrash();

    this._volume.GetContent("f.txt", false).Should().Equal(new byte[] { 8 }, "publication via atomic rename is durable (SAFE-ATOMIC)");
  }

  [Test]
  [Category("Exception")]
  public void InjectPartialWrite_GivenTornWrite_WhenWriting_ThenOnlyPrefixLandsAndErrorRaised() {
    this._volume.InjectPartialWrite(2);
    using var stream = this._volume.OpenWrite("torn.bin", false, true);

    var act = () => stream.Write([1, 2, 3, 4], 0, 4);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.IoError);

    this._volume.GetContent("torn.bin", false).Should().Equal(1, 2);
  }

}
