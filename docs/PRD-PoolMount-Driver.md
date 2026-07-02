# PRD — DriveBender Pool Mount Driver

> **Status:** Draft for implementation planning · **Owner:** Hawkynt · **Target repo:** `DriveBenderUtility`
> **One-liner:** Mount a DriveBender pool — native *or* defined by a portable JSON manifest over arbitrary members (drive roots, subfolders, UNC, removable, remote FTP/SFTP/WebDAV) — as a live, read/write filesystem: a drive letter or directory junction on Windows, a mountpoint on Linux. A RAM→fast→capacity write cascade with configurable caching (global or per-pool, PrimoCache-style), read-ahead, multi-drive I/O acceleration, and write policies; managed from an awesome cross-platform GUI with live per-pool visualisation — all provably data-safe and fully unit-tested.

This document is the authoritative product & engineering spec. It is written to be
worked from directly: every requirement has an ID (`FR-*`, `NFR-*`, `SAFE-*`,
`CFG-*`, `TST-*`), a MoSCoW priority, and given-when-then acceptance criteria where
behaviour is testable. Component contracts are given as C# interface sketches so
the implementation can begin against fixed signatures.

---

## 1. Context & background

### 1.1 What exists today
`DriveBender.Core` (namespace `DivisonM`) already models a pool **offline**, at
file granularity:

- `DriveBender.DetectedMountPoints` discovers pools by scanning drives and
  grouping physical volumes by pool `Guid`.
- `IMountPoint` → many `IVolume` (one per physical drive). Each volume is a
  `DirectoryInfo` root holding the pool's per-GUID folder.
- Logical files (`IFile`) expose `Primary`/`Primaries` and
  `ShadowCopy`/`ShadowCopies`; the same relative path may exist as a *primary*
  on one volume and as a *shadow copy* (under a
  `FOLDER.DUPLICATE.$DRIVEBENDER` subfolder) on others.
- On-disk markers (`DriveBenderConstants`): `FOLDER.DUPLICATE.$DRIVEBENDER`
  (shadow folder), `MP.$DRIVEBENDER` (pool metadata), `TEMP.$DRIVEBENDER`
  (in-progress transfer).
- Whole-file operations exist: `IPhysicalFile.MoveToDrive/CopyToDrive`,
  `PoolManager` (create/add/remove/replace/rebalance), `DuplicationManager`
  (enable/disable/level, extra shadow copies), `IntegrityChecker` + `Repairer`
  (8 issue types, backup, dry-run), `Rebalancer` (free-space equalisation).

**Key architectural fact that shapes this whole PRD:** DriveBender stores every
file *whole* on a single volume, optionally mirrored whole onto other volumes as
shadow copies. Files are **not** block-striped across drives. Therefore
"accelerated I/O using multiple physical drives" means **request-level and
mirror-level parallelism** (§6.4), *not* single-file RAID-0 striping — that would
require a new on-disk format and is an explicit non-goal (§3.3).

### 1.2 Why a driver
Today the tool operates on a pool the OS cannot see as a filesystem. Users want
to *use* the pool live — open, read, write, rename, delete through Explorer /
`ls` / any application — while the pool's duplication, balancing and tiering
happen transparently underneath.

### 1.3 One pool model — the manifest is the pool
There is exactly **one** internal pool model: a **manifest** — a set of member
folders plus tuning — resolved into the in-memory `IMountPoint`, over the same
on-disk file layout (whole-file primaries + `FOLDER.DUPLICATE.$DRIVEBENDER`
shadow folders). Everything downstream (the VFS engine, placement, duplication,
balancing, safety, and all existing Core services) operates **only** on this
model and cannot tell how the manifest was obtained. There are two *definition
sources*, not two code paths:

1. **Manifest pools** *(new, §6.0)* — an explicit **JSON manifest** listing
   arbitrary member paths: drive roots (`A:\`), subfolders (`C:\pools\dir`,
   `B:\test`), UNC shares (`\\server\share\pool`), removable drives — mounted to
   any target (`X:\`, `Y:\Mounts\MyPool`).
2. **Native pools** — the classic drive-scan (group physical volumes by on-disk
   pool `Guid`) is just a **discovery adapter that synthesizes a virtual
   manifest** whose members are those drives' pool-GUID root folders. A native
   pool *is* a manifest pool whose membership happens to be derived by scanning
   instead of read from JSON; it is otherwise indistinguishable to the engine.

Because a native pool is a special case of the manifest model, a user can also
"adopt" a discovered native pool into an explicit, editable manifest (e.g. to add
a UNC member or a landing zone) without moving any data (`FR-ADOPT`, §6.0.6).

### 1.4 Format compatibility constraint
`SAFE-COMPAT` (**Must**): For **native pools**, the driver reads and writes the
**existing DriveBender on-disk layout** so the pool remains fully interoperable
with the original Division-M product and this repo's offline tools. No new
required on-disk structures except **opt-in, self-contained** sidecars (journal,
config) that the original product safely ignores and that must not collide with
its namespace; removing our sidecars leaves a byte-identical, valid DriveBender
pool. **Manifest pools** are a DriveBenderUtility-native construct: they are not
expected to be discoverable by Division-M (which has no notion of our manifest),
but they reuse the same *internal* file/shadow layout inside each member folder,
so the offline tools in this repo operate on them unchanged.

---

## 2. Goals, personas, use cases

### 2.1 Goals
- **G1** Mount a pool as a native filesystem on Windows (drive letter *or*
  directory/junction mountpoint) and Linux (any directory mountpoint).
- **G2** Full POSIX/Win32 read-write semantics: create, open, read, write,
  truncate, append, rename/move, delete, mkdir/rmdir, enumerate, stat, set
  timestamps/attributes, flush/fsync.
- **G3** Fast: caching, read-ahead, and concurrent use of multiple physical
  drives to exceed single-drive throughput for realistic workloads.
- **G4** Tiering: SSD/NVMe **landing zones** absorb writes first; background
  workers migrate, duplicate and rebalance onto capacity drives.
- **G5** Configurable write durability: write-through, write-back, deferred —
  per pool and per folder — with a hard "minimum copies before ack" safety knob.
- **G6** Provable data safety: no acknowledged write is ever lost or corrupted,
  the duplication invariant is always restorable, and a crash at any instant is
  recoverable to a consistent state.
- **G7** Everything configurable per pool with performance-tuned defaults that
  work well out of the box.
- **G8** Exhaustively unit-tested, including fault injection, so nothing breaks.
- **G9** Create pools from **arbitrary member paths** — drive roots, subfolders,
  UNC shares, removable drives — described by a portable JSON manifest, and
  mount to any target, with graceful handling of members that are offline or
  whose drive letters have changed.
- **G10** A **tiered performance write path** — RAM write cache → fast tier
  (SSD/NVMe) → capacity tier — where the capacity tier may be **local or
  remote** (UNC / FTP / SFTP / TFTP / WebDAV), with data cascading down tiers in
  the background and durability governed by explicit, safe defaults.
- **G11** **Mount however the platform wants it**: a manifest picker + CLI on
  Windows (so Task Scheduler / autorun can mount unattended) and an `fstab`
  helper on Linux (mount a manifest at boot).
- **G12** An **awesome, usable, cross-platform GUI** (Windows *and* Linux) from
  which every aspect of every pool — membership, tiers, write policy, caches,
  per-folder overrides, mount targets — is configurable, with a live health &
  performance dashboard.
- **G13** **Flexible cache scoping** (PrimoCache/PrimoRAMDisk-style): a shared
  **global** RAM cache serving all pools by default, with any pool able to opt
  into its **own dedicated** cache tuned for its workload (write-optimised,
  small-I/O, metadata-heavy, read-streaming …), all bounded by a machine-wide RAM
  ceiling that is never over-committed.
- **G14** An **optional per-pool trash** so deletes are recoverable for a
  configurable window instead of being immediately permanent.
- **G15** **Localised** GUI and CLI — English (default) and German at minimum,
  with a resource-based system open to more languages.
- **G16** **Bit-rot detection & repair** via a pool-stored checksum database,
  which also **detects out-of-band changes** — data written to a member's
  underlying storage *behind the driver's back* (edited on another machine, by
  Division-M, or directly on disk) — and reconciles it safely, never mistaking a
  legitimate external edit for corruption or vice-versa.

### 2.2 Personas
- **Home media hoarder** — big capacity pool, mixed HDD; wants Explorer to just
  work, duplication for irreplaceable data, quiet background balancing.
- **Prosumer / small server** — RAM first + NVMe landing zone + HDD capacity; wants fast
  ingest then background tiering; runs headless Linux.
- **Data-safety-first user** — accepts lower write speed for write-through;
  wants guarantees, alerts, and clean recovery after power loss.

### 2.3 Representative use cases (given-when-then)
- **UC1 Live read** — *Given* a mounted pool, *when* an app opens and reads a
  duplicated 4 GB file, *then* bytes are served correctly and read throughput
  exceeds a single drive's by mirror read-balancing.
- **UC2 Fast ingest** — *Given* a pool with an SSD landing zone and write-back,
  *when* the user copies 20 GB in, *then* the copy completes at ~SSD speed and
  the data is migrated+duplicated to capacity drives in the background without
  user action.
- **UC3 Crash during write-back** — *Given* dirty data in the landing zone,
  *when* power is lost, *then* on next mount every acknowledged write is present
  and the pool converges to a fully consistent, correctly duplicated state with
  zero corruption.
- **UC4 Drive removed hot** — *Given* a mounted pool, *when* a capacity drive
  disappears, *then* the mount stays up, reads served from surviving copies,
  affected duplicated files remain readable, and the event is surfaced.

---

## 3. Scope (MoSCoW)

### 3.1 Must
Live mount on both platforms; full RW semantics (G2); read cache + read-ahead;
metadata cache; write-through & write-back with min-copies-before-ack; landing
zones with background migrate/duplicate/rebalance; crash-consistent journaling;
per-pool + per-folder config with tuned defaults; duplication invariant
preservation; full unit + fault-injection test suite; format compatibility
(`SAFE-COMPAT`); **manifest pools** — create/mount/manage pools from arbitrary
member paths (drive roots, subfolders, UNC, removable) via a portable JSON
manifest, with member self-identification and offline-member tolerance (§6.0);
the **tiered write cascade** RAM→fast→capacity with at least local + UNC capacity
backends (§6.7); platform-native **mount invocation** — Windows picker + CLI,
Linux `fstab` helper (§6.12); a **cross-platform GUI** covering all of the above
(§6.13).

### 3.2 Should
Deferred/coalesced write-back with delay; mirror read-balancing across copies;
adaptive read-ahead; landing-zone eviction watermarks; live config reload;
metrics/observability endpoint; graceful hot drive-loss handling; xattr/ADS
best-effort passthrough; the **`performance` write mode** (RAM→fast→capacity
cascade, §6.8); **remote capacity backends** FTP / SFTP / WebDAV; auto-mount as a
Windows service / Linux `systemd` unit; live GUI dashboard metrics; **checksum DB
+ out-of-band-change detection & reconciliation** (§6.15); **GUI/CLI i18n**
(English + German, §6.13); **optional per-pool trash** (§6.14).

### 3.3 Could
Per-file read prefetch heuristics from access patterns; compression/dedup of
cold data; **TFTP** capacity backend (archive-only, whole-file, no auth — low
priority given its limits, §6.7); GUI system-tray quick-mount; macOS support (via
macFUSE — same FUSE adapter); OS-native Recycle Bin integration on Windows
(vs. the built-in pool trash, §6.14); additional GUI languages beyond en/de;
periodic idle **deep scrub** (strong-hash re-verification of the whole pool).

### 3.4 Won't (this version) / explicit non-goals
Single-file block striping / RAID-0 across drives (would change on-disk format);
kernel-mode drivers (user-mode FUSE/WinFsp only); network/SMB re-export (out of
scope — mount locally and share via OS); encryption at rest; changing
DriveBender's fundamental whole-file-copy model; treating **RAM as a durable
tier** (RAM only ever *accelerates* — a write is never acknowledged from RAM
alone unless the user explicitly accepts volatility, §6.8/`SAFE-RAM`); using
random-access remote protocols as if they were local disks (remote backends are
whole-file capacity/archive tiers, §6.7).

---

## 4. Platform & technology strategy

### 4.1 Mount backends
| Platform | Backend | Mount targets | .NET binding |
|---|---|---|---|
| Windows | **WinFsp** (user-mode, FUSE-compatible, actively maintained) | drive letter **or** empty NTFS directory (junction-style) | `winfsp` .NET layer (`Fsp`) |
| Linux | **libfuse (FUSE3)** | any directory mountpoint | **`Tmds.Fuse`** (modern, .NET 8+) |

Rationale: WinFsp and FUSE share a near-identical operation model, so we define
**one internal filesystem operation contract** (`IPoolFileSystem`, §6.2) and
write two thin adapters. Dokan is a fallback option on Windows only if a WinFsp
blocker appears; it is not the primary path.

`NFR-PORT` (**Must**): No pool logic lives in a platform adapter. Adapters only
translate backend callbacks ↔ `IPoolFileSystem`. Adding a third backend must
require zero changes to the I/O engine.

### 4.2 Runtime & projects
New projects (SDK-style, added to `DriveBender.sln`):

- **`DriveBender.Vfs`** — platform-agnostic VFS + I/O engine (cache, read-ahead,
  scheduler, write policies, tiers, journal, pool model). Depends on
  `DriveBender.Core`. Target **`net10.0`** (per house standard for new
  projects; polyfilled with **Hawkynt's Backports** package where needed).
- **`DriveBender.Backends`** — `IVolumeIO` backends beyond local disk: UNC, FTP,
  SFTP, WebDAV, (TFTP). Isolated so remote deps don't leak into the engine.
  `net10.0`.
- **`DriveBender.Mount.Windows`** — WinFsp adapter + host. `net10.0-windows`.
- **`DriveBender.Mount.Linux`** — FUSE adapter + host + `mount.drivebender`
  helper. `net10.0`.
- **`DriveBender.Mount`** — CLI/daemon entry (`mount` / `unmount` / `status`),
  picks the backend by OS; installable as a Windows service / `systemd` unit.
  `net10.0`.
- **`DriveBender.App`** — the cross-platform GUI (**Avalonia**, MVVM), Win+Linux
  (§6.13). `net10.0`.
- **`DriveBender.Vfs.Tests`** — the suite (§11). `net10.0`.

`DriveBender.Core` must be consumable from `net10.0`: multi-target it to
`netstandard2.0;net47` (task `T-CORE-MT`). The existing WPF `DriveBender.UI`
(net47) is superseded by `DriveBender.App` but kept until feature parity; the
`net47` Console keeps working unchanged.

**UI framework choice (`NFR-UI-XPLAT`, Must):** **Avalonia** — a mature,
cross-platform .NET XAML/MVVM toolkit that renders natively on Windows and Linux
(and macOS) from one codebase. Rejected: WPF (Windows-only), MAUI (weak Linux),
Electron (heavy, non-.NET). A local web UI is a possible *additional* headless
management surface (Could) but the primary is a native Avalonia desktop app.

---

## 5. Architecture overview

```
        Windows apps / Explorer                Linux apps / VFS
                 │                                    │
        ┌────────▼─────────┐               ┌──────────▼─────────┐
        │  WinFsp adapter  │               │   FUSE adapter     │   ← platform, thin
        └────────┬─────────┘               └──────────┬─────────┘
                 └───────────────┬────────────────────┘
                        IPoolFileSystem (VFS contract, §6.2)
                                 │
   ┌─────────────────────────────▼──────────────────────────────┐
   │                    VFS / I/O ENGINE (DriveBender.Vfs)        │
   │                                                             │
   │  Pool model /      Path→Placement   Cache instances         │
   │   manifest+scan    Handle table      (global / dedicated)   │
   │  Read-ahead        I/O scheduler    Write-policy machine    │
   │  Tier cascade mgr  Background workers (drain/dup/balance)   │
   │  Journal / WAL     Crash recovery   Config resolver         │
   │  Mgmt API + OPS-EVENTS (→ GUI)                              │
   └───────────────┬─────────────────────────────┬──────────────┘
                   │ IVolumeIO (§6.1)             │ IVolumeIOBackend (§6.1)
   ┌───────────────▼──────────────┐   ┌───────────▼──────────────┐
   │  DriveBender.Core            │   │  DriveBender.Backends     │
   │  IMountPoint/IVolume/…       │   │  UNC/FTP/SFTP/WebDAV/TFTP │
   │  Duplication·Rebalance·Integ.│   └───────────┬──────────────┘
   └───────────────┬──────────────┘               │
                   │                               │
        Local physical drives (DB layout)   Remote capacity tiers
```

(The GUI `DriveBender.App` and CLI/service `DriveBender.Mount` sit above the
engine, talking to it via the local management API.)

**Layering rule (`NFR-LAYER`, Must):** upward dependencies only. Core knows
nothing of the VFS; the VFS knows nothing of WinFsp/FUSE. The engine reuses
existing Core services (`DuplicationManager`, `Rebalancer`,
`IntegrityChecker`/`Repairer`) for background reconciliation rather than
re-implementing them.

---

## 6. Component specifications

### 6.0 Pool definition, membership & discovery (CMP-POOL)
The **manifest is the single pool model** (§1.3). This component owns manifest
parsing/writing, member resolution, and the discovery adapters that feed the
model. Native drive-scan discovery is one such adapter: it emits a *virtual
manifest* (members = each drive's pool-GUID root folder, physical-volume identity
= the drive) and returns it through the same path as an explicit JSON manifest.
No layer above this one branches on "native vs manifest".

#### 6.0.1 The pool manifest (`FR-MANIFEST`, Must)
A JSON manifest is the authoritative definition of a manifest pool:

```jsonc
{
  "schema": "drivebender-pool/1",
  "poolId": "b1f2…-guid",                 // stable identity, generated once
  "name": "MyPool",
  "members": [
    { "memberId": "m1-guid", "path": "A:\\",                 "role": "landing",  "label": "SSD" },
    { "memberId": "m2-guid", "path": "B:\\test",             "role": "capacity" },
    { "memberId": "m3-guid", "path": "C:\\pools\\dir",       "role": "capacity", "reserveBytes": "20GiB" },
    { "memberId": "m4-guid", "path": "\\\\server\\share\\pool","role":"capacity",
      "credential": "cred-ref:MyPool-server", "network": true }
  ],
  "mount": { "target": "X:\\", "volumeLabel": "MyPool" },     // or "Y:\\Mounts\\MyPool"
  "defaults": { /* the §8 config block: write/cache/landingZone/… */ },
  "folders":  { /* per-folder overrides, §8 */ }
}
```

- **`path` is a hint, not the identity.** The stable identity of a member is its
  `memberId`; the actual location is resolved at mount time (§6.0.3) because
  removable drives change letters and UNC hosts move.
- **`role`**: `capacity` | `landing` | `readonly` (contributes reads only, never
  receives writes/duplicates). `role` is shorthand for tier membership —
  `landing` ⇒ the **fast tier**, `capacity` ⇒ the **capacity tier** (§6.7); it
  may be overridden per member by `tier` in `memberOverrides`. All tier sources
  for a member must agree (`CFG-VALIDATE`, §8).
- **`reserveBytes`**: for subfolder members that share a volume with foreign
  data, space the pool must not consume (§6.0.4).
- **`credential`**: an indirect reference to an OS credential-store entry for UNC
  members — **never a plaintext password** (`SEC-CRED`, Must).

#### 6.0.2 Manifest storage & redundancy (`SAFE-MANIFEST`, Must)
The manifest is stored **redundantly** so no single lost member destroys the
definition:
- A **registry copy** in a well-known config dir
  (`%ProgramData%\DriveBenderUtility\pools\<poolId>.json` /
  `/etc/drivebenderutility/pools/<poolId>.json`).
- A **mirrored copy on every online member** inside a self-describing marker
  folder `.drivebenderutility/` (distinct from the `$DRIVEBENDER` namespace so it
  never collides with native-pool interop).
Writes to the manifest are atomic (temp + rename + fsync) and versioned; on
mismatch the **highest version present on a quorum / most recently written**
copy wins, and stale copies are refreshed. A pool can be reconstructed from the
registry entry **or** from any single member marker.

#### 6.0.3 Member self-identification & resolution (`FR-RESOLVE-MEMBER`, Must)
Each member folder carries `.drivebenderutility/member.json`
(`{ poolId, memberId, name }`). At mount the provider resolves every `memberId`
to a live path by, in order: (1) the manifest's last-known `path`; (2) scanning
candidate roots — all local volumes, configured search paths, and reachable UNC
hints — for a marker matching `(poolId, memberId)`. Resolution is by **marker
content, not path**, so `A:\` today and `E:\` tomorrow resolve to the same
member. Newly resolved paths are written back to the manifest.

#### 6.0.4 Physical-volume identity & failure domains (`SAFE-PHYS`, Must)
Because members may be **subfolders**, the engine must key placement and the
"shadow-never-same-physical" invariant (§6.3) on the **underlying physical
volume**, not the path: Windows volume GUID / `\\?\Volume{…}`, Linux `st_dev` /
mount source. Consequences:
- Two members resolving to the **same physical volume** form one failure domain;
  duplication must never place a file's copies within one domain (else "2 copies"
  survive a single disk loss as 0). The provider detects this and refuses to
  place redundant copies together, warning at mount.
- **Free-space accounting** uses the *volume's* free space, adjusted for
  `reserveBytes` and de-duplicated across members sharing a volume (never
  double-count) (`FR-SPACE-SHARED`, Must).

#### 6.0.5 Offline / removable / network members (`SAFE-OFFLINE`, Must)
Members may be absent at mount or vanish live (removable unplugged, UNC
unreachable). Behaviour reuses the drive-loss model (`SAFE-DEGRADE`, §10):
- **Mount with missing members**: allowed if surviving members can serve the
  namespace; affected files served from surviving copies; the pool mounts
  **degraded** with a clear diagnostic. Mount is refused only if a required
  member set is entirely unresolvable and `refuseMountOnUnrecoverable` is set.
- **Placement reroute**: writes destined for an offline member reroute to an
  online member; duplication owed to the offline member is recorded and deferred.
- **Member return / reconciliation**: when a member reappears, a background pass
  reconciles the duplication invariant and replays deferred copies (reuses
  `IntegrityChecker`/`DuplicationManager`).
- **Network members** (`network: true`): treated as higher-latency, possibly
  weaker-durability. `fsync` semantics on UNC are validated per share; if the
  share cannot guarantee durable flush, the member is ineligible to satisfy
  `minCopiesBeforeAck` and the mount warns (`SAFE-NET-DURABILITY`, Must).

#### 6.0.6 Lifecycle & CLI (`FR-POOL-CLI`, Must)
`DriveBender.Mount` / Console gain manifest-pool verbs:
`pool create --name MyPool --member "A:\" --member "B:\test" --member
"C:\pools\dir" --member "\\server\share\pool" --mount "X:\"`,
`pool add-member` / `remove-member`, `pool import <manifest.json>`,
`pool export`, `pool list`, `pool repair-manifest`, and `pool adopt <native>`
(`FR-ADOPT`: materialise a discovered native pool's virtual manifest into an
explicit, editable JSON manifest **in place, without moving data**, so members
like UNC shares or a landing zone can be added). Creating a pool writes the
manifest (registry + member markers) and initialises each member's internal
layout; it never destroys pre-existing data in a chosen folder without explicit
`--force` (`SAFE-NONDESTRUCTIVE`, Must).

#### 6.0.7 Provider contract
Both sources produce the **same** `PoolManifest`; only the *source adapter*
differs, so the provider that opens a pool never branches on kind:

```csharp
// Source adapters both yield a PoolManifest — the one true model.
public interface IManifestSource { IEnumerable<PoolManifest> Enumerate(); }
//   JsonManifestSource   → reads registry + member-marker JSON
//   NativeScanSource     → synthesizes a virtual manifest from the drive scan

public interface IPoolProvider {
    IReadOnlyList<PoolRef> Discover();            // union of all IManifestSource results
    IMountPoint Open(PoolRef pool, out PoolHealth health);   // resolves members, reports degraded state
}

public sealed record PoolMember(Guid MemberId, string ResolvedPath,
    string PhysicalVolumeId, MemberRole Role, bool Online, long ReserveBytes);
```
`PoolHealth` reports resolved/missing members, same-physical-volume conflicts,
and network-durability warnings so the host can surface them.

### 6.1 Physical byte-range I/O — `IVolumeIO` (CMP-IO)
Core is file-granular; the engine needs byte-range access and atomic placement
primitives. Add a thin abstraction over a volume's files.

```csharp
namespace DivisonM.Vfs;

/// Byte-range + placement primitives over one physical volume.
public interface IVolumeIO {
    IVolume Volume { get; }
    long BytesFree { get; }

    Stream OpenRead(string relativePath, bool shadow);
    Stream OpenWrite(string relativePath, bool shadow, bool create);   // positional writes
    void   Truncate(string relativePath, bool shadow, long length);
    void   Delete(string relativePath, bool shadow);
    void   EnsureFolder(string relativeFolder, bool shadow);

    /// Atomic same-volume rename via TEMP.$DRIVEBENDER staging + rename.
    void   AtomicReplace(string tempRelative, string finalRelative, bool shadow);
    FileMeta Stat(string relativePath, bool shadow);
}
```

- `AtomicReplace` writes to a `*.TEMP.$DRIVEBENDER` name, fsyncs, then renames
  over the target — the only way new content becomes visible (`SAFE-ATOMIC`).
- An **in-memory fake** (`FakeVolumeIO`) implements this for unit tests so the
  entire engine is testable without touching a real disk (`TST-FAKE`).

**Backends & capability negotiation (`FR-REMOTE`, Must for UNC; Should for
FTP/SFTP/WebDAV; Could for TFTP).** `IVolumeIO` has multiple implementations —
`LocalVolumeIO` and remote backends in `DriveBender.Backends`. Because the
DriveBender model stores every file *whole*, remote backends do **not** need
efficient random writes: they operate **whole-file** (stage locally, then
put/get the whole object), which fits capacity/archive tiers naturally. Each
backend declares a **capability set** the engine honours:

```csharp
[Flags] public enum BackendCaps {
    RandomRead=1, RandomWrite=2, AtomicRename=4, DurableFlush=8,
    List=16, Delete=32, Timestamps=64, ServerCredentials=128
}
public interface IVolumeIOBackend {
    string Scheme { get; }            // file, unc, ftp, sftp, webdav, tftp
    BackendCaps Caps { get; }
    IVolumeIO Open(MemberDescriptor member, ICredentialResolver creds);
}
```

| Scheme | Random I/O | Atomic rename | Durable flush | Auth | Suitable as |
|---|---|---|---|---|---|
| `file`/`unc` local | ✅ | ✅ (rename) | ✅ (SMB: probe) | OS / stored ref | any tier, incl. fast |
| `sftp` | ✅ (ranged) | ✅ (`rename`) | ⚠️ (`fsync` ext varies) | key / password ref | capacity |
| `ftp`/`ftps` | ⚠️ (`REST`) | ⚠️ (`RNFR/RNTO`) | ❌ | user/pass ref | capacity (whole-file) |
| `webdav` | ⚠️ (`Range`+`PUT`) | ⚠️ (`MOVE`) | ❌ | basic/bearer ref | capacity (whole-file) |
| `tftp` | ❌ | ❌ | ❌ | none | archive only (Could) |

- **Capability adaptation (`FR-CAP-ADAPT`, Must):** the engine queries `Caps`
  and degrades gracefully — a backend lacking `AtomicRename` gets a
  put-temp+list-verify+swap emulation with the journal covering the gap; a
  backend lacking `DurableFlush` is **excluded from satisfying
  `minCopiesBeforeAck`** and cannot be the *only* copy of acknowledged data
  (`SAFE-REMOTE`, Must).
- **Credentials (`SEC-CRED`, Must):** all backends take credentials via an
  `ICredentialResolver` that reads the OS credential store (Windows Credential
  Manager / Linux Secret Service / libsecret) by reference — never plaintext in
  the manifest.
- **Read path (`FR-REMOTE-READ`, Should):** reads from a remote member use ranged
  GET where `RandomRead` is supported; otherwise the file is **staged whole** to a
  local scratch/fast tier on first access and served from there, with the staged
  copy managed by the cache (evictable). Prefer serving a duplicated file's read
  from a **local** copy when one exists (mirror-balancing favours local over
  remote). Large sequential reads from remote prefetch aggressively.
- **Resilience:** remote backends have per-op timeouts, bounded retries with
  backoff, and map transient failures to the offline-member path (`SAFE-OFFLINE`,
  §6.0.5) rather than failing the mount.
- **`FakeRemoteBackend`** simulates each capability profile (missing rename,
  no durable flush, high latency, disconnect) for headless tests (`TST-FAKE`).

### 6.2 VFS contract — `IPoolFileSystem` (CMP-VFS)
The single surface both platform adapters call. Backend-neutral, POSIX-ish;
Win32-only concepts map in the adapter.

```csharp
public interface IPoolFileSystem : IDisposable {
    // lifecycle
    void Mount(MountOptions options);
    void Unmount();

    // metadata
    FileMeta   GetAttributes(string path);
    void       SetAttributes(string path, FileMetaPatch patch);
    IReadOnlyList<DirEntry> ReadDirectory(string path);

    // namespace
    NodeHandle Create(string path, NodeKind kind, CreateFlags flags);
    NodeHandle Open(string path, AccessMode mode, ShareMode share);
    void       Rename(string from, string to, RenameFlags flags);
    void       Unlink(string path);
    void       MakeDir(string path);
    void       RemoveDir(string path);

    // data
    int  Read(NodeHandle h, Span<byte> buffer, long offset);
    int  Write(NodeHandle h, ReadOnlySpan<byte> data, long offset, WriteMode mode);
    void SetLength(NodeHandle h, long length);
    void Flush(NodeHandle h);          // fsync semantics
    void Close(NodeHandle h);

    // volume-wide
    FsStatistics StatFs();             // total/free as pool aggregate
}
```

**Error contract (`FR-ERRNO`, Must):** every failure maps to a stable
platform-neutral `PoolFsError` enum (NotFound, AccessDenied, Exists, NotEmpty,
NoSpace, IoError, StaleHandle, …); adapters translate to NTSTATUS / errno.

### 6.3 Path → placement resolver (CMP-PLACE)
Translates a pool-relative path to concrete physical locations and decides where
new data lands.

- **Resolution** (`FR-RESOLVE`, Must): for a path, produce the set of physical
  copies `{(volume, isShadow)}` by consulting Core's `IFile.Primaries` /
  `ShadowCopies`, cached in the metadata cache (§6.5).
- **Placement policy** (`FR-PLACE`, Must) for new/extended files, following the
  tier cascade (§6.7):
  1. Land on the highest eligible tier (RAM → fast → capacity) whose target
     member has free space above its `lowWatermark`.
  2. Within the chosen tier, pick the member by `CFG.placement.strategy`
     (`most-free-space` default, or `round-robin`, `least-used`).
  3. Shadow copies target the member(s) with most free space, never in the same
     physical failure domain as the primary (`SAFE-PHYS`; mirrors existing
     `FixMissingShadowCopies`).
- **Duplication level — definition (normative).** In this PRD the **duplication
  level `D` is the total number of copies** the pool keeps of a file: `D=1` = no
  redundancy (primary only), `D=2` = primary + 1 shadow, `D=k` = primary +
  `(k−1)` shadows. `D≥2` requires ≥ `D` independent failure domains (§6.0.4).
  (The legacy Console `set-duplication --level` used a different convention —
  see open question §15.14; the driver normalises to `D` = total copies.)
- **Invariant** (`SAFE-DUP`, Must): the number of physical copies of a file
  converges to its folder's effective duplication level `D` once background work
  settles; the engine never *reduces* copies below the configured minimum as part
  of normal operation. If `D` exceeds the number of available failure domains,
  the pool keeps the maximum placeable copies and raises a degraded warning
  (`SAFE-PHYS`) rather than silently co-locating copies.

### 6.4 I/O scheduler & multi-drive acceleration (CMP-SCHED)
- **Request-level parallelism** (`FR-PAR`, Must): independent reads/writes to
  files on *different* volumes execute concurrently on per-volume worker queues;
  one slow spindle never blocks I/O to others.
- **Mirror read-balancing** (`FR-MIRROR`, Should): for a duplicated file, large
  reads may be split into byte ranges served in parallel from the primary and
  shadow copies (RAID-1-style read acceleration), bounded by
  `CFG.io.mirrorReadSplitThreshold`. Correctness is verified against a
  single-source read in tests.
- **Per-volume concurrency cap** (`CFG.io.queueDepthPerVolume`): HDD default 2
  (avoid seek thrash), SSD/landing default 8+.
- **Elevator ordering** (Could): within a volume queue, order by offset to
  reduce seeks for HDDs.
- **Concurrency model** (`FR-CONCURRENCY`, Must): a central handle table maps
  open handles → a per-file state object that owns that file's dirty write-buffer
  (single-writer-owner, so no double-write races); reads take a shared lock,
  structural ops (rename/delete/truncate) an exclusive lock; locks are per-file,
  never global, so unrelated files never serialise. Lock ordering is defined
  (parent-before-child for namespace ops) to preclude deadlock; verified by
  stress tests (`TST-PROP`).

### 6.5 Cache subsystem (CMP-CACHE)
Three cooperating sub-caches make up a **cache instance** (§6.5A). Their *sizes*
are not fixed here — they are carved out of the instance's `size` per its
read/write `split` (§6.5A/`FR-CACHE-SPLIT`); the values below are the effective
defaults for the out-of-box `global` instance (`size: 4GiB`, `shared-auto`):

| Cache | Contents | Eviction | Default (global instance) |
|---|---|---|---|
| **Read (page) cache** | file byte ranges keyed by (fileId, aligned block) | **selectable** (default ARC); see below | grows within the shared 4 GiB (auto split) |
| **Metadata cache** | dir listings, stat, path→placement | **selectable** (default LRU) + TTL + invalidation on mutation | 100k entries (sized separately, by count) |
| **Write buffer** | dirty pages pending flush (write-back only) | flush by policy/timer/pressure (not "evicted") | grows within the shared 4 GiB; hard cap → backpressure |

- **Selectable replacement policy** (`FR-EVICT`, Should): the read and metadata
  caches' eviction algorithm is configurable per cache instance (§6.5A/§8):
  **`lru`** (default for metadata), **`arc`** (default for read — scan-resistant),
  **`fifo`**, **`lfu`**, **`clock`**/**`clock-pro`**, **`slru`** (segmented LRU),
  **`2q`**, **`mru`**, **`random`**. All implement one internal
  `ICacheEvictionPolicy` so policies are interchangeable and independently
  unit-tested; the write buffer is drained by write policy (§6.8), not by these.

- **Block size** `caches.<id>.blockSize` default 1 MiB (align read-ahead and page
  cache). **Coherency** (`SAFE-COHERE`, Must): a read after a write to the same
  range in the same mount session always returns the written bytes (read cache
  is updated/invalidated on write). Write buffer is authoritative until flushed.
- **Backpressure** (`FR-BACKP`, Must): when the write buffer hits its hard cap,
  new writes block (or degrade to write-through) rather than growing unbounded
  or dropping data.

### 6.5A Cache scope: global vs. dedicated (CMP-CACHE-SCOPE)
Caches are **allocatable, named instances**, not a single fixed pool-wide buffer
— modelled on PrimoCache/PrimoRAMDisk. A cache instance owns a RAM budget split
across the three sub-caches of §6.5 (read / metadata / write) plus its own block
size, **replacement policy** (`FR-EVICT`: lru/arc/fifo/lfu/clock/clock-pro/slru/
2q/mru/random, chosen independently for the read and metadata caches) and
read-ahead settings. Pools **attach** to a cache instance.

- **Global cache** (`FR-CACHE-GLOBAL`, Must): a default shared instance
  (`cache.id: "global"`) that all pools use unless they opt out. Pools sharing it
  compete for space under a weighted-fair eviction (per-pool `weight`), so a busy
  pool can't fully starve another; hit/occupancy is tracked per pool.
- **Dedicated cache** (`FR-CACHE-DEDICATED`, Must): a pool may attach to its own
  private instance with independent size and tuning — e.g. a **write-optimised**
  pool (large write buffer, aggressive coalescing), a **small-I/O** pool (small
  block size, oversized metadata cache), or a **read-streaming** pool (large read
  cache, wide read-ahead). A dedicated cache's RAM is reserved for that pool and
  not shared.
- **Read/write memory split (`FR-CACHE-SPLIT`, Should)** — each instance chooses
  how its RAM budget is divided between the read cache and the write buffer:
  - **`shared-auto`** (default) — one shared budget; the read/write ratio adapts
    continuously to the live workload (a write-heavy burst grows the write buffer,
    a read-heavy phase grows the read cache), within safety bounds so neither can
    be starved to zero and the write buffer can always drain.
  - **`shared-fixed`** — one shared budget split at a **fixed percentage**
    (e.g. `read: 70%, write: 30%`); the boundary does not move.
  - **`separate`** — **independent** read and write allocations, each with its own
    hard cap (fully isolated; a write flood can't shrink the read cache and vice
    versa).
  - (metadata cache is sized independently of this split, by entry count.)
  Whatever the mode, the instance's total still counts against `cacheHost.maxTotal`
  (`SAFE-RAM-BUDGET`) and the write buffer's durability rules (§6.8, `SAFE-RAM`)
  are unaffected — the split only governs *how much* RAM each side may use.
- **Cache profiles (`CFG.cacheProfiles`, Should)** — named presets users pick as
  a *Vorbild* then tweak: `balanced` (default `shared-auto`), `write-optimized`
  (write-weighted split), `small-io`, `metadata-heavy`, `read-streaming`
  (read-weighted split). Each preset just sets the §6.5 knobs.
- **PrimoCache-style options** each instance exposes: separate read vs. write
  cache sizing; **deferred-write latency** (ties into §6.8); cache **block/
  granularity size**; **read-ahead/prefetch**; optional **dynamic sizing** (grow
  into free host RAM, shrink under memory pressure) vs. fixed reservation;
  include/exclude rules by folder. (An **L2 SSD cache** in PrimoCache terms maps
  onto our **fast tier**, §6.7 — the same physical idea, expressed as tiering,
  not duplicated as a second cache layer.)
- **Machine-wide RAM ceiling (`SAFE-RAM-BUDGET`, Must):** the sum of all cache
  instances (global + every dedicated) is validated against a configurable host
  ceiling (`cacheHost.maxTotal`, default a % of physical RAM) and **may never be
  over-committed**. Fixed reservations are allocated first; dynamic caches share
  the remainder and shrink first under pressure. Configuration that would exceed
  the ceiling is rejected (`CFG-VALIDATE`). This protects host stability
  regardless of how many pools are mounted.
- **Lifecycle:** cache instances are created/resized without unmount where
  possible (`CFG.reload`); shrinking flushes/evicts down to the new cap first
  (never drops dirty write-buffer data — it is flushed per policy, upholding
  `SAFE-NOLOSS`).

### 6.6 Read-ahead (CMP-RA)
- **Sequential detection** (`FR-RA`, Should): detect sequential access per
  handle; prefetch up to `CFG.readAhead.maxWindow` (default 8 MiB) ahead,
  ramping from `minWindow` (default 1 MiB).
- **Adaptive** (Should): grow window on sustained sequential hits, shrink/stop
  on random access; never prefetch across EOF; prefetch respects cache cap and
  is dropped first under pressure.

### 6.7 Storage tiers & the write cascade (CMP-TIER)
Generalises landing zones into an ordered **tier stack** that data cascades down.
Each member declares a tier; the engine writes to the fastest eligible tier first
and migrates downward in the background.

| Tier | Medium | Role | Volatile? |
|---|---|---|---|
| **T0 — RAM** | the write buffer (§6.5) | absorb bursts at memory speed | **yes** — never a durable copy on its own (`SAFE-RAM`) |
| **T1 — fast** | SSD/NVMe members (`role: landing`) | absorb writes durably, fast | no |
| **T2 — capacity** | HDD members, local *or* remote (UNC/FTP/SFTP/WebDAV) | bulk storage + duplication home | no (but remote durability varies, §6.1) |

- **Cascade ingest** (`FR-TIER`, Must): a write goes to **T0 (RAM)** first; when
  the RAM write buffer approaches its cap it spills to **T1 (fast)** if a fast
  member has free space above `lowWatermark`; when T1 is full/absent it spills to
  **T2 (capacity)**. Each downward hop is chosen by placement (§6.3) and honours
  `SAFE-PHYS`.
- **Background drain** (`FR-LZ-DRAIN`, Must): the drainer moves data down the
  stack (T1→T2), creates the required shadow copies to reach the duplication
  level, then frees the upper tier — via Core `CopyToDrive`/`MoveToDrive` +
  `DuplicationManager`, all under the journal. Remote T2 backends drain
  whole-file (§6.1).
- **Watermarks** (`CFG.tiers[*].{high,low}Watermark`, defaults 90% / 75% per
  tier): drain accelerates above `high`; ingest stops using a tier below free =
  `low` and spills down.
- **RAM durability rule** (`SAFE-RAM`, Must): T0 only *accelerates*. A write is
  **never acknowledged from RAM alone** unless the folder's policy is explicitly
  `performance` *and* the user has set `acceptVolatileAck: true` (§6.8); default
  is off. `fsync` always forces data out of RAM to ≥ `minCopiesBeforeAck` durable
  copies before returning.
- **Tier durability caveat** (`SAFE-LZ`, Must): a file present only on one T1
  member with no second copy is at single-drive risk until drained/duplicated;
  `minCopiesBeforeAck` (default 2 for duplicated folders) governs whether that
  state may be acknowledged. A remote T2 member lacking `DurableFlush` cannot be
  the sole durable copy (`SAFE-REMOTE`, §6.1).

### 6.8 Write-policy state machine (CMP-WP)
Per-pool default, per-folder override. Modes:

- **write-through** (`FR-WT`, Must): `Write`/`Flush` do not return success until
  **all required copies** (per duplication level, ≥ `minCopiesBeforeAck`) are
  durably written and fsync'd. Slowest, safest.
- **write-back** (`FR-WB`, Must): return success once data is in the write
  buffer **and** `minCopiesBeforeAck` durable copies exist (default: 1 on the
  landing zone or target volume; 2 for critical folders). Remaining copies +
  migration complete asynchronously.
- **deferred** (`FR-DEF`, Should): write-back plus a coalescing delay
  (`CFG.write.deferWindow`, default 5 s) so bursts to the same file batch into
  fewer physical writes; a hard `maxDeferSeconds` (default 30 s) bounds risk.
- **performance** (`FR-PERF-MODE`, Should): the full tier cascade (§6.7) at
  maximum speed — RAM → fast → capacity — with aggressive coalescing. By default
  it still respects `minCopiesBeforeAck` (ack waits for that many durable T1/T2
  copies, *not* RAM). Only if the user opts in with `acceptVolatileAck: true`
  does it acknowledge from RAM before any durable copy exists (bypassing
  `minCopiesBeforeAck`, §8) — a deliberate speed-over-safety trade the GUI
  surfaces with a clear warning and that `fsync` still overrides (`SAFE-RAM`).
  Intended for scratch/re-creatable data.

State per dirty file: `RamBuffered → Landed(tier,nCopies) → Draining →
Replicated → Clean`. Transitions are journalled (§6.9). `fsync`/`Flush` forces
progression to at least `minCopiesBeforeAck` durable copies before returning
(`SAFE-FSYNC`, Must) — a successful `fsync` is an absolute durability promise
regardless of policy, **including `performance`/`acceptVolatileAck`**.

### 6.9 Journal / write-ahead log & crash recovery (CMP-WAL)
The heart of `G6`. A per-pool append-only journal (sidecar under the pool's
metadata area, ignorable by the original product) records the *intent* of every
non-atomic mutation **before** it touches disk, and a completion record after.

- **Journalled operations** (`SAFE-WAL`, Must): create, rename, delete,
  truncate, landing-zone drain/migrate, shadow-copy creation/removal,
  rebalance moves.
- **Ordering** (`SAFE-ORDER`, Must): intent record fsync'd → perform via
  temp-file + atomic rename (`SAFE-ATOMIC`) → completion record. Data files are
  never mutated in place in a way that can tear.
- **Recovery on mount** (`FR-RECOVER`, Must): replay the journal — roll forward
  completed-but-unacked operations, roll back/clean up incomplete ones (delete
  orphaned `*.TEMP.$DRIVEBENDER`), then run a fast integrity pass that reconciles
  the duplication invariant **and** the out-of-band `(size,mtime)` delta scan
  (§6.15) so externally-modified members are caught before serving. Mount is
  refused (or read-only) only if reconciliation cannot guarantee safety, with a
  clear diagnostic.
- **Guarantee** (`SAFE-NOLOSS`, Must): no operation that was acknowledged to the
  application (write-through success, or write-back after `minCopiesBeforeAck`,
  or successful `fsync`) may be lost or partially applied after any crash.
- **Idempotent replay** (`SAFE-IDEMP`, Must): replay is safe to run any number
  of times; interrupted recovery re-runs cleanly.

### 6.10 Background workers (CMP-BG)
A single scheduler runs cooperative, cancellable, rate-limited jobs so
background work never starves foreground I/O:

- **Drainer** — landing-zone → capacity migration (§6.7).
- **Duplicator** — bring files up to duplication level (reuses
  `DuplicationManager` / `FixMissingShadowCopies`).
- **Balancer** — free-space equalisation (reuses `Rebalancer`); only moves
  *clean, replicated* files; pauses under foreground load.
- **Scrubber** (Should) — idle-time checksum verification, bit-rot repair, and
  out-of-band-change reconciliation (§6.15; reuses/extends `IntegrityChecker`).

`FR-BG-THROTTLE` (Must): background I/O is capped
(`CFG.background.maxThroughput`, default 50% of idle bandwidth, ~0 under active
foreground load) and every job is journalled and resumable.

### 6.11 Configuration resolver (CMP-CFG)
Hierarchical, with effective value = folder override → pool → global default.
Live reload (`CFG.reload`, Should) applies non-structural changes without
unmount. See §8 for the full schema.

### 6.12 Mount host, CLI & platform invocation (CMP-HOST)
`DriveBender.Mount` picks WinFsp/FUSE by OS. Clean unmount flushes all dirty
state and quiesces workers before releasing the mount (`FR-CLEAN-UNMOUNT`, Must).
A mount is always ultimately "mount **this manifest** at **this target**", so
every invocation path resolves to `(manifest, target)`:

- **Core CLI** (`FR-MOUNT-CLI`, Must): `dbmount mount --manifest <path|poolId>
  [--target X:] [--foreground] [--read-only]`, `dbmount unmount <target|poolId>`,
  `dbmount status [--json]`, `dbmount list`. `--target` defaults to the manifest's
  `mount.target`. Exit codes and `--json` are scripting-friendly.
- **Windows — picker** (`FR-MOUNT-WIN-GUI`, Must): from the GUI/Explorer a user
  picks a `*.dbpool.json` manifest and mounts it; a file association + right-click
  "Mount DriveBender pool" is registered.
- **Windows — unattended** (`FR-MOUNT-WIN-CLI`, Must): the same CLI is the
  Task Scheduler / autorun surface — `dbmount mount --manifest C:\pools\media.json
  --target X:` runs headless at logon/boot; also installable as a **Windows
  service** (`dbmount install-service --manifest … --target …`) so the pool
  mounts before login. Credentials for remote members come from the service
  account's credential store.
- **Linux — fstab** (`FR-MOUNT-FSTAB`, Must): ship a `mount.drivebender` (a.k.a.
  `/sbin/mount.fuse.drivebender`) helper so `mount`/`fstab` work natively:
  ```fstab
  /etc/drivebenderutility/pools/media.json  /mnt/media  fuse.drivebender  defaults,_netdev,allow_other  0 0
  ```
  The device field is the **manifest path**; options pass through to the FUSE
  adapter. `_netdev` for pools with remote members. A **`systemd`** unit template
  is also provided for non-fstab setups.
- **Auto-remount** (Should): on removable/UNC member reappearance the service can
  auto-(re)mount pools flagged `automount: true`.

### 6.13 Cross-platform GUI (CMP-UI)
`DriveBender.App` — an **Avalonia** desktop app (Windows + Linux from one
codebase, MVVM) that is the primary control surface. **Everything configurable in
the manifest/config (§8) is editable here** (`FR-UI`, Must) — the GUI is a
first-class client of the same validated config layer (`CFG-VALIDATE`), never a
parallel settings store.

- **Pool dashboard** — every pool with health at a glance: mounted/target,
  member list with online/offline + physical-volume + tier badges, degraded/
  same-disk/remote-durability warnings (from `PoolHealth`, §6.0.7), free/used per
  tier, duplication coverage.
- **Create/edit wizard** — build a manifest by adding members of any kind (browse
  local folder, pick removable, type UNC, add FTP/SFTP/WebDAV with a
  **credential picker** that stores secrets in the OS keychain, never in the
  manifest); assign tier/role and `reserveBytes`; choose mount target
  (drive letter or directory); import/export/adopt (§6.0.6).
- **Tuning panels** — write policy (incl. `performance` with an explicit,
  clearly-worded volatile-ack opt-in), tier watermarks, read-ahead, per-folder
  overrides via a folder tree, background throttle, and **cache management**:
  create/size named cache instances, attach a pool to the global or a dedicated
  cache, pick the **replacement policy** (LRU/ARC/FIFO/… `FR-EVICT`), the
  **read/write split** (auto / fixed-% / separate, `FR-CACHE-SPLIT`) shown as a
  live-adjustable bar, and a profile — with a live meter of total RAM vs. the
  `cacheHost.maxTotal` ceiling so over-commit is impossible to configure. Every field shows its effective
  (inherited) value and validates live with the same rules the CLI uses; bad
  values are rejected in-form.
- **Live monitoring** (`FR-UI-LIVE`, Should) — real-time charts from the metrics
  stream (§12): cache hit rate, dirty/RAM bytes, per-tier drain lag, per-member
  throughput/latency/queue depth, background job progress; alerts for degraded
  states and single-copy risk.
- **Per-pool live activity view** (`FR-UI-MAP`, Should) — click a pool to open a
  detail screen that shows *exactly what is happening right now*, driven by the
  engine activity stream (`OPS-EVENTS`, §12):
  - **Topology / tier diagram** — RAM (T0) → fast (T1) → capacity (T2, incl.
    remote) tiers and the physical members within each, each node showing live
    used/free, temperature (hot/warm/cold), online/offline, and role.
  - **Data-placement map** — where data currently lives: a browsable folder/file
    tree *and* an aggregate treemap, each node showing which tier/member holds
    its **primary** and **shadow copies** (so a user can see, e.g., "this file is
    on the SSD landing tier, not yet duplicated to capacity"). Scales to large
    pools by aggregating to folder level and lazy-loading detail on drill-down.
  - **Animated flows** (`FR-UI-ANIM`, Should) — active operations rendered as
    animated flows along the edges between nodes: landing-zone drains (T1→T2),
    duplications (member→member), rebalance moves, reads/writes, remote transfers.
    Each flow encodes **direction, throughput, and the item** in motion; hovering
    /clicking a flow reveals file, size, source→destination, and *why* (drain /
    duplicate / rebalance / user I/O). Completed moves settle into the placement
    map so the picture stays truthful.
  - **Playback controls** — pause/resume the animation, filter by activity type,
    and a short rolling history so a burst can be reviewed after it happens.
  - **Cost guard** (`NFR-UI-LIVE`) — the view subscribes to a **sampled/
    coalesced** event feed (rate-limited server-side); it never asks the engine to
    enumerate the whole pool per frame, and rendering degrades to aggregate flows
    when activity is very high, so visualisation never perturbs I/O.
- **Mount controls** — mount/unmount, set automount, install service/systemd unit,
  copy the equivalent CLI command / fstab line for scripting.
- **UX bar** (`NFR-UI-UX`, Must): responsive, keyboard-navigable, light/dark
  themes, accessible (screen-reader labels, sufficient contrast), consistent on
  both OSes; no action that risks data proceeds without a clear confirmation and
  plain-language explanation. Long operations are async with progress + cancel.
- **Localisation** (`FR-I18N`, Should) — all user-facing strings in the GUI and
  CLI come from resource files (English default + German shipped), selected from
  the OS locale with a manual override; numbers, sizes, dates and times format
  per locale. No user-facing string is hard-coded (enforced by a test). The
  resource system is open to further languages (Could).
- The GUI talks to the mount daemon over a small local IPC/management API so it
  can manage running mounts without hosting the engine itself; it degrades to
  read-only status display if the daemon isn't running.

### 6.14 Optional per-pool trash (CMP-TRASH)
`FR-TRASH` (Should): when enabled for a pool, `Unlink`/`RemoveDir` moves items to
a hidden pool trash (`.drivebenderutility/trash/…`, kept out of the mounted
namespace, `FR-HIDE`) instead of deleting all copies, so deletes are recoverable.

- **Retention** (`CFG.trash.*`): keep until an age (`retention`, default 7 days)
  and/or a size cap (`maxSize`, default 5% of pool) is exceeded, then auto-purge
  oldest-first. Trashed bytes count toward pool usage and are shown in the GUI.
- **Space relief** (`trash.dropDuplicatesInTrash`, default on): trashed items may
  be reduced to a single copy to reclaim space while still being restorable.
- **Operations**: `pool trash list|restore|purge` (CLI) and a GUI trash view;
  restore returns the item to its original path and re-establishes duplication.
- **Safety**: trash moves are journalled (`SAFE-WAL`); a same-volume move is a
  rename (cheap), cross-tier is a move; purge is a real delete of all remaining
  copies. Disabled by default → deletes are permanent (today's behaviour).
- **Non-goal for v1**: OS-native Recycle Bin integration on Windows (Could, §3.3);
  the built-in pool trash works identically on both platforms.

### 6.15 Integrity: checksum DB, scrub & out-of-band detection (CMP-SCRUB)
DriveBender stores no checksums, and — critically — a manifest pool's members are
ordinary folders/drives that may be **written to outside the driver** (mounted on
another machine, edited directly, touched by Division-M). This component both
finds silent **bit-rot** and safely reconciles **out-of-band (OOB) changes**.

- **Checksum database** (`FR-CHECKSUM`, Should): a per-pool DB (redundant sidecar
  like the manifest, `SAFE-MANIFEST`-style) records, per physical copy,
  `{ memberId, relPath, size, mtime, fastHash, verifiedAt }` (fast hash =
  xxHash3/BLAKE3; an optional `strongHash` for paranoia mode). Entries are
  written **on the write path** (the data is already in RAM at flush, so no extra
  read) and updated atomically with the file via the journal.
- **Scrubber** (`FR-SCRUB`, Should): a background/idle, throttled job
  (`CFG.background.scrubberSchedule`) re-hashes copies and compares to the DB and
  across copies. A quick pass uses `(size, mtime)` to skip unchanged files; a
  deep scrub re-hashes everything (Could, §3.3). Reuses/extends
  `IntegrityChecker`.
- **Change classification** (`SAFE-OOB`, Must) — on any detected divergence
  between a copy and its DB entry, classify **before** acting:
  1. **Bit-rot / silent corruption** — content hash differs but `(size, mtime)`
     match the DB (the FS layer never saw a write). → **Repair** from a copy whose
     hash matches the DB; quarantine the corrupt copy; alert. If *no* copy
     matches the recorded hash → mark **unrecoverable**, alert, and never
     overwrite the last data.
  2. **Legitimate out-of-band edit** — content hash differs *and* `(size, mtime)`
     advanced beyond the DB. → **Accept** the changed copy as authoritative,
     refresh the DB, invalidate caches for that path, and re-propagate the new
     content to the other copies (bringing duplication back in sync). Logged as an
     external edit, not an error.
  3. **Conflict** — two or more copies diverged *inconsistently* (both edited
     out-of-band to different content), or the heuristic is ambiguous (e.g. equal
     mtime, different hash across copies). → **Never auto-resolve destructively**:
     keep all versions (quarantine the losers under `.drivebenderutility/conflicts/`),
     surface a conflict for user/GUI resolution.
- **Mount-time reconciliation** (`FR-OOB-MOUNT`, Must): because a member may have
  been used elsewhere while unmounted, mount runs a fast `(size, mtime)` delta
  scan (full hashing deferred to the scrubber / on first access) and applies the
  same classification, so the pool converges to a correct, cache-coherent state
  before serving stale data.
- **Heuristic honesty** (`SAFE-OOB`): mtime can be spoofed or skewed (removable
  media across machines with different clocks), so classification is
  **conservative** — when unsure it treats the case as a *conflict* (keep
  everything) rather than guessing; it never deletes or overwrites the only
  remaining copy of any content.

---

## 7. Functional requirements — filesystem semantics

| ID | Priority | Requirement (given-when-then acceptance) |
|---|---|---|
| FR-CREATE | Must | *Given* a valid parent, *when* an app creates a file, *then* it appears in the pool namespace and is placed per §6.3. |
| FR-READ | Must | *When* reading any offset/length within a file, *then* exact bytes are returned; reads past EOF return 0. |
| FR-WRITE | Must | *When* writing at any offset (incl. sparse/append), *then* the file reflects the write per the active policy; overwrites and holes handled. |
| FR-TRUNC | Must | `SetLength` grows (zero-fill) or shrinks correctly across all copies. |
| FR-RENAME | Must | **Namespace-atomic** within the pool (the name flips atomically on every member holding a copy, journalled); a cross-folder move then reconciles the *destination* folder's duplication level in the background under the journal (may transiently over/under-duplicate, never lose data). Overwrite-on-rename respects `RenameFlags`. |
| FR-DELETE | Must | Unlink removes **all** primary + shadow copies and journals it; no orphan copies remain. If pool trash is enabled (`FR-TRASH`, §6.14) the copies are moved to the hidden trash instead of purged, and are restorable until retention/size limits purge them. |
| FR-DIR | Must | mkdir/rmdir/readdir consistent across volumes; the pool presents a single merged namespace; `FOLDER.DUPLICATE.$DRIVEBENDER` and `*.$DRIVEBENDER` sidecars are **hidden** from the mounted view (`FR-HIDE`, Must). |
| FR-STAT | Must | Size = logical size (not sum of copies); timestamps preserved; `StatFs` reports pool aggregate free/total with duplication overhead accounted, `reserveBytes` subtracted, and members sharing a physical volume de-duplicated. Members whose backend cannot report capacity (some FTP/WebDAV) are excluded from the free/total aggregate with a documented convention, not counted as zero or infinite. |
| FR-ATTR | Should | Attributes/mode/timestamps settable; Win32 attributes ↔ POSIX mode mapped best-effort; xattr/ADS passthrough best-effort. |
| FR-SHARE | Should | Windows share modes & byte-range locks honoured within the mount; advisory locks on Linux. |
| FR-CONCURRENT | Must | Concurrent handles to the same file behave per platform norms; no cache incoherence (§6.5). |
| FR-BIGFILE | Must | Files larger than any single free region are still limited to one volume (whole-file model) — if no single volume fits, return `NoSpace` clearly (documented limitation). |
| FR-NOTIFY | Should | Emit filesystem change notifications so Explorer/`inotify` consumers refresh live (WinFsp notify / FUSE `inval`), for both foreground ops and background moves that alter placement (not the merged name). |
| FR-LINK | Should | Symlinks supported where the platform + backing store allow (POSIX symlink; Windows reparse). **Hardlinks** and device/FIFO special files: supported best-effort or explicitly reported `NotSupported` — behaviour pinned so `pjdfstest` outcomes are deterministic. |
| FR-INODE | Must | Each node has a **stable 64-bit file id** for the mount session (consistent `st_ino` / WinFsp index number), preserved across rename and required for hardlink identity; ids survive remount deterministically. |
| FR-CASE | Must | Case sensitivity follows the mount platform (Windows case-insensitive/‑preserving, Linux case-sensitive). Cross-member **name collisions** (same relative path, distinct files, not primary/shadow) resolve by a deterministic rule (documented) and are surfaced as an integrity issue, never silently shadowing data. |
| FR-PERMS | Should | Ownership/permissions are **passed through** to the backing store: POSIX uid/gid/mode on Linux, ACLs on NTFS; FUSE `uid`/`gid`/`umask` and `allow_other` mount options supported. Where a backend can't represent perms (FTP/WebDAV/TFTP), a documented default mode is presented and set-perm returns `NotSupported` rather than lying. |
| FR-ATIME | Should | `atime` update policy configurable (`relatime` default, `noatime` for performance); `mtime`/`ctime`/`btime` preserved at the backing store's precision. |
| FR-MULTI | Must | Multiple pools mount simultaneously in one host/daemon, sharing the global cache budget and background scheduler without cross-pool interference or a shared failure. |

---

## 8. Configuration schema & tuned defaults (`CFG-*`)

Config is JSON (YAML accepted). Resolution order (lowest→highest): built-in
defaults → global file → **pool config** → per-folder override → **per-member**
overrides (`memberOverrides`, e.g. per-member `queueDepth` or `network`). For a
**manifest pool** the pool config *is* the manifest's `defaults` / `folders` /
`memberOverrides` blocks plus the `members` identity array (§6.0.1) — one file
defines membership *and* tuning; for a **native pool** it is a separate per-pool
file keyed by pool GUID/name. Machine-global keys (`cacheHost`, `caches`) live in
the **global** config file, not in a per-pool manifest; a manifest attaches to a
cache by name via `cache.use` (or defines one inline via `cache.dedicated`). All
keys optional; omitted keys inherit. Defaults below are the **performance-tuned
out-of-box** values (`CFG-DEFAULT`, Must: zero-config mount must be safe and fast
on typical HW).

```jsonc
{
  "pool": "MediaPool",                     // pool name or GUID; omit for global defaults
  "mount": {
    "target": "P:",                        // Windows drive letter, dir path, or Linux mountpoint
    "readOnly": false,
    "volumeLabel": "MediaPool"
  },
  "write": {
    "policy": "write-back",                // write-through | write-back | deferred | performance
    "minCopiesBeforeAck": 2,               // hard safety floor for duplicated folders; 1 for non-duplicated
    "deferWindow": "5s",
    "maxDeferSeconds": 30,
    "acceptVolatileAck": false,            // performance-mode only: ack from RAM before a durable copy (risky)
    "fsyncIsDurable": true                 // fsync always forces min-copies durability (cannot be disabled)
  },
  // ── cache scoping (global-scope keys; §6.5A) ──────────────────────────────
  "cacheHost": {
    "maxTotal": "50%"                      // machine-wide RAM ceiling for ALL caches; never over-committed
  },
  "caches": {                              // named, allocatable cache instances
    "global": {                            // the default shared instance
      "profile": "balanced",
      "size": "4GiB",                      // total RAM budget for this instance (read+write share)
      "split": { "mode": "shared-auto" },  // shared-auto | shared-fixed | separate
      "blockSize": "1MiB",
      "readEviction": "arc",               // lru|arc|fifo|lfu|clock|clock-pro|slru|2q|mru|random
      "metadataEntries": 100000, "metadataTtl": "30s",
      "metadataEviction": "lru",
      "dynamic": true                      // grow into free RAM, shrink under pressure
    },
    "media-dedicated": {                    // read-weighted, fixed split, isolated read/write
      "profile": "read-streaming", "size": "8GiB", "readEviction": "slru", "dynamic": false,
      "split": { "mode": "shared-fixed", "read": "80%", "write": "20%" }
    },
    "db-dedicated": {                        // write-optimised: fully separate read/write pools
      "profile": "write-optimized",
      "split": { "mode": "separate", "readCacheMax": "512MiB", "writeBufferMax": "4GiB" }
    }
  },
  // ── per-pool cache attachment ─────────────────────────────────────────────
  "cache": {
    "use": "global",                       // attach to a named instance…
    "weight": 1.0,                         // …fair-share weight when sharing "global"
    // — OR — define a dedicated instance inline for this pool only:
    "dedicated": null                      // e.g. { "profile": "write-optimized", "writeBufferMax": "2GiB" }
  },
  "readAhead": {
    "enabled": true,
    "minWindow": "1MiB",
    "maxWindow": "8MiB",
    "adaptive": true
  },
  "io": {
    "queueDepthPerVolume": { "hdd": 2, "ssd": 8 },
    "mirrorReadSplitThreshold": "8MiB",    // reads ≥ this may split across copies
    "elevatorOrdering": true
  },
  "placement": {
    "strategy": "most-free-space",         // most-free-space | round-robin | least-used
    "shadowNeverSamePhysical": true
  },
  "tiers": {                               // the write cascade (§6.7); RAM (T0) is the cache.writeBufferMax above
    "fast":     { "members": [],           // e.g. ["SSD-1"]; empty = no fast tier
                  "highWatermark": "90%", "lowWatermark": "75%", "drainConcurrency": 2 },
    "capacity": { "members": ["*"],        // "*" = all members not assigned to another tier
                  "highWatermark": "95%", "lowWatermark": "85%" }
  },
  "memberOverrides": {                     // per-member tuning keyed by memberId or path
                                           // (member *identities* live in the manifest `members` array, §6.0.1)
    "\\\\server\\share\\pool": {
      "scheme": "unc", "tier": "capacity", "network": true,
      "credential": "cred-ref:MyPool-server",
      "timeout": "30s", "retries": 3, "queueDepth": 4
    },
    "sftp://backup.example/pool": {
      "scheme": "sftp", "tier": "capacity", "network": true,
      "credential": "cred-ref:MyPool-sftp", "wholeFile": true
    }
  },
  "background": {
    "maxThroughput": "50%",                // of idle bandwidth; ~0 under foreground load
    "balancerEnabled": true,
    "duplicatorEnabled": true
    // scrubber schedule lives under "integrity" below
  },
  "safety": {
    "journalEnabled": true,                // cannot be false in Must-tier
    "refuseMountOnUnrecoverable": true,
    "verifyDrainWithChecksum": true
  },
  "integrity": {                           // checksum DB, scrub & out-of-band detection (§6.15)
    "checksumDb": true,
    "fastHash": "xxh3",                    // xxh3 | blake3
    "strongHash": null,                    // e.g. "blake3" for paranoia mode; null = fast only
    "onExternalEdit": "accept-newest",     // accept-newest | conflict-only | read-only-until-reconciled
    "scrubberSchedule": "idle-weekly",     // (also under background); quick vs deep per §6.15
    "deepScrubSchedule": null              // e.g. "monthly"; full re-hash (Could)
  },
  "trash": {                               // optional per-pool recycle (§6.14)
    "enabled": false,                      // off = deletes are permanent (default)
    "retention": "7d",
    "maxSize": "5%",
    "dropDuplicatesInTrash": true
  },
  "locale": "auto",                        // auto | en | de … (GUI/CLI language, §6.13)
  "folders": {                             // per-folder overrides (glob or path)
    "Documents/**": { "write": { "policy": "write-through", "minCopiesBeforeAck": 2 } },
    "Scratch/**":   { "write": { "policy": "performance", "acceptVolatileAck": true }, "duplication": 0 }
  },
  "observability": {
    "logLevel": "info",
    "metrics": { "enabled": true, "endpoint": "127.0.0.1:9723" }
  }
}
```

`CFG-VALIDATE` (Must): config is validated on load; invalid values are rejected
with a precise message and the mount refuses to start rather than silently
falling back to unsafe behaviour. `CFG-SAFE-FLOOR` (Must): with duplication level
`D` (total copies, §6.3), `minCopiesBeforeAck` may never exceed `D` nor be set
below 1; for duplicated folders (`D≥2`) the effective floor is `min(2, D)`.
`acceptVolatileAck` is rejected unless `policy: performance`, never weakens
`fsync`, and — because it acknowledges before *any* durable copy exists — it
**bypasses `minCopiesBeforeAck`** (the two are mutually exclusive at ack time);
the GUI/CLI must echo an explicit volatility warning when it is enabled. Tier
membership may be expressed by a member's `role`/`tier` (§6.0.1) or by
`tiers[*].members`; all sources for a given member must agree, else reject.

`CFG-SCHEMA` (Must): manifest, config, and journal each carry a `schema` version.
A newer app reads older versions and migrates on write (atomically, keeping a
backup); an older app refuses a newer **major** version with a clear message
rather than misinterpreting it. Unknown keys are preserved on rewrite (forward
compatibility) so a future field isn't dropped by an older GUI.

---

## 9. Non-functional requirements

| ID | Priority | Requirement |
|---|---|---|
| NFR-PERF-READ | Must | Sequential read of a duplicated large file ≥ 1.6× single-drive throughput with mirror read-balancing enabled (measured, §11.4). |
| NFR-PERF-WRITE | Must | Write-back ingest to an SSD landing zone sustains ≥ 90% of that SSD's raw sequential write speed until drain backpressure. |
| NFR-PERF-META | Should | Cached `readdir`/`stat` for a 10k-entry directory returns in < 5 ms after warm-up. |
| NFR-LATENCY | Should | Cached read latency < 200 µs; uncached small read adds one physical seek+read only. |
| NFR-SCALE | Should | Correct operation with pools up to 24 volumes and 10M files; memory scales with cache caps, not file count. |
| NFR-STABILITY | Must | 72 h soak under mixed fio/Explorer load with zero leaks, zero corruption, zero deadlocks. |
| NFR-PORT | Must | Identical VFS behaviour on Windows & Linux for the shared contract (verified by a shared conformance suite). |
| NFR-RESOURCE | Should | Idle mount < 100 MB RSS excluding configured caches. |
| NFR-OBSERV | Should | Structured logs via `DriveBender.Logger`; optional metrics (cache hit rate, queue depth, dirty bytes, drain lag, per-volume IOPS/throughput). |
| NFR-PERF-RAM | Should | With `performance` mode, burst writes fitting the RAM buffer complete at ≥ memory-bandwidth-bound speed; sustained writes step down to the fast-tier rate without stalling foreground. |
| NFR-REMOTE | Should | A remote capacity backend never blocks foreground I/O to local members; remote drain proceeds within its bandwidth and retries transient errors without failing the mount. |
| NFR-UI-XPLAT | Must | The GUI builds and runs from one codebase on Windows and Linux with equivalent functionality. |
| NFR-UI-UX | Must | GUI is responsive (<100 ms UI feedback), keyboard-navigable, themed (light/dark), accessible (labels + contrast), and never risks data without a clear confirmation. |
| NFR-UI-LIVE | Should | Dashboard metrics update at ≥ 1 Hz with negligible engine overhead (< 1% CPU). |

---

## 10. Data-safety model (`SAFE-*`) — consolidated

The safety guarantees are the product's core promise. Summary of the invariants
enforced and tested:

1. `SAFE-NOLOSS` — no acknowledged write is ever lost after any crash.
2. `SAFE-ATOMIC` — content becomes visible only via temp-write + fsync + rename;
   no torn files.
3. `SAFE-WAL` / `SAFE-ORDER` — intent journalled and fsync'd before mutation;
   completion after.
4. `SAFE-IDEMP` — recovery/replay is idempotent and resumable.
5. `SAFE-DUP` — duplication level is an invariant the system converges to and
   never violates downward during normal operation.
6. `SAFE-COHERE` / `SAFE-FSYNC` — cache is coherent; fsync is an absolute
   durability barrier.
7. `SAFE-LZ` — single-copy-in-landing-zone risk is explicit and governed by
   `minCopiesBeforeAck`.
8. `SAFE-COMPAT` — on-disk format stays DriveBender-compatible; sidecars are
   removable leaving a valid pool.
9. `SAFE-DEGRADE` — on drive loss/full/IO error, the mount stays up where
   possible, degrades read-only or errors specific ops cleanly, and never
   corrupts surviving data.
10. `SAFE-MANIFEST` — the pool manifest is stored redundantly (registry + every
    member); a pool is reconstructable from any single member marker; manifest
    writes are atomic and versioned.
11. `SAFE-PHYS` — duplicate copies never share a physical volume/failure domain,
    even when members are subfolders of the same disk.
12. `SAFE-OFFLINE` — offline/removable/unreachable members degrade gracefully,
    reroute writes, defer owed duplication, and reconcile on return; no data
    loss or corruption across member disappearance.
13. `SAFE-NONDESTRUCTIVE` — creating/adding a member never destroys pre-existing
    data in a chosen folder without explicit `--force`.
14. `SAFE-RAM` — the RAM tier never counts as a durable copy; no write is
    acknowledged from RAM alone unless `performance` + `acceptVolatileAck`, and
    `fsync` always forces RAM out to durable copies regardless of mode.
15. `SAFE-REMOTE` — a remote backend lacking durable-flush can never be the sole
    durable copy of acknowledged data; capability gaps (no atomic rename) are
    covered by journalled emulation, not silently ignored.
16. `SAFE-OOB` — out-of-band changes and bit-rot are detected via the checksum DB
    and classified conservatively (corruption vs. legitimate external edit vs.
    conflict); repair/accept/quarantine never overwrites or deletes the last copy
    of any content, and ambiguity is kept (never guessed) for user resolution.

Every `SAFE-*` requirement has at least one dedicated fault-injection test
(§11.3). **A `SAFE-*` regression is release-blocking.**

---

## 11. Test strategy (`TST-*`)

Follows the house standard: TDD/BDD, ISTQB given-when-then, equivalence classes,
boundary values, exceptional cases — never happy-path-only. NUnit +
FluentAssertions + Moq, categorised (`Unit/Integration/EndToEnd/Performance/
Regression` × `HappyPath/EdgeCase/Exception`) so CI can gate the pure-unit tier
headlessly (matches existing `ci.yml` split).

### 11.1 Unit tests (`TST-UNIT`, Must)
Every engine component is unit-tested against the **`FakeVolumeIO`** in-memory
backend (`TST-FAKE`) — no real disk, fully deterministic, runs in CI's headless
tier. Coverage per component:

- **Cache**: hit/miss/evict; boundary block alignment (offset 0, blockSize-1,
  blockSize, EOF-1, EOF, past-EOF); coherency after overlapping writes;
  backpressure at hard cap; concurrent readers/writer.
- **Eviction policies (`FR-EVICT`)**: each `ICacheEvictionPolicy`
  (lru/arc/fifo/lfu/clock/clock-pro/slru/2q/mru/random) unit-tested against a
  known access trace for the expected victim order and hit-rate ordering
  (e.g. arc/slru beat lru under scan; fifo ignores recency); policy is swappable
  per instance without affecting correctness, only hit rate.
- **Cache scope (§6.5A)**: pools sharing the `global` instance evict by weighted
  fair-share; a dedicated instance is isolated from global pressure; sum of all
  instances never exceeds `cacheHost.maxTotal` (`SAFE-RAM-BUDGET`) — over-commit
  config is rejected; dynamic caches shrink before fixed reservations under
  pressure; resizing/shrinking flushes dirty write-buffer data rather than
  dropping it (`SAFE-NOLOSS`); profile presets expand to the expected knobs.
- **Read/write split (`FR-CACHE-SPLIT`)**: `shared-auto` shifts the ratio toward
  the active side under read- vs write-heavy traces yet never starves either to
  zero and always leaves the write buffer able to drain; `shared-fixed` holds the
  boundary regardless of load; `separate` keeps read and write caps fully
  independent (a write flood can't shrink the read cache); every mode's totals
  still respect `cacheHost.maxTotal`.
- **Read-ahead**: sequential ramp, random-access shrink, EOF clamp, prefetch
  drop under pressure, no over-read.
- **Placement**: most-free/round-robin/least-used; shadow-never-same-physical;
  no-space fallback; landing-zone eligibility at watermark boundaries.
- **Write-policy state machine**: every transition; write-through blocks until
  N copies; write-back acks at `minCopiesBeforeAck`; deferred coalescing &
  `maxDeferSeconds` bound; fsync forces durability in every mode; `performance`
  respects `minCopiesBeforeAck` by default and only acks from RAM when
  `acceptVolatileAck` is set — and even then `fsync` forces durability
  (`SAFE-RAM`).
- **Tier cascade**: RAM→fast→capacity spill at each watermark boundary; drain
  T1→T2 reaches duplication level then frees the upper tier; spill when a tier is
  absent/full; placement honours `SAFE-PHYS` across tiers.
- **Backends & capability adaptation**: `FakeRemoteBackend` profiles — missing
  `AtomicRename` uses journalled emulation; missing `DurableFlush` is excluded
  from `minCopiesBeforeAck` and never the sole copy (`SAFE-REMOTE`); latency /
  disconnect maps to the offline path; whole-file put/get round-trips; credential
  resolver reads by reference, never plaintext (`SEC-CRED`).
- **Integrity / OOB (`SAFE-OOB`)**: with a faked backend that mutates a copy
  behind the driver — (a) content changed, `(size,mtime)` unchanged → classified
  **bit-rot**, repaired from a DB-matching copy, corrupt copy quarantined; (b)
  `(size,mtime)` advanced → classified **external edit**, accepted as
  authoritative, DB refreshed, caches invalidated, re-propagated; (c) two copies
  diverge / ambiguous mtime → **conflict**, all versions kept, nothing
  overwritten; (d) no good copy → **unrecoverable**, last data untouched.
  Checksum DB written on the write path (no extra read) and reconstructable from
  redundancy; mount-time `(size,mtime)` delta scan applies the same rules.
- **Trash (`FR-TRASH`)**: unlink with trash on moves (not purges) all copies;
  restore returns to original path and re-duplicates; retention/size purge oldest
  first; `dropDuplicatesInTrash` reduces copies but keeps restorability; trash
  moves journalled and crash-safe; disabled → permanent delete.
- **Journal/recovery**: replay after injected crash at *every* step boundary
  (property test: crash-point sweep) → asserts `SAFE-NOLOSS`, `SAFE-IDEMP`,
  duplication reconciled, no orphan temps.
- **Path/namespace**: create/rename/delete/mkdir edge cases; sidecar hiding;
  case sensitivity per platform; long paths / Unicode.
- **Config resolver**: member>folder>pool>global precedence; validation rejects
  bad/unsafe values; `minCopiesBeforeAck` floor/ceiling.
- **Pool model / manifest**: parse/validate manifest; member resolution by marker
  content when the `path` hint is wrong (letter change); native scan → virtual
  manifest equals the equivalent explicit manifest; two members on one physical
  volume detected as one failure domain (`SAFE-PHYS`); shared-volume free-space
  de-dup + `reserveBytes` (`FR-SPACE-SHARED`); offline member → degraded open,
  write reroute, deferred duplication, reconcile-on-return (`SAFE-OFFLINE`);
  manifest redundancy — reconstruct from any single member marker, atomic
  versioned write, registry↔marker conflict resolution (`SAFE-MANIFEST`);
  UNC-member durability probe gates `minCopiesBeforeAck` eligibility; `adopt` and
  `create` are non-destructive without `--force` (`SAFE-NONDESTRUCTIVE`). The
  `FakeVolumeIO` backend models per-member physical-volume identity and
  online/offline transitions so all of this runs headless.

### 11.2 Property-based / invariant tests (`TST-PROP`, Should)
Randomised operation sequences (create/write/rename/delete/flush/crash) against
a **model oracle** (a simple in-memory reference filesystem). After each
sequence and after simulated crash+recovery, assert the mounted view equals the
oracle and all `SAFE-*` invariants hold.

### 11.3 Fault-injection tests (`TST-FAULT`, Must)
`FakeVolumeIO` can inject: power-loss at op boundary, `NoSpace`, `IoError`, slow
volume, disappearing volume, partial write, fsync failure. Each `SAFE-*`
requirement (§10) maps to ≥1 fault test. Crash-consistency uses the crash-point
sweep from §11.1.

### 11.4 Performance tests (`TST-PERF`, Should — advisory tier)
`fio`/synthetic harness measuring NFR-PERF-*: mirror-read speedup, RAM-burst and
fast-tier ingest, cascade drain throughput, remote-backend drain without
foreground stall, cache hit latency, metadata throughput. Wall-clock; advisory in
CI (matches existing Performance tier policy).

### 11.5 Platform conformance (`TST-CONFORM`, Must for GA)
Real-mount integration on both OSes (own CI job, gated like existing Integration
tier): Linux `pjdfstest` / `fsx`; Windows filesystem behaviour tests + `fsx`
port. Verifies POSIX/Win32 correctness end-to-end.

### 11.6 Soak & E2E (`TST-SOAK`, Should)
72 h mixed-load soak (NFR-STABILITY); E2E use-case scripts for UC1–UC4 including
UC3 real power-loss simulation via forced process kill mid-drain; a
manifest-pool E2E spanning local + UNC + SFTP members mounted via CLI (Windows)
and fstab helper (Linux).

### 11.7 GUI tests (`TST-UI`, Should)
ViewModels are unit-tested headless (MVVM, no window needed): every settings
panel round-trips to the same validated config layer and **rejects the exact same
values the CLI rejects** (`CFG-VALIDATE` parity); the `performance` +
`acceptVolatileAck` opt-in surfaces its warning; health/metrics view binds to a
faked `PoolHealth`/metrics stream (degraded, same-disk, remote-durability
states). The **live activity view** (`FR-UI-MAP`/`FR-UI-ANIM`) is tested against
a faked `OPS-EVENTS` stream: topology/placement bind correctly, flows animate for
each op type with the right direction/source→dest, completed moves settle into the
placement map, and the view stays responsive (drops samples, aggregates) under a
flood of synthetic events without unbounded memory growth. Avalonia
headless-platform smoke tests drive mount/unmount and wizard flows. Accessibility
(labels/contrast/keyboard) checked per `NFR-UI-UX`. **i18n (`FR-I18N`)**: a test
asserts no hard-coded user-facing strings, both `en` and `de` resource sets load
and are complete (no missing keys), and locale-specific size/date formatting is
applied.

`TST-GATE` (Must): the pure-unit + fault-injection tiers (engine **and** GUI
ViewModels) are green-gating on every commit; conformance/soak/perf run on
schedule or pre-release, matching the repo's existing tiered CI.

---

## 12. Observability, security, operations

- **Logging** (`OPS-LOG`): route through `DriveBender.Logger`; structured levels;
  every background move/duplicate/drain/recovery logged with file + reason
  (mirrors existing verbose repair logging).
- **Metrics** (`OPS-METRICS`, Should): cache hit ratio (per pool + per cache
  instance, incl. the shared global cache §6.5A), dirty/RAM bytes, per-tier drain
  lag, per-member IOPS/throughput/queue depth/latency, recovery events; exposed on
  the local management API the GUI consumes (§6.13).
- **Activity event stream** (`OPS-EVENTS`, Should): a subscribable, **sampled and
  server-side rate-limited** feed of in-flight operations — reads/writes, cache
  admit/evict, landing-zone drains, duplications, rebalance moves, remote
  transfers — each event carrying `{op, path, bytes, from→to (tier/member),
  reason, progress}`. This feeds the GUI's live activity view (`FR-UI-MAP`/
  `FR-UI-ANIM`); it is derived from the same journalled operations, is
  best-effort (dropping samples under load rather than blocking I/O), and never
  requires walking the pool. A point-in-time **placement query** (`where does
  path X live`) is also exposed for drill-down without scanning.
- **Security** (`SEC-*`): honour OS ACLs on the backing volumes; the mount runs
  with the privileges required by WinFsp/FUSE and no more; no path traversal out
  of the pool root; sidecars written with restrictive perms. Admin/root needed
  only where the mount backend requires it.
- **Credentials** (`SEC-CRED`, Must): remote-member secrets (FTP/SFTP/WebDAV/UNC)
  are stored **only** in the OS credential store (Windows Credential Manager /
  Linux Secret Service) and referenced by handle from the manifest; never written
  in plaintext to the manifest, logs, or metrics. The management API is bound to
  localhost and authenticated so the GUI cannot be driven by other users.
- **Alerts**: surface degraded states (drive lost, member offline, single-copy
  risk exceeding policy, remote-durability downgrade, recovery required) to
  logs/metrics and the GUI.

---

## 13. Phased roadmap (aligned to MoSCoW)

- **M0 — Foundations.** `T-CORE-MT` (multi-target Core), new projects & solution
  wiring, `IVolumeIO` + `FakeVolumeIO`, config resolver + schema, CI tiers,
  **the pool model** (`PoolManifest`, `IManifestSource` with both the JSON and
  native-scan adapters, member self-identification/resolution, physical-volume
  identity), `pool create/import/adopt` CLI. *Exit:* engine testable end-to-end
  against the fake; a manifest pool over faked members (incl. subfolder + offline
  member) resolves and opens; native scan yields an equivalent virtual manifest.
- **M1 — Read-only mount (Must core) + mount plumbing.** `IPoolFileSystem`, both
  adapters, path→placement resolver, metadata + read cache, read-ahead,
  request-level parallelism, mirror read-balancing; the mount CLI, Windows
  picker/association, and Linux `mount.drivebender` fstab helper (§6.12). *Exit:*
  pool mounts RO on both OSes via CLI/picker/fstab, conformance read tests pass,
  NFR-PERF-READ met.
- **M2 — Write-through + journal (Must safety).** Write path, `IVolumeIO`
  atomic writes, journal/WAL, crash recovery, duplication maintenance via Core.
  *Exit:* full RW write-through, all `SAFE-*` fault tests green, no-loss proven.
- **M3 — Tiered write cascade (Must perf).** RAM write buffer, write-policy state
  machine, fast/capacity tiers + drainer, background workers + throttling.
  *Exit:* UC2/UC3 pass, NFR-PERF-WRITE met, crash-during-drain recovers.
- **M4 — Remote backends, `performance` mode, integrity & trash (Should).**
  `DriveBender.Backends` (UNC first, then SFTP/FTP/WebDAV) with capability
  negotiation + credential store; `performance` write mode with volatile-ack
  opt-in; **checksum DB + scrubber + out-of-band detection/reconciliation
  (§6.15)**; **optional trash (§6.14)**; deferred/coalesced writes, adaptive
  read-ahead, live config reload, metrics, hot drive-loss. *Exit:* a pool with a
  remote capacity member drains correctly; `SAFE-REMOTE`/`SAFE-RAM`/`SAFE-OOB`
  tests green; an external edit and an injected bit-flip are each handled per
  §6.15. (Checksum-DB write-path hooks land earlier, alongside the journal in
  M2/M3.)
- **M5 — Cross-platform GUI (`DriveBender.App`, Must).** Avalonia app: dashboard,
  create/edit wizard (incl. remote members + credential picker), tuning panels,
  live monitoring + activity view, trash/conflict resolution UI, mount controls,
  service/systemd install; **i18n (en + de, §6.13)**. *Exit:* every §8 setting
  editable in the GUI on Win+Linux; `TST-UI` (incl. i18n) green; `NFR-UI-*` met.
- **M6 — Hardening / GA.** Soak, conformance suites, perf tuning of defaults,
  docs (README mount + GUI usage), WPF-UI retirement once at parity. *Exit:*
  NFR-STABILITY, `TST-CONFORM` green.

---

## 14. Risks & mitigations
| Risk | Mitigation |
|---|---|
| WinFsp/FUSE binding gaps on `net10.0` | Spike bindings in M0; Dokan fallback for Windows; pin known-good versions. |
| Crash-consistency bugs are subtle & catastrophic | Journal-first design, crash-point-sweep property tests, model-oracle diffing, release-blocking `SAFE-*` gate. |
| Whole-file model limits single-file speed & max size | Documented non-goal; mirror read-balancing + request parallelism deliver realistic gains; clear `NoSpace` semantics. |
| Members on the same physical disk defeat duplication | `SAFE-PHYS`: key placement on physical-volume identity, not path; detect & refuse redundant copies in one failure domain; warn at mount. |
| Removable drive letters change / members go offline | Member self-identification by marker content (`FR-RESOLVE-MEMBER`); `SAFE-OFFLINE` degraded mount + deferred duplication + reconcile-on-return. |
| UNC members: weak fsync, latency, auth | Validate durable-flush per share (`SAFE-NET-DURABILITY`); exclude from `minCopiesBeforeAck` if unsafe; credentials via OS store only (`SEC-CRED`). |
| Manifest lost or divergent copies | `SAFE-MANIFEST`: redundant registry+per-member copies, atomic versioned writes, reconstruct from any one, `pool repair-manifest`. |
| Adopting a subfolder that shares a disk with foreign data | `reserveBytes` + shared-volume free-space de-duplication (`FR-SPACE-SHARED`); non-destructive create (`SAFE-NONDESTRUCTIVE`). |
| Cache incoherence / double-write races | Single write-buffer owner per file, coherency tests, backpressure over unbounded growth. |
| Format drift breaks Division-M interop | `SAFE-COMPAT` tests: round-trip a pool through the original product's expectations; sidecars removable. |
| Background work starves foreground | Hard throttle + priority queues; soak test asserts foreground latency under background load. |
| RAM tier mistaken for durable storage | `SAFE-RAM`: never a durable copy; volatile ack is opt-in per folder with a GUI warning; `fsync` always forces out of RAM; crash tests cover RAM-only dirty data. |
| Remote backend semantics (no rename/flush, latency) | Capability negotiation + journalled emulation (`FR-CAP-ADAPT`); excluded from durability role (`SAFE-REMOTE`); isolated in `DriveBender.Backends` so deps don't leak. |
| One GUI codebase drifting per-OS | Avalonia single codebase; headless ViewModel tests + CFG-VALIDATE parity with CLI; run GUI CI on both OSes. |
| fstab/service helper fragility | Ship & test `mount.drivebender` + systemd/service units in the conformance tier; `--json` status for scripting. |
| Mount-backend licensing vs project LGPL | WinFsp is GPLv3-or-commercial, macFUSE is now proprietary, Dokan LGPL, libfuse LGPL — confirm each backend is invoked as a separate process / dynamically at arm's length so LGPL distribution holds; document per-backend license; treat macOS as Could. (§15.15) |
| Change notifications missing → stale Explorer/`ls` | `FR-NOTIFY` via WinFsp notify / FUSE inval for foreground and background placement changes; conformance test that a background move is observable. |
| Out-of-band edit misread as corruption (or vice-versa) → data loss | `SAFE-OOB` conservative classification (corruption vs. edit vs. conflict); never overwrite/delete the last copy; ambiguity kept as a conflict; mtime-spoofing assumed possible. |
| Checksum DB drifts / lost | Stored redundantly like the manifest; written on the write path with the journal; rebuildable by a full scrub; a missing DB degrades to compare-copies integrity, never blocks mount. |
| Trash silently consumes pool space | Retention + `maxSize` auto-purge, `dropDuplicatesInTrash`, GUI shows trash usage; disabled by default. |

---

## 15. Open questions (to resolve before/within M0)
1. WinFsp .NET binding vs a thin P/Invoke layer — confirm during M0 spike.
2. Windows attribute ↔ POSIX mode mapping table — define precisely.
3. Journal location & format — sidecar under pool metadata dir; confirm a name
   guaranteed-ignored by the original product; binary vs append-text.
4. Does `FrameworkExtensions.Corlib` cover `netstandard2.0`, or switch Core
   polyfills to Hawkynt's Backports for the multi-target?
5. Exact default for `minCopiesBeforeAck` on *non-duplicated* folders under
   write-back (1 = landing-only risk) — confirm acceptable default vs. force 2.
6. Metrics format — Prometheus endpoint vs. log-only for v1.
7. Member-marker folder name — confirm `.drivebenderutility/` never collides with
   the `$DRIVEBENDER` namespace and is safely ignored by Division-M on
   drive-root members.
8. UNC durable-flush probe — how to reliably detect whether a given SMB share
   honours `FlushFileBuffers`/`fsync` before trusting it for `minCopiesBeforeAck`.
9. Registry-vs-member manifest conflict resolution — quorum vs. highest-version;
   behaviour when registry and markers disagree after offline edits.
10. Should manifest pools also emit a native-compatible pool GUID on drive-root
    members so Division-M can *also* see them, or stay strictly our-format?
11. Remote backend libraries — pick SFTP/FTP/WebDAV client deps (license,
    maintenance, trimming/AOT compatibility on `net10.0`).
12. Avalonia vs. an additional local web management UI — is the desktop app
    sufficient, or is a headless-server web console also Must for Linux boxes?
13. RAM-cache global-vs-dedicated accounting (§6.5A) — how to cap total RAM
    across a global cache plus per-pool dedicated caches so the machine never
    over-commits; eviction priority between pools sharing the global cache.
14. Duplication-level convention — the legacy Console `set-duplication --level`
    ("0 to disable, 1+ copies") vs. this PRD's `D` = total copies (§6.3). Confirm
    the normalisation and whether the Console CLI is migrated to match.
15. Mount-backend licensing (§14) — verify WinFsp (GPLv3/commercial), macFUSE
    (proprietary), Dokan/libfuse (LGPL) are all compatible with shipping under
    LGPL-3.0; decide whether WinFsp needs the commercial license for a bundled
    distribution or stays a user-installed dependency.
16. Name-collision resolution rule (`FR-CASE`) — the deterministic winner when
    two members hold the same relative path as genuinely different files.
17. Metrics vs management API — one localhost endpoint or two? `metrics.endpoint`
    (§8) vs the authenticated management/`OPS-EVENTS` API (§12) — unify or split.
18. OOB reconciliation default (`integrity.onExternalEdit`) — is `accept-newest`
    the right default for pools whose members are routinely edited elsewhere, or
    should shared/removable members default to `conflict-only`? Also: mtime
    granularity/skew thresholds for the classification heuristic.
19. Checksum DB storage form — embedded key-value store (e.g. LiteDB/SQLite) vs.
    a custom append-log sidecar; size at 10M files; per-member vs. per-pool.
20. Trash scope on cross-machine removable members — a trashed item on a drive
    then used elsewhere; whether trash is per-member or pool-central.

---

## 16. Acceptance criteria for "done" (v1 / GA)
- All **Must** `FR-*`, `NFR-*`, `SAFE-*`, `CFG-*` satisfied with passing tests.
- Pure-unit + fault-injection tiers green in CI on every commit; conformance,
  soak, and perf tiers green pre-release.
- Zero-config mount is safe and fast on typical hardware; every aspect in §8 is
  configurable per pool with documented tuned defaults.
- A power-loss at any instant recovers to a consistent, correctly-duplicated
  pool with no acknowledged data lost (`SAFE-NOLOSS` proven by the crash-point
  sweep and a real kill-mid-drain E2E).
- A manifest pool spanning a drive root, a subfolder, and a UNC share mounts to
  an arbitrary target; survives a member changing drive letter and a member going
  offline/returning with no data loss (`FR-MANIFEST`, `FR-RESOLVE-MEMBER`,
  `SAFE-OFFLINE`, `SAFE-PHYS` proven by tests); a native pool opens through the
  identical code path via its virtual manifest.
- The write cascade works end-to-end: a burst lands in RAM, spills to the fast
  tier, then drains to (local or remote) capacity with duplication satisfied; RAM
  is never a durable copy (`SAFE-RAM`) and a non-durable remote member is never
  the sole copy (`SAFE-REMOTE`).
- The pool mounts unattended on Windows via the CLI (Task Scheduler / service)
  and on Linux via an `fstab` line, and interactively via the Windows picker.
- A **global RAM cache** shared across pools plus at least one pool with a
  **dedicated** cache profile both work, honour their independent caps, and never
  over-commit host RAM (§6.5A).
- The **cross-platform GUI** exposes and validates every §8 setting on Windows
  and Linux, shows live health/metrics, and gates risky actions behind clear
  confirmations (`FR-UI`, `NFR-UI-*`); GUI and CLI run in English and German
  (`FR-I18N`).
- Integrity holds against the real world: an **injected bit-flip** is detected
  and repaired from a good copy, an **external edit** (member modified behind the
  driver) is detected and reconciled without loss, and an ambiguous conflict is
  preserved for resolution (`SAFE-OOB`).
- With **trash** enabled, a deleted file is recoverable within its retention
  window and permanently purged after; disabled, deletes are immediate.
- README updated with mount + GUI usage; this PRD's requirement IDs referenced
  from the implementing PRs.
