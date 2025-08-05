using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using DivisonM;

namespace DriveBender.UI {
  public partial class CreatePoolDialog : Window {
    
    private readonly List<string> _selectedDrives = new List<string>();
    
    public CreatePoolDialog() {
      InitializeComponent();
      DrivesListBox.SelectionChanged += (s, e) => {
        RemoveDriveButton.IsEnabled = DrivesListBox.SelectedItem != null;
      };
    }
    
    private void AddDriveButton_Click(object sender, RoutedEventArgs e) {
      var dialog = new System.Windows.Forms.FolderBrowserDialog {
        Description = "Select drive or folder to add to pool",
        ShowNewFolderButton = true
      };
      
      if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
        var drivePath = dialog.SelectedPath;
        if (!_selectedDrives.Contains(drivePath)) {
          _selectedDrives.Add(drivePath);
          DrivesListBox.Items.Add(drivePath);
        }
      }
    }
    
    private void RemoveDriveButton_Click(object sender, RoutedEventArgs e) {
      if (DrivesListBox.SelectedItem is string selectedDrive) {
        _selectedDrives.Remove(selectedDrive);
        DrivesListBox.Items.Remove(selectedDrive);
      }
    }
    
    private void OKButton_Click(object sender, RoutedEventArgs e) {
      var poolName = PoolNameTextBox.Text?.Trim();
      if (string.IsNullOrEmpty(poolName)) {
        MessageBox.Show("Please enter a pool name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
      }
      
      if (_selectedDrives.Count == 0) {
        MessageBox.Show("Please add at least one drive to the pool.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
      }
      
      try {
        var mountPoint = MountPointTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(mountPoint)) {
          mountPoint = poolName;
        }
        
        var success = PoolManager.CreatePool(poolName, mountPoint, _selectedDrives);
        
        if (success) {
          MessageBox.Show($"Pool '{poolName}' created successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
          DialogResult = true;
        } else {
          MessageBox.Show("Failed to create pool. Check the log for details.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
      } catch (Exception ex) {
        MessageBox.Show($"Error creating pool: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
      }
    }
    
    private void CancelButton_Click(object sender, RoutedEventArgs e) {
      DialogResult = false;
    }
  }
}