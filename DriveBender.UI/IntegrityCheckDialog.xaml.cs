using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DivisonM;
using Microsoft.Win32;
using IMountPoint = DivisonM.DriveBender.IMountPoint;

namespace DriveBender.UI {
  public partial class IntegrityCheckDialog : Window {
    
    private readonly IMountPoint _mountPoint;
    public ObservableCollection<IntegrityIssueViewModel> Issues { get; set; }
    
    public IntegrityCheckDialog(IMountPoint mountPoint) {
      InitializeComponent();
      
      _mountPoint = mountPoint;
      Issues = new ObservableCollection<IntegrityIssueViewModel>();
      IssuesDataGrid.ItemsSource = Issues;
      
      Title = $"Pool Integrity Check - {_mountPoint.Name}";
      
      IssuesDataGrid.SelectionChanged += (s, e) => {
        RepairSelectedButton.IsEnabled = IssuesDataGrid.SelectedItems.Count > 0;
      };
    }
    
    private async void StartCheckButton_Click(object sender, RoutedEventArgs e) {
      try {
        StartCheckButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        ProgressTextBlock.Text = "Checking pool integrity...";
        
        Issues.Clear();
        
        var deepScan = DeepScanCheckBox.IsChecked == true;
        
        await Task.Run(() => {
          var issues = IntegrityChecker.CheckPoolIntegrity(_mountPoint, deepScan);
          
          Dispatcher.Invoke(() => {
            foreach (var issue in issues) {
              Issues.Add(new IntegrityIssueViewModel(issue));
            }
          });
        });
        
        ProgressTextBlock.Text = $"Integrity check completed. Found {Issues.Count} issue(s).";
        RepairAllButton.IsEnabled = Issues.Count > 0;
        ExportButton.IsEnabled = Issues.Count > 0;
        
      } catch (Exception ex) {
        MessageBox.Show($"Error during integrity check: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        ProgressTextBlock.Text = "Error during integrity check";
      } finally {
        StartCheckButton.IsEnabled = true;
        ProgressBar.Visibility = Visibility.Collapsed;
      }
    }
    
    private async void RepairSelectedButton_Click(object sender, RoutedEventArgs e) {
      var selectedIssues = IssuesDataGrid.SelectedItems.Cast<IntegrityIssueViewModel>().ToArray();
      if (selectedIssues.Length == 0) return;
      
      var result = MessageBox.Show(
        $"Are you sure you want to repair {selectedIssues.Length} selected issue(s)?",
        "Confirm Repair",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question);
      
      if (result == MessageBoxResult.Yes) {
        await RepairIssues(selectedIssues);
      }
    }
    
    private async void RepairAllButton_Click(object sender, RoutedEventArgs e) {
      if (Issues.Count == 0) return;
      
      var result = MessageBox.Show(
        $"Are you sure you want to repair all {Issues.Count} issue(s)?",
        "Confirm Repair All",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question);
      
      if (result == MessageBoxResult.Yes) {
        await RepairIssues(Issues.ToArray());
      }
    }
    
    private async Task RepairIssues(IntegrityIssueViewModel[] issuesToRepair) {
      try {
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Maximum = issuesToRepair.Length;
        ProgressBar.Value = 0;
        
        var repairedCount = 0;
        
        await Task.Run(() => {
          for (int i = 0; i < issuesToRepair.Length; i++) {
            var issue = issuesToRepair[i];
            
            Dispatcher.Invoke(() => {
              ProgressTextBlock.Text = $"Repairing issue {i + 1} of {issuesToRepair.Length}: {Path.GetFileName(issue.FilePath)}";
              ProgressBar.Value = i + 1;
            });
            
            try {
              if (IntegrityChecker.RepairIntegrityIssue(issue.Issue)) {
                repairedCount++;
                Dispatcher.Invoke(() => Issues.Remove(issue));
              }
            } catch (Exception ex) {
              DivisonM.DriveBender.Logger?.Invoke($"Failed to repair {issue.FilePath}: {ex.Message}");
            }
          }
        });
        
        ProgressTextBlock.Text = $"Repair completed. Successfully repaired {repairedCount} of {issuesToRepair.Length} issue(s).";
        RepairAllButton.IsEnabled = Issues.Count > 0;
        ExportButton.IsEnabled = Issues.Count > 0;
        
      } catch (Exception ex) {
        MessageBox.Show($"Error during repair: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        ProgressTextBlock.Text = "Error during repair";
      } finally {
        ProgressBar.Visibility = Visibility.Collapsed;
      }
    }
    
    private void ExportButton_Click(object sender, RoutedEventArgs e) {
      try {
        var dialog = new SaveFileDialog {
          Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
          DefaultExt = "txt",
          FileName = $"IntegrityReport_{_mountPoint.Name}_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        
        if (dialog.ShowDialog() == true) {
          var lines = new[] { $"Integrity Report for Pool: {_mountPoint.Name}", $"Generated: {DateTime.Now}", "" }
            .Concat(Issues.Select(issue => $"{issue.FilePath}\t{issue.IssueType}\t{issue.Description}\t{issue.SuggestedAction}"));
          
          System.IO.File.WriteAllLines(dialog.FileName, lines);
          MessageBox.Show("Report exported successfully.", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
      } catch (Exception ex) {
        MessageBox.Show($"Error exporting report: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
      }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e) {
      Close();
    }
  }
}