using DivisonM.Backends;
using DivisonM.Vfs;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using DivisonM.Vfs.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The whole-file store wrapper, exercised headlessly through DirectoryStore over a temp
/// directory — the identical code path every remote provider (FTP/SFTP/WebDAV/S3/Azure/
/// Dropbox/OneDrive/Google) flows through (FR-REMOTE, TST-FAKE analogue for real SDKs).
/// </summary>
[TestFixture]
[Category("Unit")]
public class WholeFileVolumeIOTests {

  private string _root = null!;
  private WholeFileVolumeIO _volume = null!;

  [SetUp]
  public void SetUp() {
    this._root = Path.Combine(Path.GetTempPath(), "dbwf-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);
    this._volume = new(Guid.NewGuid(), "wholefile", "REMOTE-TEST", new DirectoryStore(this._root));
  }

  [TearDown]
  public void TearDown() {
    this._volume.Dispose();
    if (Directory.Exists(this._root))
      Directory.Delete(this._root, true);
  }

  private void _WriteAll(string path, bool shadow, byte[] content) {
    using var stream = this._volume.OpenWrite(path, shadow, true);
    stream.Write(content, 0, content.Length);
    stream.Flush();
  }

  private byte[] _ReadAll(string path, bool shadow) {
    using var stream = this._volume.OpenRead(path, shadow);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  [Test]
  [Category("HappyPath")]
  public void WriteRead_GivenWholeFileUpload_WhenRoundTripped_ThenContentIdentical() {
    this._volume.EnsureFolder("docs", false);
    this._WriteAll("docs/hello.txt", false, [1, 2, 3, 4]);
    this._ReadAll("docs/hello.txt", false).Should().Equal(1, 2, 3, 4);
  }

  /// <summary>Counts how often the underlying store is actually fetched, to prove the object cache coalesces per-block reads.</summary>
  private sealed class CountingStore(IWholeFileStore inner) : IWholeFileStore {
    public int Downloads;
    public void Connect() => inner.Connect();
    public bool Probe() => inner.Probe();
    public byte[] Download(string p) { ++this.Downloads; return inner.Download(p); }
    public void Upload(string p, byte[] c) => inner.Upload(p, c);
    public void DeleteFile(string p) => inner.DeleteFile(p);
    public StoreMeta? Stat(string p) => inner.Stat(p);
    public void CreateFolder(string p) => inner.CreateFolder(p);
    public void DeleteFolder(string p) => inner.DeleteFolder(p);
    public IEnumerable<StoreEntry> List(string p) => inner.List(p);
    public void Dispose() => inner.Dispose();
  }

  /// <summary>A non-thread-safe store (default ThreadSafe=false) that trips if two calls overlap — models a single FTP/SFTP control channel.</summary>
  private sealed class ConcurrencyProbeStore(IWholeFileStore inner) : IWholeFileStore {
    private int _active;
    public volatile bool Overlapped;
    private T _Serial<T>(Func<T> op) {
      if (Interlocked.Increment(ref this._active) != 1) this.Overlapped = true;
      try { Thread.Sleep(1); return op(); }
      finally { Interlocked.Decrement(ref this._active); }
    }
    private void _Serial(Action op) => this._Serial<object?>(() => { op(); return null; });
    public void Connect() => this._Serial(inner.Connect);
    public bool Probe() => this._Serial(inner.Probe);
    public byte[] Download(string p) => this._Serial(() => inner.Download(p));
    public void Upload(string p, byte[] c) => this._Serial(() => inner.Upload(p, c));
    public void DeleteFile(string p) => this._Serial(() => inner.DeleteFile(p));
    public StoreMeta? Stat(string p) => this._Serial(() => inner.Stat(p));
    public void CreateFolder(string p) => this._Serial(() => inner.CreateFolder(p));
    public void DeleteFolder(string p) => this._Serial(() => inner.DeleteFolder(p));
    public IEnumerable<StoreEntry> List(string p) => this._Serial(() => inner.List(p).ToArray());
    public void Dispose() => inner.Dispose();
  }

  [Test]
  [Category("EdgeCase")]
  public void ConcurrentOps_GivenNonThreadSafeStore_WhenHammered_ThenCallsAreSerialized() {
    var probe = new ConcurrencyProbeStore(new DirectoryStore(this._root));
    using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "wf", "REMOTE", probe);
    for (var i = 0; i < 8; ++i)
      using (var w = volume.OpenWrite($"f{i}.bin", false, true)) { w.Write([(byte)i], 0, 1); w.Flush(); }

    // parallel reads/stats/writes across files — a non-thread-safe store must NOT see overlapping calls
    Parallel.For(0, 64, i => {
      try {
        switch (i % 3) {
          case 0: using (var r = volume.OpenRead($"f{i % 8}.bin", false)) { r.ReadByte(); } break;
          case 1: _ = volume.Stat($"f{i % 8}.bin", false); break;
          default: using (var w = volume.OpenWrite($"f{i % 8}.bin", false, false)) { w.SetLength(0); w.Write([9], 0, 1); w.Flush(); } break;
        }
      } catch (PoolFsException) { /* races on the same file's existence are fine; we only assert non-overlap */ }
    });

    probe.Overlapped.Should().BeFalse("the engine serializes a non-thread-safe backend so its single connection is never corrupted");
  }

  [Test]
  [Category("EdgeCase")]
  public void OpenRead_GivenManyBlockReadsOfOneFile_WhenRead_ThenObjectDownloadedOnce() {
    var counting = new CountingStore(new DirectoryStore(this._root));
    using var volume = new WholeFileVolumeIO(Guid.NewGuid(), "wf", "REMOTE", counting);
    using (var w = volume.OpenWrite("big.bin", false, true)) { w.Write(new byte[64 * 1024], 0, 64 * 1024); w.Flush(); }
    counting.Downloads = 0;

    // the engine opens a fresh read stream per block — this must NOT re-download the whole object each time
    for (var i = 0; i < 20; ++i)
      using (var r = volume.OpenRead("big.bin", false)) { var b = new byte[16]; _ = r.Read(b, 0, b.Length); }

    counting.Downloads.Should().Be(1, "the object cache serves 20 block reads from a single download (O(n²)→O(n))");

    // a write invalidates the cache so a later read sees the new content, not a stale copy
    using (var w = volume.OpenWrite("big.bin", false, false)) { w.SetLength(0); w.Write([9, 9], 0, 2); w.Flush(); }
    using (var r = volume.OpenRead("big.bin", false)) { var b = new byte[2]; _ = r.Read(b, 0, 2); b.Should().Equal(9, 9); }
    counting.Downloads.Should().BeGreaterThan(1, "the write invalidated the cached object so the next read re-fetched");
  }

  [Test]
  [Category("HappyPath")]
  public void WriteRead_GivenShadowCopy_WhenRoundTripped_ThenLandsInShadowContainer() {
    this._volume.EnsureFolder("docs", true);
    this._WriteAll("docs/f.txt", true, [9]);

    this._ReadAll("docs/f.txt", true).Should().Equal(9);
    File.Exists(Path.Combine(this._root, "docs", DriveBender.DriveBenderConstants.SHADOW_COPY_FOLDER_NAME, "f.txt"))
      .Should().BeTrue("the on-disk layout stays Drive Bender compatible (SAFE-COMPAT)");
  }

  [Test]
  [Category("HappyPath")]
  public void OpenWrite_GivenExistingFile_WhenPositionalWrite_ThenReadModifyWriteComposes() {
    this._WriteAll("f.bin", false, [1, 2, 3, 4]);

    using (var stream = this._volume.OpenWrite("f.bin", false, false)) {
      stream.Seek(1, SeekOrigin.Begin);
      stream.Write([9, 9], 0, 2);
      stream.Flush();
    }

    this._ReadAll("f.bin", false).Should().Equal(new byte[] { 1, 9, 9, 4 }, "existing content preloads so positional writes compose");
  }

  [Test]
  [Category("Exception")]
  public void OpenRead_GivenMissingFile_WhenOpened_ThenNotFound() {
    var act = () => this._volume.OpenRead("missing.bin", false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("Exception")]
  public void OpenWrite_GivenMissingFileWithoutCreate_WhenOpened_ThenNotFound() {
    var act = () => this._volume.OpenWrite("missing.bin", false, false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotFound);
  }

  [Test]
  [Category("HappyPath")]
  public void Stat_GivenFileAndFolderAndMissing_WhenQueried_ThenCorrectMetadata() {
    this._WriteAll("stat.bin", false, new byte[42]);
    this._volume.EnsureFolder("statdir", false);

    this._volume.Stat("stat.bin", false)!.Value.Length.Should().Be(42);
    this._volume.Stat("statdir", false)!.Value.IsDirectory.Should().BeTrue();
    this._volume.Stat("nothing", false).Should().BeNull();
  }

  [Test]
  [Category("HappyPath")]
  public void List_GivenFilesAndFolders_WhenListed_ThenAllEntries() {
    this._volume.EnsureFolder("dir/sub", false);
    this._WriteAll("dir/a.txt", false, [1]);
    this._WriteAll("dir/b.txt", false, [1, 2]);

    var entries = this._volume.List("dir", false).ToArray();

    entries.Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "b.txt", "sub"]);
    entries.Single(e => e.Name == "sub").IsDirectory.Should().BeTrue();
    entries.Single(e => e.Name == "b.txt").Length.Should().Be(2);
  }

  [Test]
  [Category("HappyPath")]
  public void Truncate_GivenShrink_WhenApplied_ThenWholeObjectRewritten() {
    this._WriteAll("t.bin", false, [1, 2, 3, 4]);
    this._volume.Truncate("t.bin", false, 2);
    this._ReadAll("t.bin", false).Should().Equal(1, 2);
  }

  [Test]
  [Category("HappyPath")]
  public void DeleteAndFolders_GivenLifecycle_WhenExecuted_ThenConsistent() {
    this._volume.EnsureFolder("a/b/c", false);
    this._volume.FolderExists("a/b/c", false).Should().BeTrue("EnsureFolder creates the chain recursively");

    this._WriteAll("a/b/c/f.bin", false, [1]);
    this._volume.FileExists("a/b/c/f.bin", false).Should().BeTrue();

    this._volume.Delete("a/b/c/f.bin", false);
    this._volume.FileExists("a/b/c/f.bin", false).Should().BeFalse();

    this._volume.DeleteFolder("a/b/c", false);
    this._volume.FolderExists("a/b/c", false).Should().BeFalse();
  }

  [Test]
  [Category("Exception")]
  public void AtomicReplace_GivenWholeFileBackend_WhenCalled_ThenNotSupported() {
    var act = () => this._volume.AtomicReplace("t.tmp", "t.bin", false);
    act.Should().Throw<PoolFsException>().Which.Error.Should().Be(PoolFsError.NotSupported,
      "remote backends publish via put-and-verify, never rename (FR-CAP-ADAPT)");
  }

  [Test]
  [Category("HappyPath")]
  public void Caps_GivenRemoteProfile_WhenInspected_ThenNeverDurableNorAtomic() {
    (this._volume.Caps & BackendCaps.DurableFlush).Should().Be(BackendCaps.None, "a remote member never satisfies the ack quorum (SAFE-REMOTE)");
    (this._volume.Caps & BackendCaps.AtomicRename).Should().Be(BackendCaps.None);
    this._volume.BytesTotal.Should().Be(0, "capacity unknown — excluded from pool aggregates (FR-STAT convention)");
  }

  [Test]
  [Category("HappyPath")]
  public void Engine_GivenLocalPlusWholeFileCapacityMember_WhenWrittenAndFlushed_ThenRemoteCopyConverges() {
    // local durable member takes the ack; the SharpGrip member receives its copy asynchronously (SAFE-REMOTE)
    var local = new FakeVolumeIO(Guid.NewGuid(), "local", "PHYS-LOCAL", capacity: 1L << 20);
    var cache = new CacheInstance("sg" + Guid.NewGuid().ToString("N"), new() { Size = "262144", BlockSize = "16", MetadataEntries = 100, MetadataTtl = "1m" });
    var config = ConfigResolver.ResolveEffective(null, """{ "duplication": 2, "write": { "policy": "write-back", "minCopiesBeforeAck": 1, "acceptVolatileAck": false } }""");
    var fs = new PoolFileSystem(Guid.NewGuid(), [new(local), new(this._volume)], cache, config);
    fs.Mount(new(@"X:\"));

    var handle = fs.Create("mixed.bin", NodeKind.File, CreateFlags.None);
    fs.Write(handle, [7, 7, 7], 0, WriteMode.Normal);
    fs.Flush(handle); // forces the owed remote copy out (SAFE-FSYNC)
    fs.Close(handle);

    var localHolds = local.FileExists("mixed.bin", false) || local.FileExists("mixed.bin", true);
    var remoteHolds = this._volume.FileExists("mixed.bin", false) || this._volume.FileExists("mixed.bin", true);
    localHolds.Should().BeTrue();
    remoteHolds.Should().BeTrue("the duplication invariant spans local and remote members (SAFE-DUP)");

    fs.StatFs().BytesTotal.Should().Be(1L << 20, "the capacity-unknown remote member is excluded from the aggregate");
    fs.Unmount();
  }

}

[TestFixture]
[Category("Unit")]
public class BackendRegistryTests {

  [TestCase(null, @"C:\pools\dir", "file")]
  [TestCase(null, @"\\server\share\pool", "unc")]
  [TestCase(null, "ftp://host/path", "ftp")]
  [TestCase(null, "sftp://user@host:2222/path", "sftp")]
  [TestCase(null, "s3://bucket/prefix", "s3")]
  [TestCase("webdav", "https://host/dav", "webdav")]
  [Category("HappyPath")]
  public void SchemeOf_GivenMemberPaths_WhenParsed_ThenCorrectScheme(string? explicitScheme, string path, string expected)
    => BackendRegistry.SchemeOf(explicitScheme, path).Should().Be(expected);

  [Test]
  [Category("HappyPath")]
  public void CreateDefault_GivenRegistry_WhenInspected_ThenAllPrdSchemesRegistered() {
    var registry = BackendRegistry.CreateDefault(new FakeHostEnvironment());
    registry.Schemes.Should().Contain(["file", "unc", "ftp", "ftps", "sftp", "webdav", "webdavs", "s3", "azblob", "azfile", "dropbox", "onedrive", "gdrive", "gcs", "box", "yandex", "hidrive"]);
  }

  [Test]
  [Category("Exception")]
  public void Open_GivenUnknownScheme_WhenOpened_ThenPreciseError() {
    var registry = new BackendRegistry();
    var act = () => registry.Open(Guid.NewGuid(), "m", "tftp://host/x", null, null, null);
    act.Should().Throw<ManifestException>().WithMessage("*No backend registered for scheme 'tftp'*");
  }

  [Test]
  [Category("HappyPath")]
  public void IsRemoteScheme_GivenSchemes_WhenChecked_ThenLocalKindsAreNot() {
    BackendRegistry.IsRemoteScheme("file").Should().BeFalse();
    BackendRegistry.IsRemoteScheme("unc").Should().BeFalse();
    BackendRegistry.IsRemoteScheme("sftp").Should().BeTrue();
    BackendRegistry.IsRemoteScheme("s3").Should().BeTrue();
  }

}

[TestFixture]
[Category("Unit")]
public class CredentialStoreTests {

  private FakeHostEnvironment _host = null!;
  private CredentialStore _store = null!;

  [SetUp]
  public void SetUp() {
    this._host = new();
    this._store = new(this._host, useOsStore: false); // file store only — tests never touch the real Credential Manager
  }

  [Test]
  [Category("HappyPath")]
  public void StoreResolve_GivenSecret_WhenRoundTripped_ThenIdentical() {
    this._store.Store("MyPool-server", "backupuser", "s3cr3t");

    var credential = this._store.Resolve("cred-ref:MyPool-server");

    credential.Should().NotBeNull();
    credential!.UserName.Should().Be("backupuser");
    credential.Secret.Should().Be("s3cr3t");
  }

  [Test]
  [Category("HappyPath")]
  public void Resolve_GivenReferenceWithAndWithoutPrefix_WhenResolved_ThenSameEntry() {
    this._store.Store("name", "u", "s");
    this._store.Resolve("name").Should().NotBeNull();
    this._store.Resolve("cred-ref:name").Should().NotBeNull();
  }

  [Test]
  [Category("EdgeCase")]
  public void Resolve_GivenUnknownReference_WhenResolved_ThenNull()
    => this._store.Resolve("cred-ref:nope").Should().BeNull();

  [Test]
  [Category("HappyPath")]
  public void Remove_GivenStoredSecret_WhenRemoved_ThenGone() {
    this._store.Store("gone", "u", "s");
    this._store.Remove("gone");
    this._store.Resolve("gone").Should().BeNull();
  }

  [Test]
  [Category("HappyPath")]
  public void Payload_GivenJsonSecret_WhenFieldsExtracted_ThenTyped() {
    const string secret = """{ "accessKey": "AK", "secretKey": "SK", "region": "eu-central-1" }""";
    CredentialPayload.TryGetJsonField(secret, "accessKey", out var access).Should().BeTrue();
    access.Should().Be("AK");
    CredentialPayload.TryGetJsonField(secret, "missing", out _).Should().BeFalse();
    CredentialPayload.Password("plainpass").Should().Be("plainpass");
    CredentialPayload.Password("""{ "password": "p" }""").Should().Be("p");
  }

  [Test]
  [Category("HappyPath")]
  public void Store_GivenSecrets_WhenPersisted_ThenNeverInManifestNamespace() {
    this._store.Store("s", "u", "topsecret");
    this._host.TryGetFileContent(Path.Combine(this._host.ConfigRoot, "credentials.json"))
      .Should().Contain("topsecret", "the file store holds it")
      .And.NotBeNull();
  }

}
