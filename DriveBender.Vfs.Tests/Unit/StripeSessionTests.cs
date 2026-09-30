using DivisonM.Vfs;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The stripe session on its own: where blocks go, what reads return, and whether filling makes a
/// final disk hold exactly the file that was written.
/// </summary>
[TestFixture]
[Category("Unit")]
public class StripeSessionTests {

  private const int _BLOCK = 16;
  private const string _TEMP = "f.bin.TEMP.$DRIVEBENDER";
  private const string _HELPER = "f.bin.0.STRIPE.TEMP.$DRIVEBENDER";

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;
  private FakeVolumeIO _c = null!;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 20);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 20);
    this._c = new(Guid.NewGuid(), "c", "PHYS-C", capacity: 1L << 20);
    this._a.Seed(_TEMP, false, []); // a final's temp exists from Create on
  }

  private static byte[] _Data(int length, int seed = 1) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed))];

  private static void _Truncate(StripeSession.Member member, long length) => member.Volume.Truncate(member.Path, member.Shadow, length);

  /// <summary>One final on a, helpers on b and c; every block its own stripe unit unless told otherwise.</summary>
  private StripeSession _Session(Func<IVolumeIO, double>? load = null, int copiesPerBlock = 1, long unit = _BLOCK) => new([
      new(this._a, _TEMP, false, IsFinal: true),
      new(this._b, _HELPER, false, IsFinal: false),
      new(this._c, _HELPER, false, IsFinal: false),
    ], _BLOCK, copiesPerBlock, load, minimumUnit: unit);

  [Test]
  [Category("HappyPath")]
  public void Write_GivenOneBlockAndNothingBusy_ThenItGoesToTheFinalAndNothingNeedsFilling() {
    // the small-file case: an uncontended pool writes straight into the final
    var session = this._Session();
    session.Write(0, _Data(10));

    session.MissingOn(session.Finals.Single()).Should().Be(0);
    session.CreatedHelpers().Should().BeEmpty("no helper temp is created for a block that did not go there");
    this._b.FileExists(_HELPER, false).Should().BeFalse();
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenAWriteSpanningManyBlocks_ThenTheBlocksSpreadOverTheWholeGroup() {
    var session = this._Session();
    session.Write(0, _Data(_BLOCK * 9));

    Enumerable.Range(0, 9).Select(b => session.HoldersOf(b).Single().Volume.DisplayName).Distinct()
      .Should().BeEquivalentTo(["a", "b", "c"], "a write the size of nine blocks keeps all three disks busy");
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenStripeUnitsOfFourBlocks_ThenEachUnitIsOneContiguousPieceOnOneDisk() {
    // handing blocks out one by one alternated equally cheap disks and nothing coalesced
    var session = this._Session(unit: _BLOCK * 4);
    session.Write(0, _Data(_BLOCK * 12));

    for (var unit = 0; unit < 3; ++unit)
      Enumerable.Range(unit * 4, 4).Select(b => session.HoldersOf(b).Single().Volume.DisplayName).Distinct()
        .Should().HaveCount(1, $"unit {unit} is written as one piece");
    Enumerable.Range(0, 12).Select(b => session.HoldersOf(b).Single().Volume.DisplayName).Distinct()
      .Should().HaveCount(3, "and the units still spread over the group");
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenAWriteThatStartsInsideAUnitAlreadyBegun_ThenItsBlocksJoinThatUnitsDisk() {
    // unaligned writes are the norm; the first unit of the second write is half written already
    var session = this._Session(unit: _BLOCK * 8);
    session.Write(0, _Data(_BLOCK * 5));
    session.Write(_BLOCK * 5, _Data(_BLOCK * 11));

    Enumerable.Range(0, 8).Select(b => session.HoldersOf(b).Single().Volume.DisplayName).Distinct()
      .Should().HaveCount(1, "the unit that two writes share is still one contiguous piece on one disk");
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenAWriteSmallerThanAUnit_ThenItStaysOnOneDisk() {
    var session = this._Session(unit: _BLOCK * 64);
    session.Write(0, _Data(_BLOCK * 9));

    Enumerable.Range(0, 9).Select(b => session.HoldersOf(b).Single().Volume.DisplayName).Distinct()
      .Should().Equal(["a"], "a small write is one piece, on the final");
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenTheFinalIsBusy_ThenBlocksGoToHelpersAndFillingMakesTheFinalWhole() {
    var data = _Data(_BLOCK * 6 + 5);
    var session = this._Session(load: v => v == this._a ? 100 : 0);
    session.Write(0, data);

    session.MissingOn(session.Finals.Single()).Should().BeGreaterThan(0, "the busy final took few or none of the blocks");

    session.Fill(session.Finals.Single(), _Truncate);

    this._a.GetContent(_TEMP, false).Should().Equal(data, "after filling, the final's temp is the whole file, to the byte");
    session.MissingOn(session.Finals.Single()).Should().Be(0);
  }

  [Test]
  [Category("HappyPath")]
  public void Write_GivenTwoCopiesPerBlock_ThenEveryBlockIsOnTwoDifferentPhysicalDisks() {
    var session = this._Session(copiesPerBlock: 2);
    session.Write(0, _Data(_BLOCK * 8));

    for (var block = 0; block < 8; ++block)
      session.HoldersOf(block).Select(m => m.Volume.PhysicalVolumeId).Distinct()
        .Should().HaveCount(2, $"block {block} is acknowledged on two disks, as the ack count demands");
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenPartialWritesToTheSameBlock_ThenTheyLandTogetherAndReadBackAsOne() {
    var session = this._Session(load: v => v == this._a ? 100 : 0); // the first part goes to a helper
    session.Write(0, [1, 2, 3, 4]);
    session.Write(4, [5, 6, 7, 8]); // same block: must go where the first part went

    session.HoldersOf(0).Should().HaveCount(1);
    var back = new byte[8];
    session.Read(0, back).Should().Be(8);
    back.Should().Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
  }

  [Test]
  [Category("EdgeCase")]
  public void Write_GivenABlockIsRewrittenAfterBeingFilled_ThenTheFinalIsOwedItAgainAndEndsWithTheNewBytes() {
    var session = this._Session(load: v => v == this._a ? 100 : 0);
    session.Write(0, _Data(_BLOCK * 3, seed: 1));
    session.Fill(session.Finals.Single(), _Truncate);

    var newer = _Data(_BLOCK, seed: 99);
    session.Write(_BLOCK, newer);
    session.Fill(session.Finals.Single(), _Truncate);

    var expected = _Data(_BLOCK * 3, seed: 1);
    newer.CopyTo(expected, _BLOCK);
    this._a.GetContent(_TEMP, false).Should().Equal(expected, "a rewrite after filling must not be lost to the older copy");
  }

  [Test]
  [Category("EdgeCase")]
  public void Read_GivenHolesAndAnEnd_ThenHolesReadAsZerosAndNothingIsReadPastTheEnd() {
    var session = this._Session();
    session.Write(_BLOCK * 2, [9, 9]); // blocks 0 and 1 never written

    var back = new byte[_BLOCK * 3];
    session.Read(0, back).Should().Be(_BLOCK * 2 + 2);
    back.Take(_BLOCK * 2).Should().OnlyContain(b => b == 0);
    back.Skip(_BLOCK * 2).Take(2).Should().Equal(9, 9);
    session.Read(_BLOCK * 5, back).Should().Be(0);
  }

  [Test]
  [Category("EdgeCase")]
  public void SetLength_GivenTheFileShrinksAndGrowsAgain_ThenTheCutAwayBytesReadAsZeros() {
    var session = this._Session(load: v => v == this._a ? 100 : 0);
    session.Write(0, _Data(_BLOCK * 4));
    session.SetLength(_BLOCK + 3, _Truncate);
    session.SetLength(_BLOCK * 4, _Truncate);
    session.Fill(session.Finals.Single(), _Truncate);

    var expected = new byte[_BLOCK * 4];
    _Data(_BLOCK * 4).AsSpan(0, _BLOCK + 3).CopyTo(expected);
    this._a.GetContent(_TEMP, false).Should().Equal(expected, "bytes cut away by the shrink must not come back when the file grows again");
  }

  [Test]
  [Category("Exception")]
  public void Write_GivenAHelperFailsMidWrite_ThenItsShareGoesElsewhereAndTheWriteSucceeds() {
    this._b.BeforeOperation = (op, _) => {
      if (op is VolumeOp.Write or VolumeOp.OpenWrite)
        throw new PoolFsException(PoolFsError.IoError, "disk b died");
    };
    var data = _Data(_BLOCK * 6);
    var session = this._Session();
    session.Write(0, data);

    Enumerable.Range(0, 6).SelectMany(b => session.HoldersOf(b)).Should().NotContain(m => m.Volume == this._b);
    session.Fill(session.Finals.Single(), _Truncate);
    this._a.GetContent(_TEMP, false).Should().Equal(data);
  }

  [Test]
  [Category("Exception")]
  public void Fill_GivenTheOnlyDiskHoldingABlockFailedAfterwards_ThenFillingRefusesRatherThanPublishAHole() {
    var session = this._Session(load: v => v == this._a ? 100 : 0);
    session.Write(0, _Data(_BLOCK * 3));
    var onHelpers = Enumerable.Range(0, 3).SelectMany(b => session.HoldersOf(b)).Where(m => !m.IsFinal).Select(m => m.Volume).Distinct().ToArray();
    onHelpers.Should().NotBeEmpty();

    foreach (var helper in onHelpers.OfType<FakeVolumeIO>())
      helper.BeforeOperation = (op, _) => {
        if (op == VolumeOp.OpenRead)
          throw new PoolFsException(PoolFsError.IoError, "gone");
      };

    var fill = () => session.Fill(session.Finals.Single(), _Truncate);
    fill.Should().Throw<PoolFsException>("a final missing an acknowledged block must never be treated as whole");
  }

}
