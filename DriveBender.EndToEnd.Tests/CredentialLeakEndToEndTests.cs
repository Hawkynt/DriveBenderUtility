using DivisonM.EndToEnd.Tests.TestSupport;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.EndToEnd.Tests;

/// <summary>
/// SEC-CRED: a secret handed to the pool must never be written anywhere but the credential store.
///
/// This had no coverage at all, and it is the one requirement whose failure is silent and
/// unrecoverable — a password does not come back once it has been committed to a manifest that the
/// operator has already mailed to a colleague or checked into a backup. The promise is narrow and
/// testable: members carry a <c>cred-ref:</c> REFERENCE, the secret itself lives in the OS
/// credential store, and nothing in between ever holds the plaintext.
///
/// The method is deliberately blunt. Rather than assert about the shape of each artefact, store a
/// secret no other code could produce and then hunt for that byte sequence in everything a user
/// hands to somebody else: the manifest registry, <c>pool-export</c> (which exists to be shared),
/// the manifest mirrors written onto the member disks themselves (a disk gets returned, resold, or
/// thrown out), and the CLI's own console output (which lands in terminal scrollback and CI logs).
/// A leak anywhere in that set is a disclosure; searching for the literal catches shapes an
/// assertion about structure would miss, including a secret that reached a field nobody thought of.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
[Category("Security")]
[NonParallelizable]
public class CredentialLeakEndToEndTests {

  private static readonly TimeSpan _CLI = TimeSpan.FromMinutes(2);

  /// <summary>
  /// Unmistakable, and unlike anything the product would emit on its own — so a hit while sweeping
  /// is a disclosure and never a coincidence.
  /// </summary>
  private string _secret = "";
  private string _reference = "";
  private string _poolName = "";
  private string _root = "";
  private string[] _members = [];

  [SetUp]
  public void StoreASecretAndReferenceIt() {
    var unique = Guid.NewGuid().ToString("N")[..12];
    this._secret = "S3CR3T-do-not-persist-" + unique;
    this._reference = "e2ecred-" + unique;

    this._root = Path.Combine(Path.GetTempPath(), "dbe2e-cred-" + unique);
    this._members = [Path.Combine(this._root, "m0"), Path.Combine(this._root, "m1")];
    foreach (var member in this._members)
      Directory.CreateDirectory(member);

    this._poolName = "e2ecred" + unique[..8];
  }

  [TearDown]
  public void RemoveTheSecret() {
    if (this._reference.Length > 0)
      DbMount.Run(_CLI, "credential-remove", this._reference);

    if (this._poolName.Length > 0)
      DbMount.ForgetPool(this._poolName);

    try {
      if (Directory.Exists(this._root))
        Directory.Delete(this._root, true);
    } catch (Exception) {
      // teardown is best effort
    }
  }

  [Test]
  [Category("Exception")]
  [Description("A stored secret appears in no manifest, no export, no on-disk mirror and no console output — only its reference does.")]
  public void Secret_GivenItIsStoredAndReferencedByAMember_ThenOnlyTheReferenceIsEverPersisted() {
    var stored = DbMount.Run(_CLI, "credential-set", this._reference, "-u", "someuser", "--secret", this._secret);
    if (stored.ExitCode != 0)
      Assert.Ignore($"this machine has no usable credential store: {stored.Output}");

    // the secret must not even survive the command that accepted it
    stored.Output.Should().NotContain(this._secret,
      "'credential-set' echoed the secret back to the console, where it lands in scrollback and CI logs");

    DbMount.RunExpectingSuccess(_CLI, "pool-create", "-n", this._poolName, "-m", this._members[0]);
    DbMount.RunExpectingSuccess(_CLI, "pool-add-member", this._poolName, "-m", this._members[1], "--credential", this._reference);

    var export = DbMount.RunExpectingSuccess(_CLI, "pool-export", this._poolName);
    var list = DbMount.RunExpectingSuccess(_CLI, "pool-list");

    // the export is the artefact that exists to be handed to somebody else
    export.Output.Should().NotContain(this._secret, "'pool-export' output carried the plaintext secret");
    export.Output.Should().Contain("cred-ref:" + this._reference,
      "the member must still carry its reference — otherwise this test proves nothing but a missing field");
    list.Output.Should().NotContain(this._secret, "'pool-list' output carried the plaintext secret");

    foreach (var (where, text) in _SweepFor(DbMount.PoolRegistryDirectory))
      text.Should().NotContain(this._secret, $"the manifest registry file '{where}' holds the plaintext secret");

    // the mirrors written onto the member disks: those disks leave the building
    foreach (var member in this._members)
      foreach (var (where, text) in _SweepFor(member))
        text.Should().NotContain(this._secret,
          $"'{where}' on a member disk holds the plaintext secret — the disk is now a disclosure");
  }

  [Test]
  [Category("Exception")]
  [Description("The credential store's own files are the only place the secret may rest, and the fallback file is owner-only.")]
  public void Secret_GivenItIsAtRest_ThenItIsInTheCredentialStoreAloneAndThatStoreIsNotWorldReadable() {
    var stored = DbMount.Run(_CLI, "credential-set", this._reference, "-u", "someuser", "--secret", this._secret);
    if (stored.ExitCode != 0)
      Assert.Ignore($"this machine has no usable credential store: {stored.Output}");

    // Everything under the config root EXCEPT the credential store proper. On Windows the secret
    // goes to the Credential Manager and no file should hold it at all; elsewhere it lands in
    // credentials.json, which is then the single permitted resting place.
    var configRoot = Path.GetDirectoryName(DbMount.PoolRegistryDirectory)!;
    var storePath = Path.Combine(configRoot, "credentials.json");

    foreach (var (where, text) in _SweepFor(configRoot)) {
      if (string.Equals(where, storePath, StringComparison.OrdinalIgnoreCase))
        continue;

      text.Should().NotContain(this._secret,
        $"'{where}' under the config root holds the plaintext secret; only the credential store may");
    }

    if (!File.Exists(storePath)) {
      TestContext.Out.WriteLine("no fallback file store on this platform — the secret went to the OS credential store");
      return;
    }

    TestContext.Out.WriteLine($"fallback file store in use: {storePath}");
    if (OperatingSystem.IsWindows())
      return;

    var mode = File.GetUnixFileMode(storePath);
    (mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)).Should().Be(UnixFileMode.None,
      "the fallback credential file is secrets-at-rest and must be readable by its owner alone");
  }

  [Test]
  [Category("EdgeCase")]
  [Description("A raw secret passed where a reference NAME belongs must not be written verbatim into the manifest.")]
  public void Secret_GivenItIsPassedWhereTheReferenceNameBelongs_ThenItIsNotCommittedToTheManifest() {
    // The footgun, not the happy path. '--credential' takes "cred-ref:<name> or just <name>", so
    // the field invites a value that looks like a credential — and a password pasted there is
    // normalised into the reference itself and written to the manifest, which pool-export then
    // hands out. Nothing warns, and the secret cannot be un-shared afterwards.
    DbMount.RunExpectingSuccess(_CLI, "pool-create", "-n", this._poolName, "-m", this._members[0]);

    var added = DbMount.Run(_CLI, "pool-add-member", this._poolName, "-m", this._members[1], "--credential", this._secret);
    var export = DbMount.Run(_CLI, "pool-export", this._poolName);

    TestContext.Out.WriteLine($"add-member exit {added.ExitCode}: {added.Output}");

    // The plaintext must never reach the shareable export. This measured as a real disclosure
    // before the check existed: '--credential <password>' exited 0 and wrote
    // "credential": "cred-ref:<password>" into the manifest, with nothing said about it.
    export.Output.Should().NotContain(this._secret,
      "a secret passed in place of a reference name was committed verbatim to the manifest and is "
      + "now in 'pool-export' output — the artefact users mail around and back up");

    added.ExitCode.Should().NotBe(0,
      "a --credential value naming nothing in the credential store must be refused: minting a "
      + "reference out of it is how the secret gets persisted in the first place");
    added.Output.Should().NotContain(this._secret,
      "the refusal echoed the secret back to the console");
  }

  /// <summary>
  /// Every readable text file under a directory, paired with its path. Binary and locked files are
  /// skipped: the search is for a plaintext secret, and a file the pool is holding open is not a
  /// file the operator can hand anybody anyway.
  /// </summary>
  private static IEnumerable<(string where, string text)> _SweepFor(string directory) {
    if (!Directory.Exists(directory))
      yield break;

    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
      string text;
      try {
        text = File.ReadAllText(file);
      } catch (Exception) {
        continue;
      }

      yield return (file, text);
    }
  }

}
