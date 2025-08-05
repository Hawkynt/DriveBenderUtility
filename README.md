# 🗂️ DriveBenderUtility

![License](https://img.shields.io/github/license/Hawkynt/DriveBenderUtility)
![Language](https://img.shields.io/github/languages/top/Hawkynt/DriveBenderUtility?color=purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/DriveBenderUtility?branch=master)![Activity](https://img.shields.io/github/commit-activity/y/Hawkynt/DriveBenderUtility?branch=master)](https://github.com/Hawkynt/DriveBenderUtility/commits/master)
[![Tests](https://github.com/Hawkynt/DriveBenderUtility/actions/workflows/tests.yml/badge.svg)](https://github.com/Hawkynt/DriveBenderUtility/actions/workflows/tests.yml)
[![Build](https://github.com/Hawkynt/DriveBenderUtility/actions/workflows/Build.yml/badge.svg)](https://github.com/Hawkynt/DriveBenderUtility/actions/workflows/Build.yml)

> The #1 Spot for dealing with [DriveBender](https://en.wikipedia.org/wiki/Non-standard_RAID_levels#Drive_Extender) pools outside [DriveBender](https://www.division-m.com/drivebender/).

## 📖 Overview

**DriveBenderUtility** is a comprehensive C# solution for managing Drive Bender storage pools with advanced features including pool management, drive operations, duplication control, and file integrity checking. The solution provides both command-line and WPF GUI interfaces for complete pool lifecycle management.

## 🏗️ Project Structure

The solution is organized into four main projects:

### 📚 DriveBender.Core
Core library containing all Drive Bender functionality:
- **Pool Management**: Create, delete, and manage storage pools
- **Drive Operations**: Add, remove, and replace drives with intelligent data migration
- **Duplication Manager**: Control file duplication with multiple shadow copy support
- **Integrity Checker**: Comprehensive file integrity verification and repair
- **Semantic Data Types**: Type-safe wrappers for paths, sizes, and configuration

### 💻 DriveBender.Console  
Command-line interface with 12+ commands:
- Pool creation and deletion
- Drive management operations
- Duplication control
- Integrity checking and repair
- Dry-run mode for safe operations

### 🖥️ DriveBender.UI
WPF-based graphical user interface featuring:
- Pool overview and management
- Visual integrity checking
- Drive space monitoring
- Interactive repair workflows
- Pool creation wizards

### 🧪 DriveBender.Tests
Comprehensive test suite with 100+ tests categorized as:
- **Unit Tests**: Core functionality testing (HappyPath, EdgeCase, Exception)
- **Integration Tests**: Cross-component testing
- **End-to-End Tests**: Complete workflow validation
- **Performance Tests**: Scalability and speed verification
- **Regression Tests**: Backwards compatibility and bug prevention

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

## 🚀 Getting Started

### Prerequisites

To build and run the DriveBenderUtility, you'll need:

- [.NET Framework 4.7](https://dotnet.microsoft.com/download/dotnet-framework/net47) or higher
- Administrator permissions for managing drives and pools
- Drive Bender software installed on your system
- Visual Studio 2019+ or MSBuild tools

### 🔨 Building the Project

#### Build All Projects
```bash
msbuild DriveBenderUtility.sln /p:Configuration=Release
```

#### Build Individual Projects
```bash
# Core library
msbuild DriveBender.Core/DriveBender.Core.csproj

# Console application
msbuild DriveBender.Console/DriveBender.Console.csproj

# WPF UI application  
msbuild DriveBender.UI/DriveBender.UI.csproj

# Test suite
msbuild DriveBender.Tests/DriveBender.Tests.csproj
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

## 📄 License

This project is licensed under the LGPL3 License. See the [LICENSE](LICENSE) file for more details.

## 📞 Support & Contact

### Getting Help
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
