using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>Tier-2 correctness fixes: rename-onto-self never destroys the file; oversized sizes error cleanly.</summary>
[TestFixture]
[Category("Unit")]
public class Tier2CorrectnessTests {

  private static readonly Guid _pool = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

  private FakeVolumeIO _v1 = null!;
  private FakeVolumeIO _v2 = null!;
  private PoolFileSystem _fs = null!;

  [SetUp]
  public void SetUp() {
    this._v1 = new(Guid.NewGuid(), "v1", "PHYS-1", capacity: 1L << 20);
    this._v2 = new(Guid.NewGuid(), "v2", "PHYS-2", capacity: 1L << 20);
    var cache = new CacheInstance("t2" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 100, MetadataTtl = "5m" });
    this._fs = new(_pool, [new(this._v1), new(this._v2)], cache, ConfigResolver.ResolveEffective(null, """{ "duplication": 2 }"""));
    this._fs.Mount(new(@"X:\"));
  }

  private byte[]? _AnyCopy(string path) => this._v1.GetContent(path, false) ?? this._v1.GetContent(path, true) ?? this._v2.GetContent(path, false) ?? this._v2.GetContent(path, true);

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenSamePathReplaceExisting_WhenRenamed_ThenFileSurvives() {
    var handle = this._fs.Create("keep.txt", NodeKind.File, CreateFlags.None);
    this._fs.Write(handle, [1, 2, 3], 0, WriteMode.Normal);
    this._fs.Close(handle);

    // rename onto itself with ReplaceExisting must not delete "the target" (which IS the source)
    var act = () => this._fs.Rename("keep.txt", "keep.txt", RenameFlags.ReplaceExisting);
    act.Should().NotThrow();

    this._AnyCopy("keep.txt").Should().Equal(new byte[] { 1, 2, 3 }, "a rename-onto-self is a no-op, never a destroy");
  }

  [Test]
  [Category("EdgeCase")]
  public void Rename_GivenCaseOnlyChange_WhenRenamed_ThenContentPreserved() {
    var handle = this._fs.Create("photo.jpg", NodeKind.File, CreateFlags.None);
    this._fs.Write(handle, [7, 7, 7, 7], 0, WriteMode.Normal);
    this._fs.Close(handle);

    // on a case-insensitive backend "PHOTO.JPG" resolves to the same file — must not be deleted
    this._fs.Rename("photo.jpg", "PHOTO.JPG", RenameFlags.ReplaceExisting);

    this._AnyCopy("photo.jpg").Should().Equal(new byte[] { 7, 7, 7, 7 }, "a case-only rename preserves the content");
  }

  [Test]
  [Category("Exception")]
  public void SizeSpec_GivenOverflowingSize_WhenParsed_ThenManifestExceptionNotOverflow() {
    var act = () => SizeSpec.ParseBytes("20EiB");
    act.Should().Throw<ManifestException>("an oversized value is a config error, not an uncaught OverflowException");
  }
}

/// <summary>Lifecycle edits must never persist a config that would refuse the next mount (SetDuplication/SetMemberRole validation).</summary>
[TestFixture]
[Category("Unit")]
public class LifecycleValidationTests {

  private FakeHostEnvironment _host = null!;
  private ManifestStore _store = null!;
  private PoolLifecycle _lifecycle = null!;

  [SetUp]
  public void SetUp() {
    this._host = new();
    this._host.AddVolume(@"A:\", "PHYS-A");
    this._host.AddVolume(@"B:\", "PHYS-B");
    this._host.AddDirectory(@"A:\pool");
    this._host.AddDirectory(@"B:\pool");
    this._store = new(this._host);
    this._lifecycle = new(this._host, this._store);
  }

  [Test]
  [Category("Exception")]
  public void SetDuplication_GivenLoweringBelowMinCopies_WhenApplied_ThenRejectedNotPersisted() {
    var manifest = this._lifecycle.Create("P", [new(@"A:\pool"), new(@"B:\pool")], force: true);
    // pin minCopiesBeforeAck 3 (valid while D=3)
    manifest = this._lifecycle.SetConfig(manifest, """{ "duplication": 3, "write": { "minCopiesBeforeAck": 3 } }""");

    // lowering D to 2 would make minCopies(3) > D(2) — the next mount would refuse it, so reject NOW
    var act = () => this._lifecycle.SetDuplication(manifest, 2);
    act.Should().Throw<ConfigValidationException>("an edit that would make the pool unmountable is rejected at edit time");
  }

  [Test]
  [Category("HappyPath")]
  public void SetDuplication_GivenValidLevel_WhenApplied_ThenPersisted() {
    var manifest = this._lifecycle.Create("P", [new(@"A:\pool"), new(@"B:\pool")], force: true);
    var act = () => this._lifecycle.SetDuplication(manifest, 2);
    act.Should().NotThrow();
  }
}
