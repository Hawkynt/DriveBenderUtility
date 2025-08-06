using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DivisonM {
  
  /// <summary>
  /// Represents a pool name with validation
  /// </summary>
  public struct PoolName : IEquatable<PoolName> {
    private readonly string _value;
    
    public PoolName(string value) {
      if (string.IsNullOrWhiteSpace(value))
        throw new ArgumentException("Pool name cannot be null or empty", nameof(value));
      
      if (value.Length > 255)
        throw new ArgumentException("Pool name cannot exceed 255 characters", nameof(value));
      
      if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        throw new ArgumentException("Pool name contains invalid characters", nameof(value));
      
      _value = value.Trim();
    }
    
    public string Value => _value ?? string.Empty;
    
    public static implicit operator string(PoolName poolName) => poolName.Value;
    public static implicit operator PoolName(string value) => new PoolName(value);
    
    public bool Equals(PoolName other) => string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object obj) => obj is PoolName other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_value ?? string.Empty);
    public override string ToString() => Value;
    
    public static bool operator ==(PoolName left, PoolName right) => left.Equals(right);
    public static bool operator !=(PoolName left, PoolName right) => !left.Equals(right);
  }
  
  /// <summary>
  /// Represents a drive path with validation
  /// </summary>
  public struct DrivePath : IEquatable<DrivePath> {
    private readonly string _value;
    
    public DrivePath(string value) {
      if (string.IsNullOrWhiteSpace(value))
        throw new ArgumentException("Drive path cannot be null or empty", nameof(value));
      
      var fullPath = Path.GetFullPath(value);
      if (!Directory.Exists(fullPath))
        throw new DirectoryNotFoundException($"Drive path does not exist: {fullPath}");
      
      _value = fullPath;
    }
    
    public string Value => _value ?? string.Empty;
    public DirectoryInfo DirectoryInfo => new DirectoryInfo(Value);
    public bool Exists => Directory.Exists(Value);
    public DriveInfo DriveInfo => new DriveInfo(Path.GetPathRoot(Value));
    
    public static implicit operator string(DrivePath drivePath) => drivePath.Value;
    public static implicit operator DirectoryInfo(DrivePath drivePath) => drivePath.DirectoryInfo;
    
    public static DrivePath FromString(string value) => new DrivePath(value);
    
    public bool Equals(DrivePath other) => string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object obj) => obj is DrivePath other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_value ?? string.Empty);
    public override string ToString() => Value;
    
    public static bool operator ==(DrivePath left, DrivePath right) => left.Equals(right);
    public static bool operator !=(DrivePath left, DrivePath right) => !left.Equals(right);
  }
  
  /// <summary>
  /// Represents a folder path within a pool
  /// </summary>
  public struct FolderPath : IEquatable<FolderPath> {
    private readonly string _value;
    
    public FolderPath(string value) {
      if (string.IsNullOrWhiteSpace(value))
        throw new ArgumentException("Folder path cannot be null or empty", nameof(value));
      
      // Normalize path separators and remove leading/trailing separators
      var normalized = value.Replace('\\', '/').Trim('/');
      
      // For folder paths, we need to be more lenient with validation
      // Allow colons for drive letters (C:) and basic path characters
      var invalidChars = new[] { '<', '>', '"', '|', '?', '*', '\0' };
      foreach (var c in invalidChars) {
        if (normalized.Contains(c))
          throw new ArgumentException("Folder path contains invalid characters", nameof(value));
      }
      
      _value = normalized;
    }
    
    public string Value => _value ?? string.Empty;
    public string[] Segments => Value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
    public FolderPath Parent => Value.Contains('/') ? new FolderPath(Path.GetDirectoryName(Value.Replace('/', '\\'))) : new FolderPath("");
    public string Name => Path.GetFileName(Value.Replace('/', '\\'));
    
    public static implicit operator string(FolderPath folderPath) => folderPath.Value;
    public static implicit operator FolderPath(string value) => new FolderPath(value);
    
    public FolderPath Combine(string childPath) => new FolderPath(Value + "/" + childPath.Trim('/'));
    
    public bool Equals(FolderPath other) => string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object obj) => obj is FolderPath other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_value ?? string.Empty);
    public override string ToString() => Value;
    
    public static bool operator ==(FolderPath left, FolderPath right) => left.Equals(right);
    public static bool operator !=(FolderPath left, FolderPath right) => !left.Equals(right);
  }
  
  /// <summary>
  /// Represents a file size in bytes
  /// </summary>
  public struct ByteSize : IEquatable<ByteSize>, IComparable<ByteSize> {
    private readonly ulong _bytes;
    
    public ByteSize(ulong bytes) {
      _bytes = bytes;
    }
    
    public ulong Bytes => _bytes;
    public double Kilobytes => _bytes / 1024.0;
    public double Megabytes => _bytes / (1024.0 * 1024.0);
    public double Gigabytes => _bytes / (1024.0 * 1024.0 * 1024.0);
    public double Terabytes => _bytes / (1024.0 * 1024.0 * 1024.0 * 1024.0);
    
    public static ByteSize FromBytes(ulong bytes) => new ByteSize(bytes);
    public static ByteSize FromKilobytes(double kb) => new ByteSize((ulong)(kb * 1024));
    public static ByteSize FromMegabytes(double mb) => new ByteSize((ulong)(mb * 1024 * 1024));
    public static ByteSize FromGigabytes(double gb) => new ByteSize((ulong)(gb * 1024 * 1024 * 1024));
    public static ByteSize FromTerabytes(double tb) => new ByteSize((ulong)(tb * 1024 * 1024 * 1024 * 1024));
    
    public static implicit operator ulong(ByteSize size) => size.Bytes;
    public static implicit operator ByteSize(ulong bytes) => new ByteSize(bytes);
    
    public string ToHumanReadable() => DriveBender.SizeFormatter.Format(_bytes);
    
    public bool Equals(ByteSize other) => _bytes == other._bytes;
    public override bool Equals(object obj) => obj is ByteSize other && Equals(other);
    public override int GetHashCode() => _bytes.GetHashCode();
    public override string ToString() => ToHumanReadable();
    
    public int CompareTo(ByteSize other) => _bytes.CompareTo(other._bytes);
    
    public static bool operator ==(ByteSize left, ByteSize right) => left.Equals(right);
    public static bool operator !=(ByteSize left, ByteSize right) => !left.Equals(right);
    public static bool operator <(ByteSize left, ByteSize right) => left._bytes < right._bytes;
    public static bool operator >(ByteSize left, ByteSize right) => left._bytes > right._bytes;
    public static bool operator <=(ByteSize left, ByteSize right) => left._bytes <= right._bytes;
    public static bool operator >=(ByteSize left, ByteSize right) => left._bytes >= right._bytes;
    
    public static ByteSize operator +(ByteSize left, ByteSize right) => new ByteSize(left._bytes + right._bytes);
    public static ByteSize operator -(ByteSize left, ByteSize right) => new ByteSize(left._bytes - right._bytes);
  }
  
  /// <summary>
  /// Represents duplication level settings
  /// </summary>
  public struct DuplicationLevel : IEquatable<DuplicationLevel> {
    private readonly int _value;
    
    public DuplicationLevel(int value) {
      if (value < 0)
        throw new ArgumentException("Duplication level cannot be negative", nameof(value));
      if (value > 10) // Reasonable upper limit
        throw new ArgumentException("Duplication level cannot exceed 10", nameof(value));
      
      _value = value;
    }
    
    public int Value => _value;
    public bool IsDisabled => _value == 0;
    public bool IsSingleCopy => _value == 1;
    public bool IsMultipleCopies => _value > 1;
    
    public static DuplicationLevel Disabled => new DuplicationLevel(0);
    public static DuplicationLevel Single => new DuplicationLevel(1);
    public static DuplicationLevel Double => new DuplicationLevel(2);
    public static DuplicationLevel Triple => new DuplicationLevel(3);
    
    public static implicit operator int(DuplicationLevel level) => level.Value;
    public static implicit operator DuplicationLevel(int value) => new DuplicationLevel(value);
    
    public bool Equals(DuplicationLevel other) => _value == other._value;
    public override bool Equals(object obj) => obj is DuplicationLevel other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public override string ToString() => _value == 0 ? "Disabled" : $"{_value} copies";
    
    public static bool operator ==(DuplicationLevel left, DuplicationLevel right) => left.Equals(right);
    public static bool operator !=(DuplicationLevel left, DuplicationLevel right) => !left.Equals(right);
  }
  
  /// <summary>
  /// Represents the result of a space check operation
  /// </summary>
  public class SpaceCheckResult {
    public bool HasSufficientSpace { get; set; }
    public ByteSize RequiredSpace { get; set; }
    public ByteSize AvailableSpace { get; set; }
    public ByteSize ShortfallSpace => RequiredSpace > AvailableSpace ? RequiredSpace - AvailableSpace : ByteSize.FromBytes(0);
    public IEnumerable<(DriveBender.IVolume Volume, ByteSize AvailableSpace)> VolumeSpaceInfo { get; set; }
    public string RecommendedAction { get; set; }
  }
  
  /// <summary>
  /// Options for drive operations
  /// </summary>
  public class DriveOperationOptions {
    public bool DryRun { get; set; } = true;
    public bool CreateBackup { get; set; } = true;
    public bool PromptUser { get; set; } = true;
    public bool AutoBalance { get; set; } = true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);
  }
  
  /// <summary>
  /// Result of a drive operation
  /// </summary>
  public class DriveOperationResult {
    public bool Success { get; set; }
    public string Message { get; set; }
    public Exception Exception { get; set; }
    public TimeSpan Duration { get; set; }
    public int FilesProcessed { get; set; }
    public ByteSize DataProcessed { get; set; }
    public IEnumerable<string> Warnings { get; set; } = new List<string>();
  }
}