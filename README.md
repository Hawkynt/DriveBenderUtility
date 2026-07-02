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

**DriveBenderUtility** is a comprehensive C# solution for managing Drive Bender storage pools with advanced features including pool management, drive operations, duplication control, and file integrity checking. The solution provides both command-line and WPF GUI interfaces for complete pool lifecycle management.

## 🏗️ Project Structure

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
whole-file capacity tier, over the **official first-party SDKs** (no wrapper
libraries):

| Scheme | Backend | SDK |
|---|---|---|
| `file` / `unc` | local drive, subfolder, UNC share | .NET |
| `ftp` / `ftps` | FTP / FTPS | FluentFTP |
| `sftp` | SFTP (password or private key) | SSH.NET |
| `webdav` / `webdavs` | WebDAV | WebDav.Client |
| `s3` | Amazon S3 & S3-compatible (MinIO…) | AWSSDK.S3 |
| `azblob` / `azfile` | Azure Blob / Azure Files | Azure.Storage.* |
| `dropbox` | Dropbox | Dropbox.Api |
| `onedrive` | Microsoft OneDrive | Microsoft.Graph |
| `gdrive` / `gcs` | Google Drive / Cloud Storage | Google.Apis.* |

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

## ✨ Features

### 🔧 Pool Management
- ✅ Create new storage pools with multiple drives
- ✅ Delete existing pools with data preservation options
- ✅ Add drives to existing pools with automatic balancing
- ✅ Remove drives with intelligent data migration
- ✅ Replace drives with seamless data transfer
- ✅ Space checking with user warnings

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

## 📦 Getting Started

### Prerequisites

To build and run the DriveBenderUtility, you'll need:

- [.NET SDK 10](https://dotnet.microsoft.com/download) (builds every project; the
  legacy projects still *target* .NET Framework 4.7)
- Administrator permissions for managing drives and pools
- Drive Bender software installed on your system (for native pools)

### 🔨 Building the Project

#### Build All Projects
```bash
msbuild DriveBenderUtility.sln /p:Configuration=Release
```

#### Build Individual Projects
```bash
# everything (Core multi-targets net47/netstandard2.0; engine, backends, mount, app are net10)
dotnet build DriveBender.sln -c Release

# the mount CLI/daemon and the desktop shell
dotnet build DriveBender.Mount/DriveBender.Mount.csproj -c Release
dotnet build DriveBender.App/DriveBender.App.csproj -c Release
```

### 🧪 Running Tests

Run the comprehensive test suite:
```bash
# Run all tests
nunit3-console DriveBender.Tests/bin/Release/DriveBender.Tests.dll

# Run specific categories
nunit3-console DriveBender.Tests/bin/Release/DriveBender.Tests.dll --where "cat==Unit && cat==HappyPath"
nunit3-console DriveBender.Tests/bin/Release/DriveBender.Tests.dll --where "cat==Integration"
nunit3-console DriveBender.Tests/bin/Release/DriveBender.Tests.dll --where "cat==Performance"
```

### 💻 Using the Console Interface

```bash
# Display help
DriveBender.Console.exe --help

# Create a new pool
DriveBender.Console.exe create-pool --name "MyPool" --mount "C:\MyPool" --drives "D:\" "E:\"

# Check pool integrity
DriveBender.Console.exe check-integrity --pool "MyPool" --deep-scan

# Enable duplication on a folder
DriveBender.Console.exe enable-duplication --pool "MyPool" --folder "Documents/Important" --level 3

# Remove a drive from pool
DriveBender.Console.exe remove-drive --pool "MyPool" --drive "D:\" --move-data

# Repair integrity issues
DriveBender.Console.exe repair --pool "MyPool" --create-backup --dry-run=false
```

### 🖥️ Using the WPF Interface

Launch the GUI application:
```bash
DriveBender.UI.exe
```

Features:
- **Pool Overview**: Visual representation of all pools and their health
- **Integrity Dashboard**: Real-time integrity status with repair options
- **Drive Management**: Add/remove drives with space validation
- **Duplication Control**: Configure folder-level duplication settings

### 📚 API Usage Examples

#### Basic Pool Operations
```csharp
using DivisonM;

// Create a new pool
var poolName = new PoolName("MyDataPool");
var mountPoint = @"C:\MyDataPool";
var drives = new[] { @"D:\", @"E:\" };

bool success = PoolManager.CreatePool(poolName, mountPoint, drives);

// Add duplication to critical folders
var importantFolder = new FolderPath("Documents/Critical");
DuplicationManager.EnableDuplicationOnFolder(mountPoint, importantFolder, DuplicationLevel.Triple);
```

#### Integrity Checking and Repair
```csharp
// Check pool integrity
var issues = IntegrityChecker.CheckPoolIntegrity(mountPoint, deepScan: true, dryRun: true);

// Repair issues with backup
foreach (var issue in issues.Take(10)) {
    bool repaired = IntegrityChecker.RepairIntegrityIssue(issue, dryRun: false, createBackup: true);
    Console.WriteLine($"Issue {issue.Type}: {(repaired ? "Repaired" : "Failed")}");
}
```

#### Advanced Drive Management
```csharp
// Check space before removing drive
var spaceCheck = PoolManager.CheckSpaceForDriveRemoval(poolName, drivePath);
if (!spaceCheck.HasSufficientSpace) {
    Console.WriteLine($"Insufficient space: {spaceCheck.ShortfallSpace.ToHumanReadable()} needed");
    Console.WriteLine($"Recommended action: {spaceCheck.RecommendedAction}");
}

// Remove drive with options
var options = new DriveOperationOptions {
    DryRun = false,
    CreateBackup = true,
    PromptUser = true,
    AutoBalance = true
};

var result = PoolManager.RemoveDriveFromPool(poolName, drivePath, options);
```

#### Using Semantic Data Types
```csharp
// Type-safe operations
var poolName = new PoolName("ProductionPool");
var drivePath = new DrivePath(@"C:\Data");
var folderPath = new FolderPath("Users/Documents");
var fileSize = ByteSize.FromGigabytes(2.5);
var duplicationLevel = new DuplicationLevel(3);

// Automatic validation
Console.WriteLine($"Pool: {poolName}");
Console.WriteLine($"Drive exists: {drivePath.Exists}");
Console.WriteLine($"Folder segments: {string.Join("/", folderPath.Segments)}");
Console.WriteLine($"Size: {fileSize.ToHumanReadable()}");
Console.WriteLine($"Duplication: {duplicationLevel}");
```

## 🔧 Advanced Configuration

### Command Line Options

#### Global Options
- `--dry-run`: Preview operations without making changes (default: true)
- `--verbose`: Enable detailed logging
- `--no-backup`: Skip backup creation during repairs
- `--timeout <minutes>`: Set operation timeout (default: 30)

#### Pool Management Commands
```bash
# List all pools
DriveBender.Console.exe list-pools

# Show pool details  
DriveBender.Console.exe pool-info --name "MyPool"

# Delete pool (with confirmation)
DriveBender.Console.exe delete-pool --name "MyPool" --preserve-data
```

#### Drive Operations
```bash
# Add drive to existing pool
DriveBender.Console.exe add-drive --pool "MyPool" --drive "F:\" 

# Replace drive (remove old, add new)
DriveBender.Console.exe replace-drive --pool "MyPool" --old-drive "D:\" --new-drive "G:\"

# Balance pool data
DriveBender.Console.exe balance --pool "MyPool"
```

### Configuration Files

#### Pool Configuration (JSON)
```json
{
  "pools": [
    {
      "name": "MediaPool",
      "mountPoint": "C:\\Media",
      "drives": ["D:\\", "E:\\", "F:\\"],
      "duplicationSettings": {
        "Videos": { "level": 2, "enabled": true },
        "Photos": { "level": 3, "enabled": true },
        "Documents": { "level": 2, "enabled": true }
      }
    }
  ],
  "globalSettings": {
    "defaultDuplicationLevel": 2,
    "autoRepair": true,
    "createBackups": true,
    "deepScanInterval": "weekly"
  }
}
```

## 📊 Architecture & Design

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

## 🤝 Contributing

We welcome contributions! Here's how to get started:

### Development Setup
```bash
# Clone the repository
git clone https://github.com/Hawkynt/DriveBenderUtility.git
cd DriveBenderUtility

# Build the solution
msbuild DriveBenderUtility.sln /p:Configuration=Debug

# Run tests to ensure everything works
nunit3-console DriveBender.Tests/bin/Debug/DriveBender.Tests.dll
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

## 📈 Performance Considerations

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

## 🛡️ Security Considerations

### Permissions
- **Administrator Rights**: Required for drive operations
- **File System Access**: Full control over pool directories
- **Registry Access**: Reading Drive Bender configuration

### Data Protection
- **Backup Creation**: Automatic backups before destructive operations
- **Dry-Run Mode**: Preview changes before execution
- **Validation**: Input validation prevents path traversal attacks
- **Logging**: Comprehensive audit trail of all operations

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
