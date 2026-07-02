using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace DriveBender.UI {
  /// <summary>
  /// Builds a manifest pool from members of any supported kind — local folder, UNC share,
  /// FTP/SFTP, WebDAV and the cloud providers — and creates it through the `dbmount` CLI,
  /// which owns the manifest model. The WPF app targets net47 and cannot reference the
  /// net10 engine directly, so it drives the tested CLI instead.
  /// </summary>
  public partial class CreatePoolDialog : Window {

    public sealed class MemberRow {
      public string Kind { get; set; }
      public string Location { get; set; }
      public string Role { get; set; }
      public string Scheme { get; set; }
    }

    private sealed class MemberKind {
      public string Display { get; set; }
      public string Scheme { get; set; }        // null = local folder (browsable)
      public string UriHint { get; set; }
      public override string ToString() => this.Display;
    }

    private static readonly MemberKind[] _KINDS = {
      new MemberKind { Display = "Local folder / drive", Scheme = null, UriHint = @"C:\pools\dir or A:\" },
      new MemberKind { Display = "UNC share", Scheme = "unc", UriHint = @"\\server\share\pool" },
      new MemberKind { Display = "FTP", Scheme = "ftp", UriHint = "ftp://user@host/path" },
      new MemberKind { Display = "FTPS", Scheme = "ftps", UriHint = "ftps://user@host/path" },
      new MemberKind { Display = "SFTP", Scheme = "sftp", UriHint = "sftp://user@host/path" },
      new MemberKind { Display = "WebDAV", Scheme = "webdav", UriHint = "webdav://host/dav/path" },
      new MemberKind { Display = "WebDAV (HTTPS)", Scheme = "webdavs", UriHint = "webdavs://host/dav/path" },
      new MemberKind { Display = "Amazon S3", Scheme = "s3", UriHint = "s3://bucket/prefix" },
      new MemberKind { Display = "Azure Blob", Scheme = "azblob", UriHint = "azblob://container/prefix" },
      new MemberKind { Display = "Azure File", Scheme = "azfile", UriHint = "azfile://share/prefix" },
      new MemberKind { Display = "Dropbox", Scheme = "dropbox", UriHint = "dropbox://app/prefix" },
      new MemberKind { Display = "OneDrive", Scheme = "onedrive", UriHint = "onedrive://driveId/prefix" },
      new MemberKind { Display = "Google Drive", Scheme = "gdrive", UriHint = "gdrive://root/prefix" },
      new MemberKind { Display = "Google Cloud Storage", Scheme = "gcs", UriHint = "gcs://bucket/prefix" },
    };

    private readonly ObservableCollection<MemberRow> _members = new ObservableCollection<MemberRow>();

    public CreatePoolDialog() {
      InitializeComponent();

      KindComboBox.ItemsSource = _KINDS;
      KindComboBox.SelectedIndex = 0;
      RoleComboBox.ItemsSource = new[] { "capacity", "landing", "readonly" };
      RoleComboBox.SelectedIndex = 0;
      MembersListView.ItemsSource = _members;
    }

    private MemberKind SelectedKind => KindComboBox.SelectedItem as MemberKind ?? _KINDS[0];

    private void KindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      if (BrowseButton == null)
        return; // fires during initial binding before the visual tree is ready

      var kind = this.SelectedKind;
      BrowseButton.IsEnabled = kind.Scheme == null;
      MemberPathTextBox.ToolTip = "Example: " + kind.UriHint;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e) {
      var dialog = new System.Windows.Forms.FolderBrowserDialog {
        Description = "Select a drive or folder to add to the pool",
        ShowNewFolderButton = true
      };

      if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        MemberPathTextBox.Text = dialog.SelectedPath;
    }

    private void AddMemberButton_Click(object sender, RoutedEventArgs e) {
      var kind = this.SelectedKind;
      var location = MemberPathTextBox.Text?.Trim();
      if (string.IsNullOrEmpty(location)) {
        MessageBox.Show("Enter a location for the member.", "Add Member", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
      }

      if (_members.Any(m => string.Equals(m.Location, location, StringComparison.OrdinalIgnoreCase))) {
        MessageBox.Show("That member is already in the list.", "Add Member", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
      }

      _members.Add(new MemberRow {
        Kind = kind.Display,
        Location = location,
        Role = RoleComboBox.SelectedItem as string ?? "capacity",
        Scheme = kind.Scheme
      });
      MemberPathTextBox.Clear();
    }

    private void RemoveMemberButton_Click(object sender, RoutedEventArgs e) {
      if (MembersListView.SelectedItem is MemberRow row)
        _members.Remove(row);
    }

    private void OKButton_Click(object sender, RoutedEventArgs e) {
      var poolName = PoolNameTextBox.Text?.Trim();
      if (string.IsNullOrEmpty(poolName)) {
        MessageBox.Show("Please enter a pool name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
      }

      if (_members.Count == 0) {
        MessageBox.Show("Please add at least one member to the pool.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
      }

      var dbmount = LocateDbmount();
      if (dbmount == null) {
        MessageBox.Show(
          "The 'dbmount' command-line tool was not found.\n\n" +
          "It creates and mounts manifest pools (with remote/cloud members). Build DriveBender.Mount " +
          "or install the tool, then place it next to this app.",
          "dbmount not found", MessageBoxButton.OK, MessageBoxImage.Error);
        return;
      }

      try {
        var arguments = BuildCreateArguments(poolName);
        var result = RunDbmount(dbmount, arguments);
        if (result.ExitCode == 0) {
          MessageBox.Show($"Pool '{poolName}' created successfully.\n\n{result.Output}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
          DialogResult = true;
        } else {
          MessageBox.Show($"Failed to create pool.\n\n{result.Output}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
      } catch (Exception ex) {
        MessageBox.Show($"Error creating pool: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
      }
    }

    private string BuildCreateArguments(string poolName) {
      var builder = new StringBuilder();
      builder.Append("pool create --name ").Append(Quote(poolName));

      foreach (var member in _members) {
        // landing-role members go through --landing so they form the fast tier
        if (string.Equals(member.Role, "landing", StringComparison.OrdinalIgnoreCase))
          builder.Append(" --landing ").Append(Quote(member.Location));
        else
          builder.Append(" --member ").Append(Quote(member.Location));
      }

      var mount = MountPointTextBox.Text?.Trim();
      if (!string.IsNullOrEmpty(mount))
        builder.Append(" --mount ").Append(Quote(mount));

      return builder.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private struct RunResult {
      public int ExitCode;
      public string Output;
    }

    private static RunResult RunDbmount(string dbmount, string arguments) {
      var startInfo = new ProcessStartInfo {
        FileName = dbmount,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using (var process = Process.Start(startInfo)) {
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new RunResult {
          ExitCode = process.ExitCode,
          Output = (output + error).Trim()
        };
      }
    }

    /// <summary>Finds dbmount(.exe) next to this app, then in nearby solution build outputs (dev convenience).</summary>
    private static string LocateDbmount() {
      var names = new[] { "dbmount.exe", "dbmount" };
      var candidates = new List<string>();

      var appDir = AppDomain.CurrentDomain.BaseDirectory;
      foreach (var name in names)
        candidates.Add(Path.Combine(appDir, name));

      // walk up to the solution root and probe DriveBender.Mount build outputs
      var dir = new DirectoryInfo(appDir);
      for (var i = 0; i < 6 && dir != null; ++i, dir = dir.Parent) {
        var mountBin = Path.Combine(dir.FullName, "DriveBender.Mount", "bin");
        if (Directory.Exists(mountBin))
          foreach (var name in names)
            candidates.AddRange(SafeEnumerate(mountBin, name));
      }

      foreach (var candidate in candidates)
        if (File.Exists(candidate))
          return candidate;

      return null;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern) {
      try {
        return Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
      } catch {
        return Array.Empty<string>();
      }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
      DialogResult = false;
    }
  }
}
