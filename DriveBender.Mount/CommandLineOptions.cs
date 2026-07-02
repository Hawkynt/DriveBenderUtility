using CommandLine;

namespace DivisonM.Mount;

// `dbmount pool create …` is translated to the "pool-create" verb by the arg
// preprocessor in Program (CommandLineParser has no nested verbs).

[Verb("pool-create", HelpText = "Create a manifest pool from arbitrary member paths (drive roots, subfolders, UNC shares).")]
public sealed class PoolCreateOptions {
  [Option('n', "name", Required = true, HelpText = "Name of the new pool.")]
  public string Name { get; set; } = "";

  [Option('m', "member", Required = true, Min = 1, HelpText = "Member path(s): drive root (A:\\), subfolder (C:\\pools\\dir), or UNC share (\\\\server\\share\\pool).")]
  public IEnumerable<string> Members { get; set; } = [];

  [Option('l', "landing", HelpText = "Member path(s) that form the fast tier (SSD/NVMe landing zones).")]
  public IEnumerable<string> LandingZones { get; set; } = [];

  [Option("mount", HelpText = "Mount target: a drive letter (X:\\) or an empty directory.")]
  public string? MountTarget { get; set; }

  [Option("force", HelpText = "Consent to absorbing pre-existing content of a non-empty member folder into the pool.")]
  public bool Force { get; set; }
}

[Verb("pool-import", HelpText = "Import a pool from a manifest JSON file.")]
public sealed class PoolImportOptions {
  [Value(0, Required = true, MetaName = "manifest", HelpText = "Path to the *.dbpool.json manifest file.")]
  public string ManifestPath { get; set; } = "";

  [Option("force", HelpText = "Consent to absorbing pre-existing content of non-empty member folders.")]
  public bool Force { get; set; }
}

[Verb("pool-export", HelpText = "Export a pool's manifest as JSON.")]
public sealed class PoolExportOptions {
  [Value(0, Required = true, MetaName = "pool", HelpText = "Pool name or id.")]
  public string Pool { get; set; } = "";

  [Option('o', "output", HelpText = "Target file; writes to stdout when omitted.")]
  public string? Output { get; set; }
}

[Verb("pool-list", HelpText = "List all discovered pools (explicit manifests and native scan).")]
public sealed class PoolListOptions {
  [Option("json", HelpText = "Machine-readable JSON output.")]
  public bool Json { get; set; }
}

[Verb("pool-add-member", HelpText = "Add a member to an existing manifest pool.")]
public sealed class PoolAddMemberOptions {
  [Value(0, Required = true, MetaName = "pool", HelpText = "Pool name or id.")]
  public string Pool { get; set; } = "";

  [Option('m', "member", Required = true, HelpText = "Path of the new member.")]
  public string Member { get; set; } = "";

  [Option("role", Default = "capacity", HelpText = "capacity | landing | readonly.")]
  public string Role { get; set; } = "capacity";

  [Option("reserve", HelpText = "Bytes the pool must not consume on the member's volume (e.g. 20GiB).")]
  public string? Reserve { get; set; }

  [Option("credential", HelpText = "Credential reference for remote members (cred-ref:<name> or just <name>).")]
  public string? Credential { get; set; }

  [Option("force", HelpText = "Consent to absorbing pre-existing content of a non-empty folder.")]
  public bool Force { get; set; }
}

[Verb("pool-remove-member", HelpText = "Remove a member from a manifest pool; its data stays in place.")]
public sealed class PoolRemoveMemberOptions {
  [Value(0, Required = true, MetaName = "pool", HelpText = "Pool name or id.")]
  public string Pool { get; set; } = "";

  [Option('m', "member", Required = true, HelpText = "Member path or member id.")]
  public string Member { get; set; } = "";
}

[Verb("pool-adopt", HelpText = "Materialise a discovered native pool into an explicit, editable manifest — in place, no data is moved.")]
public sealed class PoolAdoptOptions {
  [Value(0, Required = true, MetaName = "pool", HelpText = "Native pool name or id.")]
  public string Pool { get; set; } = "";
}

[Verb("pool-repair-manifest", HelpText = "Reconcile divergent manifest copies (registry vs. member mirrors); highest version wins.")]
public sealed class PoolRepairManifestOptions {
  [Value(0, Required = true, MetaName = "pool", HelpText = "Pool name or id.")]
  public string Pool { get; set; } = "";
}

[Verb("credential-set", HelpText = "Store a secret in the OS credential store; reference it from members as cred-ref:<name>.")]
public sealed class CredentialSetOptions {
  [Value(0, Required = true, MetaName = "name", HelpText = "Reference name (e.g. MyPool-server).")]
  public string Name { get; set; } = "";

  [Option('u', "user", Default = "", HelpText = "User name part of the credential.")]
  public string User { get; set; } = "";

  [Option("secret", HelpText = "The secret; omit to read it from stdin (recommended - keeps it out of shell history).")]
  public string? Secret { get; set; }
}

[Verb("credential-remove", HelpText = "Remove a stored secret.")]
public sealed class CredentialRemoveOptions {
  [Value(0, Required = true, MetaName = "name", HelpText = "Reference name.")]
  public string Name { get; set; } = "";
}

[Verb("mount", HelpText = "Mount a pool manifest at a target (drive letter, directory, or Linux mountpoint).")]
public sealed class MountOptions {
  [Option("manifest", Required = true, HelpText = "Manifest path or pool id/name.")]
  public string Manifest { get; set; } = "";

  [Option('t', "target", HelpText = "Mount target; defaults to the manifest's mount.target.")]
  public string? Target { get; set; }

  [Option("read-only", HelpText = "Mount read-only.")]
  public bool ReadOnly { get; set; }

  [Option("foreground", HelpText = "Stay attached to the console instead of daemonizing.")]
  public bool Foreground { get; set; }
}

[Verb("install-service", HelpText = "Install a Windows service that mounts a manifest at boot (FR-MOUNT-WIN-CLI).")]
public sealed class InstallServiceOptions {
  [Option("manifest", Required = true, HelpText = "Manifest path or pool id/name.")]
  public string Manifest { get; set; } = "";

  [Option('t', "target", HelpText = "Mount target; defaults to the manifest's mount.target.")]
  public string? Target { get; set; }
}

[Verb("uninstall-service", HelpText = "Remove the Windows service for a manifest.")]
public sealed class UninstallServiceOptions {
  [Option("manifest", Required = true, HelpText = "Manifest path or pool id/name.")]
  public string Manifest { get; set; } = "";
}

[Verb("install-systemd", HelpText = "Install the systemd unit + mount.drivebender fstab helper on Linux (FR-MOUNT-FSTAB).")]
public sealed class InstallSystemdOptions {
  [Option("manifest", Required = true, HelpText = "Manifest path or pool id/name (for the enable hint).")]
  public string Manifest { get; set; } = "";
}

[Verb("register-shell", HelpText = "Register the right-click \"mount\" action for *.dbpool.json manifests (Windows, FR-MOUNT-WIN-GUI).")]
public sealed class RegisterShellOptions {
}

[Verb("unmount", HelpText = "Unmount a mounted pool.")]
public sealed class UnmountOptions {
  [Value(0, Required = true, MetaName = "target", HelpText = "Mount target or pool id.")]
  public string Target { get; set; } = "";
}

[Verb("status", HelpText = "Show mount/daemon status.")]
public sealed class StatusOptions {
  [Option("json", HelpText = "Machine-readable JSON output.")]
  public bool Json { get; set; }
}

[Verb("list", HelpText = "List all discovered pools (alias of pool-list).")]
public sealed class ListOptions {
  [Option("json", HelpText = "Machine-readable JSON output.")]
  public bool Json { get; set; }
}
