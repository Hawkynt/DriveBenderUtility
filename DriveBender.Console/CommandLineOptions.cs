using System.Collections.Generic;
using CommandLine;

namespace DriveBender.Console {
  
  [Verb("list", HelpText = "List all available Drive Bender pools")]
  public class ListPoolsOptions {
    [Option('v', "verbose", HelpText = "Show detailed information")]
    public bool Verbose { get; set; }
  }
  
  [Verb("create", HelpText = "Create a new Drive Bender pool")]
  public class CreatePoolOptions {
    [Option('n', "name", Required = true, HelpText = "Name of the new pool")]
    public string Name { get; set; }
    
    [Option('m', "mount", HelpText = "Mount point path for the pool")]
    public string MountPoint { get; set; }
    
    [Option('d', "drives", Required = true, HelpText = "Comma-separated list of drive paths to include in the pool")]
    public IEnumerable<string> Drives { get; set; }
  }
  
  [Verb("delete", HelpText = "Delete an existing Drive Bender pool")]
  public class DeletePoolOptions {
    [Option('n', "name", Required = true, HelpText = "Name of the pool to delete")]
    public string Name { get; set; }
    
    [Option("remove-data", HelpText = "Also remove all data from the pool")]
    public bool RemoveData { get; set; }
  }
  
  [Verb("add-drive", HelpText = "Add a drive to an existing pool")]
  public class AddDriveOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
    
    [Option('d', "drive", Required = true, HelpText = "Path of the drive to add")]
    public string DrivePath { get; set; }
  }
  
  [Verb("remove-drive", HelpText = "Remove a drive from a pool")]
  public class RemoveDriveOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
    
    [Option('d', "drive", Required = true, HelpText = "Path of the drive to remove")]
    public string DrivePath { get; set; }
    
    [Option("move-data", Default = true, HelpText = "Move data from the drive before removing it")]
    public bool MoveData { get; set; }
  }
  
  [Verb("enable-duplication", HelpText = "Enable duplication on a folder")]
  public class EnableDuplicationOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
    
    [Option('f', "folder", Required = true, HelpText = "Path of the folder")]
    public string FolderPath { get; set; }
    
    [Option('l', "level", Default = 1, HelpText = "Duplication level (number of copies)")]
    public int Level { get; set; }
  }
  
  [Verb("disable-duplication", HelpText = "Disable duplication on a folder")]
  public class DisableDuplicationOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
    
    [Option('f', "folder", Required = true, HelpText = "Path of the folder")]
    public string FolderPath { get; set; }
  }
  
  [Verb("set-duplication", HelpText = "Set duplication level for a folder")]
  public class SetDuplicationLevelOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
    
    [Option('f', "folder", Required = true, HelpText = "Path of the folder")]
    public string FolderPath { get; set; }
    
    [Option('l', "level", Required = true, HelpText = "Duplication level (0 to disable, 1+ for number of copies)")]
    public int Level { get; set; }
  }
  
  [Verb("check", HelpText = "Check pool integrity")]
  public class CheckIntegrityOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
    
    [Option("deep", HelpText = "Perform deep scan with file hash comparison")]
    public bool DeepScan { get; set; }
  }
  
  [Verb("repair", HelpText = "Repair pool integrity issues")]
  public class RepairOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
    
    [Option("all", HelpText = "Run all standard repair operations")]
    public bool All { get; set; }
    
    [Option("deep", HelpText = "Perform deep scan during repair")]
    public bool DeepScan { get; set; }
    
    [Option("dry-run", Default = true, HelpText = "Perform dry run without making changes")]
    public bool DryRun { get; set; }
    
    [Option("no-backup", HelpText = "Skip creating backups before repairs")]
    public bool NoBackup { get; set; }
  }
  
  [Verb("rebalance", HelpText = "Rebalance data across drives in a pool")]
  public class RebalanceOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
  }
  
  [Verb("info", HelpText = "Show detailed information about a pool")]
  public class InfoOptions {
    [Option('p', "pool", Required = true, HelpText = "Name of the pool")]
    public string PoolName { get; set; }
    
    [Option('f', "folder", HelpText = "Show duplication info for specific folder")]
    public string FolderPath { get; set; }
  }
}