using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// Publishing a staged create (§SAFE-ATOMIC): the content is written under a temp name and every
/// copy is renamed to the final one in a single step, so the file never appears half-written.
///
/// The rename is the moment the file becomes real, and it is done once per copy — which means it
/// can fail part-way through. What happens then is the question here.
/// </summary>
[TestFixture]
[Category("Unit")]
public class StagedPublishTests {

  private static readonly Guid _pool = Guid.Parse("dddddddd-1111-2222-3333-555555555555");

  private FakeVolumeIO _a = null!;
  private FakeVolumeIO _b = null!;

  [SetUp]
  public void SetUp() {
    this._a = new(Guid.NewGuid(), "a", "PHYS-A", capacity: 1L << 20);
    this._b = new(Guid.NewGuid(), "b", "PHYS-B", capacity: 1L << 20);
  }

  private PoolFileSystem _Mounted() {
    var cache = new CacheInstance("sp" + Guid.NewGuid().ToString("N"),
      new() { Size = "262144", BlockSize = "16", MetadataEntries = 1000, MetadataTtl = "5m" });
    var fs = new PoolFileSystem(_pool, [new(this._a), new(this._b)], cache,
      ConfigResolver.ResolveEffective(null,
        """{ "duplication": 2, "placement": { "shadowNeverSamePhysical": false }, "trash": { "enabled": false } }"""));
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private byte[]? _AnyCopy(FakeVolumeIO volume, string path)
    => volume.GetContent(path, false) ?? volume.GetContent(path, true);

  [Test]
  [Category("Exception")]
  public void Publish_GivenARenameFails_ThenTheFileIsStillPublishedAfterwards() {
    // The staging map is the only record that this path has a temp waiting to become real, and the
    // publish removes the entry BEFORE renaming the copies. A rename that throws — a member that
    // dropped, a full disk, a bad sector — therefore takes that record with it: some copies may
    // already carry the final name and some still the temp, the create's journal intent is left
    // open, and nothing in the running pool will ever finish the job.
    var fs = this._Mounted();

    var handle = fs.Create("report.doc", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [1, 2, 3, 4], 0, WriteMode.Normal);

    this._a.FailNext(VolumeOp.AtomicReplace, PoolFsError.IoError);
    this._b.FailNext(VolumeOp.AtomicReplace, PoolFsError.IoError);

    var close = () => fs.Close(handle);
    close.Should().Throw<PoolFsException>("the copies could not be renamed into place");

    // whatever went wrong has passed: a clean unmount publishes what is still staged
    this._a.ClearFaults();
    this._b.ClearFaults();
    fs.Unmount();

    var holders = new[] { this._a, this._b }.Where(v => this._AnyCopy(v, "report.doc") != null).ToArray();
    holders.Should().NotBeEmpty(
      "the bytes were written and the create was journalled — a rename that failed once must not "
      + "leave the file stranded under its temp name with nothing left holding a note to finish it");

    foreach (var volume in holders)
      this._AnyCopy(volume, "report.doc").Should().Equal(new byte[] { 1, 2, 3, 4 },
        "and what is published is what was written");
  }

}
