# DriveBenderUtility

[![Build](https://github.com/Hawkynt/DriveBenderUtility/actions/workflows/Build.yml/badge.svg)](https://github.com/Hawkynt/DriveBenderUtility/actions/workflows/Build.yml)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/DriveBenderUtility?branch=master)](https://github.com/Hawkynt/DriveBenderUtility/commits/master)
![License](https://img.shields.io/github/license/Hawkynt/DriveBenderUtility)

## Overview

**DriveBenderUtility** is a C# tool designed to help users manage Drive Bender storage pools. This tool provides a user-friendly interface for managing drive pools.

## Project Structure

The project is organized into the following components:

- **Classes**: Contains the main logic for interacting with Drive Bender pools, handling the underlying operations required to manage pools effectively.

## Features

- View detailed drive information.
- Automatic drive pool management options for maintenance.

## Getting Started

### Prerequisites

To build and run the DriveBenderUtility, you'll need:

- [.NET Framework 4.7](https://dotnet.microsoft.com/download/dotnet-framework/net47) installed on your machine.
- Administrator permissions on the machine for managing drives.

### Building the Project

To build the project, navigate to the project directory and run:

```bash
msbuild DriveBenderUtility.csproj
```

The output binary will be located in the `bin\$(Configuration)` directory based on the configuration you select (e.g., `Debug`, `Release`).

### Running the Tool

Once built, run the executable directly from the output directory:

```bash
.\bin\Release\DriveBenderUtility.exe
```

### Example Usage

```csharp
public class Program {
  private static void Main(string[] args) {
    var mountPoints = DriveBender.DetectedMountPoints;
    var mountPoint = mountPoints.FirstOrDefault();
    if (mountPoint == null)
      return; /* no pool found */

    DriveBender.Logger = Console.WriteLine;

    Console.WriteLine();

    DriveBender.Logger($"Pool:{mountPoint.Name}({mountPoint.Description}) [{string.Join(", ", mountPoint.Volumes.Select(d => d.Name))}]");
      
    mountPoint.FixMissingDuplicationOnAllFolders();
    mountPoint.FixDuplicatePrimaries();
    mountPoint.FixDuplicateShadowCopies();
    mountPoint.FixMissingPrimaries();
    mountPoint.FixMissingShadowCopies();

    mountPoint.Rebalance();
      
    Console.WriteLine("READY.");
    Console.ReadKey(false);
  }
}
```

## Contributing

We welcome contributions from the community! Please follow these steps to contribute:

1. Fork the repository.
2. Create a new branch for your feature or bug fix.
3. Submit a pull request with a detailed description of the changes.

## License

This project is licensed under the LGPL3 License. See the [LICENSE](LICENSE) file for more details.

## Contact

For any questions, issues, or feature requests, please open an issue on the [GitHub repository](https://github.com/Hawkynt/DriveBenderUtility/issues).
