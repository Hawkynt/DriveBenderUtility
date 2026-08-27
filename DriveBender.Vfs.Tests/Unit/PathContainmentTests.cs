using DivisonM.Vfs;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// SEC-PATH: a pool-relative path may never address anything outside the member root.
///
/// This matters because pool-relative paths are not all ours. A member can be a remote store —
/// SFTP, FTP, WebDAV, S3, a cloud drive — and the names in its directory listings come from the
/// far end. A hostile, compromised or man-in-the-middled server answers a listing with
/// <c>../../../.ssh/authorized_keys</c>, the engine treats it as a file in the pool, and healing or
/// rebalancing writes that file onto a LOCAL member. Without containment the write lands wherever
/// the name points, which is the same defect as the SSH.NET recursive-SCP advisory
/// (GHSA / CVE-2019-6111 class) — arbitrary file write driven purely by what a server says its
/// files are called.
///
/// The property asserted is deliberately NOT "the call is refused". A hostile name may equally be
/// normalised into something harmless — a leading separator is trimmed, so <c>/etc/cron.d/x</c>
/// becomes the pool-relative <c>etc/cron.d/x</c> and the write legitimately proceeds INSIDE the
/// member. Demanding refusal would be asserting an implementation detail and would fail on
/// perfectly safe behaviour. The property that actually protects the user is the one checked
/// against the filesystem afterwards: nothing outside the member root is ever created, written,
/// moved, deleted or disclosed.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Regression")]
public class PathContainmentTests {

  private string _sandbox = null!;
  private string _root = null!;
  private string _neighbour = null!;
  private LocalVolumeIO _volume = null!;

  [SetUp]
  public void SetUp() {
    // the member root sits INSIDE a sandbox next to a second directory, so an escape has somewhere
    // recognisable to land — one level up is exactly where '../' arrives
    this._sandbox = Path.Combine(Path.GetTempPath(), "dbu-secpath-" + Guid.NewGuid().ToString("N"));
    this._root = Path.Combine(this._sandbox, "member");
    this._neighbour = Path.Combine(this._sandbox, "neighbour");
    Directory.CreateDirectory(this._root);
    Directory.CreateDirectory(this._neighbour);
    this._volume = new(Guid.NewGuid(), "test", this._root, "PHYS-TEST");
  }

  [TearDown]
  public void TearDown() {
    try {
      Directory.Delete(this._sandbox, true);
    } catch (IOException) {
      // best-effort cleanup
    }
  }

  /// <summary>
  /// Names a hostile server can put in a directory listing. Each one is a real technique: plain
  /// traversal, traversal hidden behind a legitimate-looking segment, the Windows separator (which
  /// a POSIX server is free to send), an absolute path, a drive-qualified path, and an alternate
  /// data stream that addresses a different file entirely.
  /// </summary>
  private static IEnumerable<string> _HostileNames => [
    "../escaped.txt",
    "../neighbour/escaped.txt",
    "../../escaped.txt",
    "documents/../../escaped.txt",
    "..\\escaped.txt",
    "..\\..\\neighbour\\escaped.txt",
    "/etc/cron.d/escaped",
    "\\escaped.txt",
    "C:\\Windows\\Temp\\escaped.txt",
    "//server/share/escaped.txt",
  ];

  /// <summary>
  /// Runs an operation that is expected to be contained, and reports whether it was REFUSED.
  ///
  /// Refusal is not the contract. A hostile name may equally be normalised into something harmless
  /// — <c>PoolPaths.Normalize</c> trims a leading separator, so <c>/etc/cron.d/x</c> becomes the
  /// pool-relative <c>etc/cron.d/x</c> and the operation legitimately proceeds INSIDE the member.
  /// The contract is only ever that nothing is touched outside the root, which is asserted against
  /// the filesystem rather than against which exception came back.
  /// </summary>
  private static bool _Attempt(Action operation) {
    try {
      operation();
      return false;
    } catch (PoolFsException) {
      return true;
    } catch (IOException) {
      return true; // an OS-level refusal is also acceptable — the path never became a file
    } catch (UnauthorizedAccessException) {
      return true;
    }
  }

  /// <summary>Answers a question about a hostile name without the question itself throwing.</summary>
  private static bool _Ask(Func<bool> query) {
    try {
      return query();
    } catch (PoolFsException) {
      return false; // the member refuses to even describe the path, which is a "no"
    }
  }

  /// <summary>Everything under the sandbox that is not inside the member root.</summary>
  private string[] _EscapedEntries()
    => [.. Directory.EnumerateFileSystemEntries(this._sandbox, "*", SearchOption.AllDirectories)
        .Where(entry => !entry.StartsWith(this._root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .Where(entry => !string.Equals(entry, this._root, StringComparison.OrdinalIgnoreCase))
        .Where(entry => !string.Equals(entry, this._neighbour, StringComparison.OrdinalIgnoreCase))];

  [Test]
  [Category("Exception")]
  public void Write_GivenAHostileRelativePath_ThenNothingIsWrittenOutsideTheRoot() {
    foreach (var name in _HostileNames)
      _Attempt(() => {
        using var stream = this._volume.OpenWrite(name, false, true);
        stream.Write([0xBA, 0xD0], 0, 2);
        stream.Flush();
      });

    this._EscapedEntries().Should().BeEmpty(
      "a member write must never create anything outside the member root, whatever the path claims to be");
  }

  [Test]
  [Category("Exception")]
  public void Write_GivenAHostileShadowPath_ThenNothingIsWrittenOutsideTheRootEither() {
    // the shadow container is a second path-building route (FOLDER.DUPLICATE.$DRIVEBENDER gets
    // spliced in), so it needs proving separately rather than by analogy
    foreach (var name in _HostileNames)
      _Attempt(() => {
        using var stream = this._volume.OpenWrite(name, true, true);
        stream.Write([0xBA, 0xD0], 0, 2);
        stream.Flush();
      });

    this._EscapedEntries().Should().BeEmpty("shadow writes are contained exactly like primary ones");
  }

  [Test]
  [Category("Exception")]
  public void Paths_GivenRelativeOrDriveQualifiedSegments_ThenTheyAreRefusedOutright() {
    // The names that CANNOT be normalised into something safe are rejected rather than
    // reinterpreted: silently turning '../x' into 'x' would be its own hazard, quietly writing to
    // the wrong file. A bare leading separator is not in this set — it means "from the pool root"
    // and is trimmed — but a relative segment can only be a mistake or an attack.
    foreach (var name in _HostileNames.Where(n => n.Contains("..")))
      _Attempt(() => {
        using var stream = this._volume.OpenWrite(name, false, true);
        stream.Write([1], 0, 1);
      }).Should().BeTrue($"'{name}' cannot be made safe by normalising, so it must be refused");

    // A DRIVE QUALIFIER is refused only where it is dangerous, and that is a platform fact rather
    // than a weaker promise. `_Contain` refuses it because Path.Combine would otherwise hand back
    // the rooted path and leave the member root entirely — which is what Combine does on Windows
    // and not what it does on POSIX, where 'C:\...' is an ordinary relative name with no special
    // meaning. Demanding refusal on both would assert the implementation rather than the property,
    // exactly as this fixture's own documentation warns; what has to hold everywhere is that
    // nothing lands outside the root, and that is asserted below and by the sibling tests.
    const string driveQualified = @"C:\Windows\Temp\escaped.txt";
    var refused = _Attempt(() => {
      using var stream = this._volume.OpenWrite(driveQualified, false, true);
      stream.Write([1], 0, 1);
    });

    if (OperatingSystem.IsWindows())
      refused.Should().BeTrue($"'{driveQualified}' would escape the member root on this platform, so it must be refused");

    this._EscapedEntries().Should().BeEmpty(
      $"whether '{driveQualified}' is refused or merely contained, it must never put anything outside the member root");
  }

  [Test]
  [Category("Exception")]
  public void Folders_GivenAHostileRelativePath_ThenNoDirectoryIsCreatedOutsideTheRoot() {
    foreach (var name in _HostileNames)
      _Attempt(() => this._volume.EnsureFolder(name, false));

    this._EscapedEntries().Should().BeEmpty(
      "creating a folder for a hostile listing entry must not scatter directories across the machine");
  }

  [Test]
  [Category("Exception")]
  public void Delete_GivenAHostileRelativePath_ThenNothingOutsideTheRootIsRemoved() {
    // a traversal that DELETES is the quieter half of the same defect: the pool would be removing
    // files it does not own, and the user would have no record of what went
    var victim = Path.Combine(this._neighbour, "precious.txt");
    File.WriteAllText(victim, "not the pool's to delete");
    var victimFolder = Path.Combine(this._neighbour, "precious-folder");
    Directory.CreateDirectory(victimFolder);

    foreach (var name in new[] { "../neighbour/precious.txt", "..\\neighbour\\precious.txt", victim })
      _Attempt(() => this._volume.Delete(name, false));

    foreach (var name in new[] { "../neighbour/precious-folder", "..\\neighbour\\precious-folder", victimFolder })
      _Attempt(() => this._volume.DeleteFolder(name, false));

    File.Exists(victim).Should().BeTrue("a pool delete must never reach a file outside the member root");
    Directory.Exists(victimFolder).Should().BeTrue("nor may it remove a directory outside the member root");
  }

  [Test]
  [Category("Exception")]
  public void AtomicReplace_GivenHostileNames_ThenNothingIsPublishedOutsideTheRoot() {
    // publication takes TWO paths, and a check on only one of them is a hole
    using (var stream = this._volume.OpenWrite("staged.tmp", false, true)) {
      stream.Write([1, 2, 3], 0, 3);
      stream.Flush();
    }

    foreach (var name in _HostileNames) {
      _Attempt(() => this._volume.AtomicReplace("staged.tmp", name, false));
      _Attempt(() => this._volume.AtomicReplace(name, "published.bin", false));
    }

    this._EscapedEntries().Should().BeEmpty("neither side of a publish may address anything outside the root");
  }

  [Test]
  [Category("Exception")]
  public void RenameFolder_GivenHostileNames_ThenNothingIsMovedOutsideTheRoot() {
    this._volume.EnsureFolder("real", false);

    foreach (var name in _HostileNames) {
      // a rename that is allowed through has moved the folder somewhere inside the root, so put it
      // back before trying the next name rather than assuming it stayed put
      _Attempt(() => this._volume.RenameFolder("real", name));
      if (!_Ask(() => this._volume.FolderExists("real", false)))
        this._volume.EnsureFolder("real", false);

      _Attempt(() => this._volume.RenameFolder(name, "arrived"));
    }

    this._EscapedEntries().Should().BeEmpty("a rename must not be able to move a folder off the member");
  }

  [Test]
  [Category("Exception")]
  public void Read_GivenAHostileRelativePath_ThenNothingOutsideTheRootIsDisclosed() {
    // the disclosure direction of the same defect: a hostile listing naming '../../secrets' would
    // otherwise let the pool serve a file it was never given, through the mount, to anyone
    var secret = Path.Combine(this._neighbour, "secret.txt");
    File.WriteAllText(secret, "credentials");

    foreach (var name in new[] { "../neighbour/secret.txt", "..\\neighbour\\secret.txt", secret }) {
      var opened = false;
      _Attempt(() => {
        using var stream = this._volume.OpenRead(name, false);
        opened = true;
      });

      opened.Should().BeFalse($"reading '{name}' must not reach outside the member root");
      _Ask(() => this._volume.FileExists(name, false)).Should().BeFalse($"'{name}' is not a file this member has");
      _Ask(() => this._volume.Stat(name, false) != null).Should().BeFalse(
        $"nor may '{name}' be described through the member");
    }

    File.ReadAllText(secret).Should().Be("credentials", "and the file outside the root is untouched");
  }

}
