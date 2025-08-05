using System;
using System.Windows;
using DivisonM;
using IMountPoint = DivisonM.DriveBender.IMountPoint;

namespace DriveBender.UI {
  public partial class AddDriveDialog : Window {
    private readonly string _poolName;
    
    public AddDriveDialog(string poolName) {
      _poolName = poolName;
      // Placeholder - implement actual dialog
      var result = MessageBox.Show($"Add drive to pool '{poolName}' - Feature coming soon!", "Add Drive", MessageBoxButton.OK, MessageBoxImage.Information);
      DialogResult = false;
    }
  }
  
  public partial class RemoveDriveDialog : Window {
    private readonly string _poolName;
    private readonly string _driveName;
    
    public RemoveDriveDialog(string poolName, string driveName) {
      _poolName = poolName;
      _driveName = driveName;
      // Placeholder - implement actual dialog
      var result = MessageBox.Show($"Remove drive '{driveName}' from pool '{poolName}' - Feature coming soon!", "Remove Drive", MessageBoxButton.OK, MessageBoxImage.Information);
      DialogResult = false;
    }
  }
  
  public partial class DuplicationManagerDialog : Window {
    private readonly DriveBender.IMountPoint _mountPoint;
    
    public DuplicationManagerDialog(DriveBender.IMountPoint mountPoint) {
      _mountPoint = mountPoint;
      // Placeholder - implement actual dialog
      var result = MessageBox.Show($"Manage duplication for pool '{mountPoint.Name}' - Feature coming soon!", "Duplication Manager", MessageBoxButton.OK, MessageBoxImage.Information);
    }
  }
}