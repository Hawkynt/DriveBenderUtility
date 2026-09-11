# 🗂️ DriveBenderUtility

[![License](https://img.shields.io/github/license/Hawkynt/DriveBenderUtility)](https://github.com/Hawkynt/DriveBenderUtility/blob/main/LICENSE)
[![Language](https://img.shields.io/github/languages/top/Hawkynt/DriveBenderUtility?color=8957D5)](https://github.com/Hawkynt/DriveBenderUtility)

[![CI](https://github.com/Hawkynt/DriveBenderUtility/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/DriveBenderUtility/actions/workflows/ci.yml)
![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/DriveBenderUtility?branch=main)
![Activity](https://img.shields.io/github/commit-activity/m/Hawkynt/DriveBenderUtility)

[![Stars](https://img.shields.io/github/stars/Hawkynt/DriveBenderUtility?color=FFD700)](https://github.com/Hawkynt/DriveBenderUtility/stargazers)
[![Forks](https://img.shields.io/github/forks/Hawkynt/DriveBenderUtility?color=008080)](https://github.com/Hawkynt/DriveBenderUtility/network/members)
[![Issues](https://img.shields.io/github/issues/Hawkynt/DriveBenderUtility)](https://github.com/Hawkynt/DriveBenderUtility/issues)
![Code Size](https://img.shields.io/github/languages/code-size/Hawkynt/DriveBenderUtility?color=4CAF50)
![Repo Size](https://img.shields.io/github/repo-size/Hawkynt/DriveBenderUtility?color=FF9800)

[![Release](https://img.shields.io/github/v/release/Hawkynt/DriveBenderUtility)](https://github.com/Hawkynt/DriveBenderUtility/releases/latest)
[![Nightly](https://img.shields.io/github/v/release/Hawkynt/DriveBenderUtility?include_prereleases&sort=date&filter=nightly-*&label=nightly&color=FF9800)](https://github.com/Hawkynt/DriveBenderUtility/releases)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/DriveBenderUtility/total)](https://github.com/Hawkynt/DriveBenderUtility/releases)

> The #1 Spot for dealing with [DriveBender](https://en.wikipedia.org/wiki/Non-standard_RAID_levels#Drive_Extender) pools outside [DriveBender](https://www.division-m.com/drivebender/).

**DriveBenderUtility** is a comprehensive C# solution for managing Drive Bender
storage pools — and mounting them as a live, read/write filesystem on Windows
(WinFsp/Dokan) and Linux (FUSE). Pools are defined by a portable JSON manifest
over arbitrary members: local drives, subfolders, UNC shares, and remote/cloud
endpoints (FTP/SFTP/WebDAV/S3/Azure/Dropbox/OneDrive/Google/Box/Yandex/HiDrive).
It adds a tiered
RAM→SSD→capacity write cascade, configurable duplication and write policies,
crash-safe journaling, bit-rot/SMART health checks with correction, and both a
CLI (`dbmount`) and an animated live web/desktop dashboard. Jump to
[**Quick Start**](#-quick-start) to create and mount your first pool.

![The dashboard: one card per pool, with tier topology and per-member health](docs/screenshots/dashboard.png)

## 🧭 Vision

Drive Bender pooled several disks into one drive letter and then stopped being maintained, leaving
working pools on disks nobody could manage any more. This started as a way to read those pools and
became a pool manager in its own right: local disks, UNC shares and remote storage stacked into one
mount, with duplication, tiering and health checking over the top.

The rule it holds to is that the data stays readable without it. Members keep ordinary files in
ordinary folders, so a pool is recoverable with a file manager if this program is ever the thing that
goes away.

## ✨ Features

### 🔧 Pool Management
- ✅ Create new storage pools with multiple drives
- ✅ Delete existing pools with data preservation options
- ✅ Forget a pool (remove from this machine's list, keep data + on-disk markers)
- ✅ Recover an orphaned pool from a member folder's manifest mirror
- ✅ Take a foreign-claimed folder over for a new pool (explicit consent)
- ✅ Add drives to existing pools with automatic balancing
- ✅ Remove drives with intelligent data migration
- ✅ Replace drives with seamless data transfer
- ✅ Space checking with user warnings

### 🚀 Write & read pipeline
- ✅ Staged writes: in-progress files live under a hidden temp name and only the atomic
  temp→final rename — the last action before the journal releases — makes them appear;
  a crash mid-write leaves no half-written file (fsync publishes early)
- ✅ Striped acks: with a relaxed ack quorum, consecutive blocks rotate across storages
  while the journal-backed owed-sync converges every copy in the background
- ✅ Parallel mirrored writes, parallel mirror-split reads (different offsets from
  different storages at once), per-block read failover, pooled positional I/O handles,
  background read-ahead

### 💾 Advanced Duplication
- ✅ Enable/disable duplication on folders
- ✅ Support for multiple shadow copies (beyond standard 2-copy limit)
- ✅ Configurable duplication levels (0-10 copies)
- ✅ Automatic shadow copy creation across volumes
- ✅ Smart duplication based on file importance

### 🔍 File Integrity & Repair
- ✅ Comprehensive integrity checking with 8 issue types:
  - Missing primary files
  - Missing shadow copies
  - Corrupted files
  - Orphaned shadow copies
  - Size mismatches
  - Timestamp inconsistencies
  - Permission issues
  - Duplicate primaries
- ✅ Automated repair with backup creation
- ✅ Dry-run mode for safe testing
- ✅ Deep scan capabilities
- ✅ Batch repair operations

### 🛡️ Safety Features
- ✅ Dry-run mode (enabled by default)
- ✅ Automatic backups before repairs
- ✅ Space validation before operations
- ✅ User prompts for destructive actions
- ✅ Comprehensive logging and error handling

### 🔒 Type Safety
- ✅ Semantic data types (PoolName, DrivePath, FolderPath, ByteSize, DuplicationLevel)
- ✅ Input validation and sanitization
- ✅ Compile-time safety for critical operations

## 📦 Installation

### Prerequisites

- **Build:** [.NET SDK 10](https://dotnet.microsoft.com/download). The engine,
  backends, `dbmount` and the app are `net10.0`; `DriveBender.Core` also targets
  `net47`/`netstandard2.0`. (Nothing needs Drive Bender installed — native pools
  are auto-discovered if present.)
- **To mount on Windows:** [**WinFsp**](https://winfsp.dev) *or*
  [**Dokan**](https://dokan-dev.github.io) — `dbmount` uses whichever is present
  (WinFsp preferred, Dokan is the no-extra-install fallback). *Installing* the
  driver needs admin (the app/UI can do it for you); *mounting* does not — mount
  as your normal user so the drive is visible in your own Explorer session.
- **To mount on Linux:** `fuse3` (`/dev/fuse`) — e.g. `sudo apt install fuse3`.
- **Remote/cloud members** need nothing extra; the SDKs are bundled.

### 🔨 Build

```bash
dotnet build DriveBender.sln -c Release
```

That produces `dbmount` (the CLI/daemon, `DriveBender.Mount/bin/Release/...`) and
`DriveBender.App` (the desktop shell). To run `dbmount` directly during
development, use `dotnet <path>/dbmount.dll <args>`; a published build gives a
plain `dbmount` executable. The examples below write `dbmount`.

### 🧪 Tests

```bash
dotnet test DriveBender.Vfs.Tests/DriveBender.Vfs.Tests.csproj   # the VFS engine (headless)
dotnet test DriveBender.Tests/DriveBender.Tests.csproj           # legacy Core suite
dotnet test DriveBender.Vfs.Tests/DriveBender.Vfs.Tests.csproj --filter "TestCategory=Unit"
```

## 🚀 Quick start

A **pool** is defined by a portable JSON *manifest* — a set of member folders
(local drives/subfolders, UNC shares, or remote endpoints) plus tuning. You
create it once, then mount it as a live drive.

### 1. Create a pool

```bash
# two local members, duplicated data mounted at X:\ (Windows) …
dbmount pool create --name MyPool --member "D:\" --member "E:\" --mount "X:\"

# … or on Linux, mounted at a directory
dbmount pool create --name MyPool --member /mnt/disk1 --member /mnt/disk2 --mount /mnt/mypool

# an SSD landing zone (fast tier) plus capacity drives
dbmount pool create --name Media --landing "F:\ssd" --member "G:\" --member "H:\" --mount "M:\"

dbmount pool list          # what's discovered (manifest pools + native scan)
dbmount pool export MyPool # print the manifest JSON
```

Creating a pool never destroys existing folder contents without `--force`, and a
folder already owned by another pool is always refused.

### 2. Mount it

```bash
# Windows (WinFsp or Dokan must be installed; run as your normal user — NOT elevated, or the
# drive lands in a different session than Explorer and won't be visible)
dbmount mount --manifest MyPool            # mounts at the manifest's target, or pass --target Y:\
dbmount status                             # what's mounted right now
dbmount unmount X:\                        # clean unmount (flushes dirty data); or Ctrl+C the mount

# Linux
dbmount mount --manifest MyPool --target /mnt/mypool
fusermount3 -u /mnt/mypool                 # or: dbmount unmount /mnt/mypool
```

Now use `X:\` (or `/mnt/mypool`) from Explorer / any app — reads, writes,
rename, delete all work, with duplication, tiering and balancing handled
underneath.

**Mount automatically at boot / login:**

```bash
# Windows service (mounts before login)
dbmount install-service --manifest MyPool --target X:\
# Windows Explorer: register a right-click "mount" for *.dbpool.json manifests
dbmount register-shell

# Linux: install the systemd unit + mount.drivebender fstab helper (run with sudo)
dbmount install-systemd --manifest MyPool
systemctl enable --now drivebender-pool@MyPool.service
#   …or add to /etc/fstab:
#   /etc/drivebenderutility/pools/MyPool.json  /mnt/mypool  fuse.drivebender  defaults,_netdev  0 0
```

### 3. Remote & cloud members

Store the secret once (it goes to the OS credential store, never the manifest),
then reference it by handle:

```bash
# password / key based (FTP, SFTP, WebDAV, S3, Azure…)
dbmount credential-set MyPool-nas --user backup      # prompts for the secret (hidden)
dbmount pool add-member MyPool --member "sftp://backup@nas.local/pool" --credential MyPool-nas

# OAuth providers (Google, OneDrive, Dropbox, Box, Yandex, HiDrive) — browser login
# with your own registered client id (loopback + PKCE, auto-refresh)
dbmount credential-login MyPool-gdrive --provider google --client-id <your-id> --client-secret <your-secret>
dbmount pool add-member MyPool --member "gdrive://backups" --credential MyPool-gdrive
```

Supported schemes: `file`/`unc`, `ftp`/`ftps`, `sftp`, `webdav`/`webdavs`, `s3`,
`azblob`, `azfile`, `dropbox`, `onedrive`, `gdrive`, `gcs`, `box`, `yandex`,
`hidrive` (see the backend table above for the secret format each expects).

### 4. The GUI — web dashboard & desktop app

```bash
dbmount serve --open      # animated live dashboard at http://127.0.0.1:9723 (token-gated)
```

The page shows every pool with live capacity donuts, cache-hit/dirty and
cache-occupancy meters, a hit-rate history, and a **live flow map** — pool I/O →
RAM cache → fast tier → capacity storage — where **data blocks fly along curves**
as reads, writes, drains and duplications actually happen, with each storage's
measured latency shown in its node; updated once a second while pools are
mounted. With `placement.autoLandingZone` enabled, the **landing zone follows
the measured-fastest drive automatically** (hysteresis + cooldown prevent
flapping; a slow or busy drive gets demoted live). From the same page you can run the
**entire lifecycle**: create a pool (pick local folders with a built-in **folder
browser**, or add remote members whose **credentials are collected by a
scheme-aware dialog** — user/password for FTP·WebDAV, password *or* private key
for SFTP, access/secret keys for S3, account key for Azure, token for
Dropbox·OneDrive, service-account JSON for Google — stored under a reference the
manifest never inlines), mount / unmount, add or remove members, set
**duplication** (copies to keep — pool-wide or per folder/file glob; copies land
on independent physical disks by default, SAFE-PHYS, with an opt-in to also keep
copies on the same disk for bit-rot protection when no independent disk is free),
edit **all pool settings** via a validated JSON editor, remove- and replace-media,
**browse the pool** with one column per storage showing exactly where every
file/folder lives (✅ primary · 🔁 shadow · ❌ absent), run a **problem scan**
with a full report (under-duplicated files, integrity issues, per-device SMART)
plus one-click fix, restore, **forget** a pool (drop it from this machine's list
while leaving its data and on-disk markers intact, so it can be re-imported or
recovered later), and delete (keep data) or purge (wipe data, guarded by a
name-confirmation). If a chosen member folder is still claimed by another —
possibly forgotten or otherwise invisible — pool, the UI offers to **restore**
that pool from the manifest copy left in the folder, or **take the folder over**
for the new one, instead of failing outright. The **desktop app**
(`DriveBender.App`) is the same page in a native window — it launches the daemon
for you, so web and desktop are identical.

### 5. Health & media maintenance

```bash
dbmount pool health MyPool               # metadata scan: SMART, missing copies, inconsistent files — never changes anything
dbmount pool health MyPool --deep        # + re-checksum every file to find silent bit-rot (reads all data, can take long)
dbmount pool health MyPool --fix         # repair bit-rot, re-sync stale copies, resolve conflicts, restore duplication
dbmount pool restore MyPool              # bring every file back to its duplication level

dbmount pool remove-media MyPool --member "E:\"                 # scatter its data, then drop it
dbmount pool replace-media MyPool --old "D:\" --new "K:\newdisk"  # migrate to a replacement disk
```

A mounted pool also **heals itself**: losing a member degrades redundancy, not
availability — reads fail over to surviving copies and writes keep flowing (ack
on what is reachable; opt out with `resilience.acceptDegradedWrites: false`).
Deletes and renames a missing member sleeps through are tombstoned and replayed
on its return, stale content re-syncs to the newest write, and missing copies
are recreated in the background until the pool is back at full duplication — no
manual repair needed.

Run `dbmount --help` (or `dbmount <verb> --help`) for the full option list.

## 🖼️ Screenshots

The GUI is a dependency-free web dashboard served by `dbmount serve` (and hosted
verbatim in the desktop app) — theme-aware, so it follows your OS light/dark
preference:

| Live dashboard (dark) | Live dashboard (light) |
|---|---|
| ![Dashboard, dark theme](docs/screenshots/dashboard.png) | ![Dashboard, light theme](docs/screenshots/dashboard-light.png) |

| Create a pool | Pool settings |
|---|---|
| ![Create pool dialog](docs/screenshots/create-pool.png) | ![Pool settings dialog](docs/screenshots/settings.png) |

- **Dashboard** — one card per pool: a capacity donut, the RAM→fast→capacity
  tier topology with animated flow lines, per-member usage bars and health, and
  the full lifecycle actions (mount, browse, health/fix, duplication, settings…).
- **Create a pool** — stack up members (local folder/drive, UNC, or any remote
  URI) with a per-member role and a folder browser; no JSON required.
- **Settings** — every pool knob as a labelled control (mount location, write
  policy, cache, background maintenance…), with an advanced JSON escape hatch.

## ⚙️ How it works

Seven diagrams for the parts that are hard to guess from the file list. They describe the engine as
it is, not as it is planned — where something is not built yet, it says so.

### The pool: one namespace over many storages

A pool is a set of **members** (a drive, a folder, a UNC share, a cloud endpoint) presented as one
filesystem. A file is never split: it lives whole on one member, and a *duplicated* file lives whole
on several. That is what makes a member readable on its own — pull a disk out, plug it into any
machine, and the files on it are just files.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontSize":"15px","lineColor":"#90A4AE","textColor":"#37474F","clusterBkg":"#FAFAFA","clusterBorder":"#E0E0E0","edgeLabelBackground":"#FFFFFF"}}}%%
flowchart TD
    App["Application<br/>reads and writes a drive letter or a mountpoint"] --> Driver

    subgraph Driver["Filesystem driver"]
        WinFsp["WinFsp / Dokan<br/>Windows"]
        Fuse["FUSE<br/>Linux"]
    end

    Driver --> Engine

    subgraph Engine["Pool engine"]
        Placement["Placement<br/>which member takes this file"]
        Journal["Journal<br/>intent to completion, replayed on mount"]
        Cache["Caches<br/>blocks, metadata, owed writes"]
    end

    Engine --> M0 & M1 & M2

    subgraph Members["Members — each holds whole files"]
        M0["Landing zone<br/>fast tier"]
        M1["Capacity disk"]
        M2["Cloud / UNC"]
    end

    classDef primary fill:#E3F2FD,stroke:#42A5F5,stroke-width:1.5px,color:#0D47A1,rx:8,ry:8
    classDef surface fill:#FFFFFF,stroke:#CFD8DC,stroke-width:1.5px,color:#37474F,rx:8,ry:8
    classDef store fill:#E8F5E9,stroke:#66BB6A,stroke-width:1.5px,color:#1B5E20,rx:8,ry:8
    classDef warn fill:#FFF8E1,stroke:#FFB300,stroke-width:1.5px,color:#E65100,rx:8,ry:8
    classDef danger fill:#FFEBEE,stroke:#EF5350,stroke-width:1.5px,color:#B71C1C,rx:8,ry:8
    classDef accent fill:#EDE7F6,stroke:#7E57C2,stroke-width:1.5px,color:#4527A0,rx:8,ry:8
    classDef decision fill:#ECEFF1,stroke:#90A4AE,stroke-width:1.5px,color:#37474F
    class App primary
    class WinFsp,Fuse,Placement,Journal surface
    class Cache accent
    class M0,M1,M2 store
```

### Reading: cache, then the readiest copy

A read is answered from the block cache where it can be. Where it cannot, the engine resolves which
members hold the file and asks the one that is **readiest** — fewest requests outstanding, then
lowest measured latency. A duplicated file can serve different offsets from different disks at once.

If a copy fails or answers short, the next copy is tried before the read is allowed to fail, and a
member that just failed is parked at the back of the order for a while — one dying disk should not
be tried first for every block of a large read.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontSize":"15px","lineColor":"#90A4AE","textColor":"#37474F","clusterBkg":"#FAFAFA","clusterBorder":"#E0E0E0","edgeLabelBackground":"#FFFFFF"}}}%%
flowchart TD
    Read["read(path, offset, length)"] --> Block{"block in<br/>page cache?"}
    Block -- yes --> Serve["serve from RAM"]
    Block -- no --> Resolve["resolve copies<br/>from the metadata cache"]
    Resolve --> Order["order by readiness<br/>outstanding I/O, then latency"]
    Order --> Fetch["read the block<br/>from the chosen copy"]
    Fetch --> Ok{"complete?"}
    Ok -- yes --> Fill["fill cache, serve,<br/>maybe read ahead"]
    Ok -- "no — error, or shorter<br/>than the file's length" --> Next{"another<br/>copy?"}
    Next -- yes --> Fetch
    Next -- no --> Requery["re-resolve once<br/>the copies may have moved"]
    Requery --> Fail["error — never<br/>silently short"]

    classDef primary fill:#E3F2FD,stroke:#42A5F5,stroke-width:1.5px,color:#0D47A1,rx:8,ry:8
    classDef surface fill:#FFFFFF,stroke:#CFD8DC,stroke-width:1.5px,color:#37474F,rx:8,ry:8
    classDef store fill:#E8F5E9,stroke:#66BB6A,stroke-width:1.5px,color:#1B5E20,rx:8,ry:8
    classDef warn fill:#FFF8E1,stroke:#FFB300,stroke-width:1.5px,color:#E65100,rx:8,ry:8
    classDef danger fill:#FFEBEE,stroke:#EF5350,stroke-width:1.5px,color:#B71C1C,rx:8,ry:8
    classDef accent fill:#EDE7F6,stroke:#7E57C2,stroke-width:1.5px,color:#4527A0,rx:8,ry:8
    classDef decision fill:#ECEFF1,stroke:#90A4AE,stroke-width:1.5px,color:#37474F
    class Read primary
    class Serve,Fill store
    class Resolve,Order,Fetch,Requery surface
    class Block,Ok,Next decision
    class Fail danger
```

### Writing: staged, acknowledged, then converged

A new file is written under a hidden temp name and becomes visible only at the final atomic rename,
so a crash mid-write leaves no half-written file. A write is acknowledged once it is durable on the
required number of copies; any copies still owed are recorded and completed in the background.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontSize":"15px","actorBkg":"#E3F2FD","actorBorder":"#42A5F5","actorTextColor":"#0D47A1","actorLineColor":"#B0BEC5","signalColor":"#546E7A","signalTextColor":"#37474F","noteBkgColor":"#FFF8E1","noteBorderColor":"#FFB300","noteTextColor":"#E65100","sequenceNumberColor":"#FFFFFF"}}}%%
sequenceDiagram
    autonumber
    participant App
    participant Engine as Pool engine
    participant A as Member A
    participant B as Member B

    App->>Engine: create + write
    Engine->>A: write to hidden temp
    Engine->>B: write to hidden temp
    Note over Engine: acknowledged once the ack quorum is durable
    Engine-->>App: ok
    App->>Engine: close
    Engine->>A: atomic rename temp to final
    Engine->>B: atomic rename temp to final
    Note over Engine,B: copies still owed are held in the write buffer and completed in the background — the journal intent closes last
```

### Memory: three caches with different jobs

One configurable pool of RAM, split between reading and writing. The split can be automatic, fixed,
or two separate budgets.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontSize":"15px","lineColor":"#90A4AE","textColor":"#37474F","clusterBkg":"#FAFAFA","clusterBorder":"#E0E0E0","edgeLabelBackground":"#FFFFFF"}}}%%
flowchart LR
    subgraph RAM["cache.size — one budget, split between reading and writing"]
        direction TB
        Pages["Page cache<br/>file blocks, 1 MiB by default<br/>serves repeat reads and read-ahead"]
        Meta["Metadata cache<br/>stat, directory listings,<br/>which members hold a file"]
        Buf["Write buffer<br/>bytes acknowledged but not yet on every copy<br/>the only record of what a copy still owes"]
    end

    Buf --> Members["Members"]

    classDef primary fill:#E3F2FD,stroke:#42A5F5,stroke-width:1.5px,color:#0D47A1,rx:8,ry:8
    classDef surface fill:#FFFFFF,stroke:#CFD8DC,stroke-width:1.5px,color:#37474F,rx:8,ry:8
    classDef store fill:#E8F5E9,stroke:#66BB6A,stroke-width:1.5px,color:#1B5E20,rx:8,ry:8
    classDef warn fill:#FFF8E1,stroke:#FFB300,stroke-width:1.5px,color:#E65100,rx:8,ry:8
    classDef danger fill:#FFEBEE,stroke:#EF5350,stroke-width:1.5px,color:#B71C1C,rx:8,ry:8
    classDef accent fill:#EDE7F6,stroke:#7E57C2,stroke-width:1.5px,color:#4527A0,rx:8,ry:8
    classDef decision fill:#ECEFF1,stroke:#90A4AE,stroke-width:1.5px,color:#37474F
    class Pages,Meta accent
    class Buf warn
    class Members store
```

### Tiering: land fast, drain later

With a **landing zone** member, new files go to the fast tier and a background drainer moves settled
files down to capacity. Placement declines the fast tier once it is past its low watermark, so a full
SSD stops absorbing rather than wedging the pool.

The drain copies the file down, re-validates that nothing changed underneath it, and only then frees
the fast tier — so an interruption leaves the file on one tier or the other, never on neither.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontSize":"15px","lineColor":"#90A4AE","textColor":"#37474F","clusterBkg":"#FAFAFA","clusterBorder":"#E0E0E0","edgeLabelBackground":"#FFFFFF"}}}%%
flowchart LR
    W["new file"] --> P{"fast tier below<br/>its low watermark?"}
    P -- yes --> L["Landing zone<br/>SSD"]
    P -- "no — it is full" --> C["Capacity<br/>HDD or cloud"]
    L -- "settled: closed,<br/>clean, unchanged" --> D["Drainer<br/>copy down, re-validate,<br/>then free the fast tier"]
    D --> C

    classDef primary fill:#E3F2FD,stroke:#42A5F5,stroke-width:1.5px,color:#0D47A1,rx:8,ry:8
    classDef surface fill:#FFFFFF,stroke:#CFD8DC,stroke-width:1.5px,color:#37474F,rx:8,ry:8
    classDef store fill:#E8F5E9,stroke:#66BB6A,stroke-width:1.5px,color:#1B5E20,rx:8,ry:8
    classDef warn fill:#FFF8E1,stroke:#FFB300,stroke-width:1.5px,color:#E65100,rx:8,ry:8
    classDef danger fill:#FFEBEE,stroke:#EF5350,stroke-width:1.5px,color:#B71C1C,rx:8,ry:8
    classDef accent fill:#EDE7F6,stroke:#7E57C2,stroke-width:1.5px,color:#4527A0,rx:8,ry:8
    classDef decision fill:#ECEFF1,stroke:#90A4AE,stroke-width:1.5px,color:#37474F
    class W primary
    class P decision
    class L,C store
    class D surface
```

### The recycle bin

Off by default — `trash.enabled` turns it on. With it, a delete does not destroy the file: one copy
is **renamed** into a hidden per-member trash tree with a sidecar recording where it came from, and
the other copies are dropped (`dropDuplicatesInTrash`, on by default — the bin keeps the file
recoverable, not redundant). Nothing is copied, so deleting stays as cheap as it was. A
retention/size policy purges oldest-first in the background.

Restoring from a pool that is mounted goes through the process that owns it, and re-establishes the
file's duplication level on the way back — a recovered file that is one bad sector from being lost again is
only half recovered.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontSize":"15px","lineColor":"#90A4AE","textColor":"#37474F","clusterBkg":"#FAFAFA","clusterBorder":"#E0E0E0","edgeLabelBackground":"#FFFFFF"}}}%%
flowchart TD
    Del["delete file"] --> On{"trash<br/>enabled?"}
    On -- no --> Gone["every copy removed<br/>permanently"]
    On -- yes --> Move["rename ONE copy into the hidden<br/>trash tree with a .trashinfo sidecar<br/>and drop the others"]
    Move --> Bin["Recycle bin<br/>listed by CLI, API and the dashboard"]
    Bin --> R["restore"] --> Back["back at its original path,<br/>duplication re-established"]
    Bin --> Pol["retention and size policy"] --> Purge["oldest purged for good"]
    Bin -. "member removed from the pool" .-> Scatter["the bin moves to<br/>the remaining members"]

    classDef primary fill:#E3F2FD,stroke:#42A5F5,stroke-width:1.5px,color:#0D47A1,rx:8,ry:8
    classDef surface fill:#FFFFFF,stroke:#CFD8DC,stroke-width:1.5px,color:#37474F,rx:8,ry:8
    classDef store fill:#E8F5E9,stroke:#66BB6A,stroke-width:1.5px,color:#1B5E20,rx:8,ry:8
    classDef warn fill:#FFF8E1,stroke:#FFB300,stroke-width:1.5px,color:#E65100,rx:8,ry:8
    classDef danger fill:#FFEBEE,stroke:#EF5350,stroke-width:1.5px,color:#B71C1C,rx:8,ry:8
    classDef accent fill:#EDE7F6,stroke:#7E57C2,stroke-width:1.5px,color:#4527A0,rx:8,ry:8
    classDef decision fill:#ECEFF1,stroke:#90A4AE,stroke-width:1.5px,color:#37474F
    class Del primary
    class On decision
    class Gone,Purge danger
    class Move,Pol,R surface
    class Bin accent
    class Back,Scatter store
```

### Snapshots

> **Engine only today.** Taking, listing, deleting and preserving work and are tested; there is no
> CLI verb, no API and no screen yet, and **no reserve** — see [docs/Snapshots.md](docs/Snapshots.md)
> for the design and the delivery slices.

Taking a snapshot **copies nothing**: it records the pool's namespace at that instant. From then on
the pool may not destroy content those paths still point at, so the first thing that would destroy
one preserves it first.

What that costs depends entirely on *how* the file is written, and the difference is the whole
design:

| What happens to a pinned file | What it costs |
| --- | --- |
| Replaced outright (truncate + write — `File.WriteAllBytes`, editors, backup tools) | **Nothing.** The old file is *renamed* into the store; the new content is staged and renamed into place. Two renames. |
| Modified in place (write at an offset, keep the rest) | **A copy.** The untouched bytes must survive in the live file *and* in the version. |
| Renamed, or renamed over | A copy — the live file has to stay put for the rename to have something to move. |
| Deleted | Nothing. The file is renamed into the store. |
| Never touched again | **Nothing at all** — the snapshot's copy *is* the live file. Most of a backup pool. |

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontSize":"15px","lineColor":"#90A4AE","textColor":"#37474F","clusterBkg":"#FAFAFA","clusterBorder":"#E0E0E0","edgeLabelBackground":"#FFFFFF"}}}%%
flowchart TD
    T["take snapshot"] --> Idx["record the namespace<br/>no data copied"]
    Idx --> Pin["every path pinned"]

    Pin --> Ev{"something would destroy<br/>a pinned version"}
    Ev -- "whole file replaced,<br/>or deleted" --> Ren["RENAME the live file<br/>into the store — no bytes moved"]
    Ev -- "modified in place,<br/>or renamed" --> Cop["COPY into the store<br/>untouched bytes must survive twice"]
    Ev -- "never touched" --> None["nothing stored<br/>the live file IS the version"]

    Ren --> Un["path unpinned — later writes cost<br/>nothing until the next snapshot"]
    Cop --> Un

    Un --> Rd["read as of the snapshot:<br/>earliest version after it was taken,<br/>otherwise the live file"]
    Un --> Dl["delete snapshot: drop its pin;<br/>the version goes when the last pin does"]

    classDef primary fill:#E3F2FD,stroke:#42A5F5,stroke-width:1.5px,color:#0D47A1,rx:8,ry:8
    classDef surface fill:#FFFFFF,stroke:#CFD8DC,stroke-width:1.5px,color:#37474F,rx:8,ry:8
    classDef store fill:#E8F5E9,stroke:#66BB6A,stroke-width:1.5px,color:#1B5E20,rx:8,ry:8
    classDef warn fill:#FFF8E1,stroke:#FFB300,stroke-width:1.5px,color:#E65100,rx:8,ry:8
    classDef danger fill:#FFEBEE,stroke:#EF5350,stroke-width:1.5px,color:#B71C1C,rx:8,ry:8
    classDef accent fill:#EDE7F6,stroke:#7E57C2,stroke-width:1.5px,color:#4527A0,rx:8,ry:8
    classDef decision fill:#ECEFF1,stroke:#90A4AE,stroke-width:1.5px,color:#37474F
    class T primary
    class Ev decision
    class Ren,None store
    class Cop warn
    class Idx,Pin,Un,Rd,Dl surface
```

> **A snapshot is not a backup.** It shares the pool's disks and its failure domains: it protects
> against deleting or overwriting a file, not against losing the storage underneath it.

## 📁 Project structure

The solution is organized into the following projects:

### 📚 DriveBender.Core
Core library containing all Drive Bender functionality:
- **Pool Management**: Create, delete, and manage storage pools
- **Drive Operations**: Add, remove, and replace drives with intelligent data migration
- **Duplication Manager**: Control file duplication with multiple shadow copy support
- **Integrity Checker**: Comprehensive file integrity verification and repair
- **Semantic Data Types**: Type-safe wrappers for paths, sizes, and configuration

### 💻 DriveBender.Console  
Command-line interface covering the full pool lifecycle:
- Pool creation and deletion
- Drive management operations
- Duplication control
- Integrity checking and repair
- Dry-run mode for safe operations

### 🖥️ DriveBender.App *(net10.0)*
The desktop GUI is a thin cross-platform **WebView shell** (Photino → WebView2 on
Windows, WebKitGTK on Linux) that launches `dbmount serve` and hosts the same web
UI the daemon serves — so the desktop app and a browser are the *same* animated,
live dashboard. (The legacy WPF `DriveBender.UI` has been retired.)

### 🧪 DriveBender.Tests
Comprehensive test suite categorized as:
- **Unit Tests**: Core functionality testing (HappyPath, EdgeCase, Exception)
- **Integration Tests**: Cross-component testing
- **End-to-End Tests**: Complete workflow validation
- **Performance Tests**: Scalability and speed verification
- **Regression Tests**: Backwards compatibility and bug prevention

### ⚙️ DriveBender.Vfs *(net10.0)*
Platform-agnostic VFS/I/O engine towards live pool mounting
(see `docs/PRD-PoolMount-Driver.md`):
- **Pool manifests**: pools defined over arbitrary member paths — drive roots,
  subfolders, UNC shares — in a portable, versioned JSON manifest stored
  redundantly (machine registry + a mirror on every member)
- **Member self-identification**: members carry a `.drivebenderutility/member.json`
  marker and are resolved by marker content, so drive-letter changes are harmless
- **Native pool adapter**: the classic drive scan synthesizes a *virtual manifest*,
  so native pools flow through the identical code path and can be *adopted*
  into editable manifests in place
- **Physical failure domains**: placement identity is the underlying volume
  (subfolder members on one disk are one domain), with de-duplicated free-space
  accounting and `reserveBytes`
- **Byte-range I/O abstraction** (`IVolumeIO`) with local backend and atomic
  temp-and-rename publication, plus whole-file remote backends
- **Hierarchical configuration** (built-in defaults → global → pool → folder
  globs) with strict validation: duplication-aware ack floors, journal and fsync
  safety switches that cannot be disabled, and a never-over-committed RAM ceiling
  for cache instances

### 🌐 DriveBender.Backends *(net10.0)*
Members can be far more than local drives — any of these joins a pool as a
whole-file capacity tier. The stores live in the standalone, provider-neutral
**[`Hawkynt.CloudStorage`](Hawkynt.CloudStorage)** library (published to NuGet);
`DriveBender.Backends` adapts them to the engine's `IVolumeIO` via
`CloudStoreAdapter`:

| Scheme | Backend | Client |
|---|---|---|
| `file` / `unc` | local drive, subfolder, UNC share | .NET |
| `ftp` / `ftps` | FTP / FTPS | FluentFTP |
| `sftp` / `ssh` | SFTP (password or private key) | SSH.NET |
| `webdav` / `webdavs` / `dav` / `davs` | WebDAV | WebDav.Client |
| `s3` | Amazon S3 & S3-compatible (MinIO…) | AWSSDK.S3 |
| `azblob` / `azfile` | Azure Blob / Azure Files | Azure.Storage.* |
| `dropbox` | Dropbox | Dropbox.Api |
| `onedrive` | Microsoft OneDrive | Microsoft.Graph |
| `gdrive` / `gcs` | Google Drive / Cloud Storage | Google.Apis.* |
| `box` | Box | REST |
| `yandex` | Yandex Disk | REST |
| `hidrive` | Strato HiDrive | REST |

OAuth providers (`gdrive`, `onedrive`, `dropbox`, `box`, `yandex`, `hidrive`)
use **bring-your-own client-id OAuth2** — a loopback-redirect + PKCE browser
login via `dbmount credential-login`, with automatic access-token refresh so a
long-lived pool never relies on a static token. (Box, Yandex Disk and HiDrive
are wired up but not yet integration-tested against live accounts.)

Remote members are capability-honest: no atomic rename and no durable flush, so
the engine journals around the gaps and never counts a remote copy toward
`minCopiesBeforeAck` (`SAFE-REMOTE`). Secrets live only in the OS credential
store (Windows Credential Manager, or an owner-only file fallback) and are
referenced from the manifest by `cred-ref:<name>` handle (`SEC-CRED`).

### 🚀 DriveBender.Mount *(net10.0 / net10.0-windows, `dbmount`)*
CLI/daemon entry point for manifest pools:
- `dbmount pool create|import|export|list|add-member|remove-member|adopt|repair-manifest`
  — members may be drive roots, subfolders, UNC shares, or any remote URI above
- `dbmount credential set <name>` / `remove` — store remote secrets (read from a
  hidden prompt or stdin, never shell history)
- `dbmount mount --manifest <file|poolId|name> [--target X:\|/mnt/pool] [--read-only]`
  — mounts the pool as a live filesystem: **WinFsp** or **Dokan** on Windows,
  **FUSE** on Linux. Crash recovery replays the journal before serving, health
  warnings surface up front, background workers (owed-copy sync, landing-zone
  drain, trash maintenance) pump while mounted, and unmount flushes all dirty
  state
- Non-destructive by contract: pre-existing folder content is never absorbed
  without `--force`, and folders owned by another pool are always refused

### 🪟 DriveBender.Mount.Windows *(net10.0-windows)*
The Windows platform adapters — thin callback translations over the engine's
`IPoolFileSystem` contract (no pool logic in the adapter, `NFR-PORT`):
- **WinFsp** (`winfsp.net`) — preferred, richer semantics
- **Dokan** (`dokan-dotnet`, LGPL) — automatic fallback so no specific driver
  install is forced; `dbmount` picks whichever is present

### 🐧 DriveBender.Mount.Linux *(net10.0)*
The **FUSE** platform adapter (`LTRData.FuseDotNet`, LGPL) plus a
`mount.drivebender` fstab helper and a `drivebender-pool@.service` systemd
template, so a manifest mounts natively at boot:

```fstab
/etc/drivebenderutility/pools/media.json  /mnt/media  fuse.drivebender  defaults,_netdev  0 0
```

### 🖥️ DriveBender.App *(net10.0)* & the web UI
The management daemon `dbmount serve` hosts a **dependency-free animated web
dashboard** (127.0.0.1, per-session token): live capacity donuts, cache-hit and
dirty meters, a hit-rate sparkline, a RAM→fast→capacity **tier topology with
animated flow lines**, per-member health tiles, and health/fix/restore actions,
fed at 1 Hz over Server-Sent Events. `DriveBender.App` is the cross-platform
desktop shell that hosts that same page in a native WebView.

### 🧪 DriveBender.Vfs.Tests *(net10.0)*
Headless engine suite: the whole VFS engine runs against in-memory fakes
(`FakeVolumeIO`, `FakeHostEnvironment`) including fault injection — power loss,
no-space, torn writes, offline members — so every safety invariant is testable
without a real pool.

## 🔧 Configuration

Tuning lives in the manifest's `defaults` block (per pool) or a machine-wide
`config.json` under the config root (`%ProgramData%\DriveBenderUtility` on
Windows, `/etc/drivebenderutility` or `~/.config/drivebenderutility` on Linux).
Values resolve built-in defaults → global file → pool → per-folder glob. A few
common knobs:

```jsonc
{
  "duplication": 2,                       // total copies kept of each file
  "write": {
    "policy": "write-back",               // write-through | write-back | deferred | performance
    "minCopiesBeforeAck": 2               // durable copies required before a write is acknowledged
  },
  "resilience": {
    "onMemberLoss": "retain-metadata",    // keep showing metadata when a drive is pulled,
                                          //   or "discard-inaccessible" to drop unreachable entries
    "acceptDegradedWrites": true          // keep writing on the reachable copies when a member is
                                          //   missing (owed copies heal on return); false = refuse
  },
  "trash": { "enabled": true, "retention": "7d" },   // recoverable deletes
  "folders": {
    "Documents/**": { "write": { "policy": "write-through" }, "duplication": 3 }
  }
}
```

Config is validated on load and can be reloaded live without unmounting. See
`docs/PRD-PoolMount-Driver.md` §8 for the complete schema.

> **Legacy native pools:** the older `DriveBender.Console` still manages
> Division-M-format native pools directly (`create`, `add-drive`,
> `enable-duplication`, `check`, `repair`, `rebalance` — see
> `DriveBender.Console/CommandLineOptions.cs`). New work should prefer manifest
> pools via `dbmount`, which can also *adopt* a discovered native pool
> (`dbmount pool adopt <name>`) into an editable manifest without moving data.

## 📈 Performance

### Scalability Metrics
- **Small Pools** (< 1TB): Operations complete in seconds
- **Medium Pools** (1-10TB): Operations complete in minutes
- **Large Pools** (10TB+): Operations may take hours but provide progress feedback

### Memory Usage
- **Core Library**: < 50MB baseline memory usage
- **GUI Application**: < 200MB including UI framework
- **Batch Operations**: Memory usage scales linearly with file count

### Optimization Features
- **Lazy Loading**: Files and metadata loaded on-demand
- **Parallel Processing**: Multi-threaded integrity checking
- **Caching**: Intelligent caching of file metadata
- **Progress Reporting**: Real-time progress updates for long operations

## 🛡️ Security

### Permissions
- **Administrator Rights**: Required for drive operations
- **File System Access**: Full control over pool directories
- **Registry Access**: Reading Drive Bender configuration

### Data Protection
- **Backup Creation**: Automatic backups before destructive operations
- **Dry-Run Mode**: Preview changes before execution
- **Validation**: Input validation prevents path traversal attacks
- **Logging**: Comprehensive audit trail of all operations

## 🏗️ Architecture

### Core Components

```
DriveBender.Core/
├── PoolManager.cs          # Pool lifecycle management
├── DuplicationManager.cs   # Shadow copy control
├── IntegrityChecker.cs     # File integrity verification
├── DataTypes.cs           # Semantic type definitions
└── DriveBender.cs         # Original Drive Bender interface
```

### Design Patterns
- **Factory Pattern**: Pool and volume creation
- **Strategy Pattern**: Different integrity check strategies
- **Observer Pattern**: Progress reporting during operations
- **Command Pattern**: CLI command structure
- **Repository Pattern**: Data access abstraction

### Error Handling Strategy
- **Graceful Degradation**: Continue processing despite individual failures
- **Comprehensive Logging**: All operations logged with context
- **User Feedback**: Clear error messages with suggested actions
- **Recovery Options**: Multiple repair strategies for different issue types

## 🛠️ Building

```bash
dotnet build -c Release
dotnet test
```

Mounting needs WinFsp or Dokan on Windows and FUSE on Linux; the test suite does not.

## 🤝 Contributing

We welcome contributions! Here's how to get started:

### Development Setup
```bash
# Clone the repository
git clone https://github.com/Hawkynt/DriveBenderUtility.git
cd DriveBenderUtility

# Build the solution
dotnet build DriveBender.sln -c Release

# Run tests to ensure everything works
dotnet test DriveBender.Vfs.Tests/DriveBender.Vfs.Tests.csproj
dotnet test DriveBender.Tests/DriveBender.Tests.csproj
```

### Contribution Guidelines
1. **Fork** the repository
2. **Create** a feature branch (`git checkout -b feature/amazing-feature`)
3. **Add tests** for new functionality
4. **Ensure** all tests pass
5. **Update** documentation as needed
6. **Commit** changes (`git commit -m 'Add amazing feature'`)
7. **Push** to branch (`git push origin feature/amazing-feature`)
8. **Create** a Pull Request

### Code Standards
- Follow existing C# conventions
- Add XML documentation for public APIs
- Include unit tests for new features
- Update README for significant changes
- Use semantic data types for type safety

### Testing Requirements
- **Unit Tests**: Required for all new core functionality
- **Integration Tests**: Required for cross-component features  
- **Performance Tests**: Required for operations handling large datasets
- **Regression Tests**: Add tests for bug fixes

## 🆘 Getting Help
- **Documentation**: This README and inline code documentation
- **Issues**: [GitHub Issues](https://github.com/Hawkynt/DriveBenderUtility/issues) for bugs and feature requests
- **Discussions**: [GitHub Discussions](https://github.com/Hawkynt/DriveBenderUtility/discussions) for questions and help

### Reporting Issues
When reporting issues, please include:
- Operating system and version
- .NET Framework version
- Drive Bender version
- Steps to reproduce the issue
- Expected vs actual behavior
- Log files (if available)

### Feature Requests
We welcome feature requests! Please:
- Check existing issues first
- Describe the use case
- Provide implementation suggestions if possible
- Consider contributing the feature yourself

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see [LICENSE](LICENSE).
