using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DivisonM;

namespace DriveBender.UI {
  public partial class MainWindow : Window {
    
    public ObservableCollection<PoolViewModel> Pools { get; set; }
    public ObservableCollection<VolumeViewModel> Volumes { get; set; }
    
    private PoolViewModel _selectedPool;
    
    public MainWindow() {
      InitializeComponent();
      
      Pools = new ObservableCollection<PoolViewModel>();
      Volumes = new ObservableCollection<VolumeViewModel>();
      
      PoolsDataGrid.ItemsSource = Pools;
      VolumesDataGrid.ItemsSource = Volumes;
      
      Loaded += MainWindow_Loaded;
    }
    
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
      await RefreshPools();
    }
    
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) {
      await RefreshPools();
    }
    
    private async Task RefreshPools() {
      try {
        StatusTextBlock.Text = "Refreshing pools...";
        
        await Task.Run(() => {
          var mountPoints = DivisonM.DriveBender.DetectedMountPoints;
          
          Dispatcher.Invoke(() => {
            Pools.Clear();
            Volumes.Clear();
            
            foreach (var pool in mountPoints) {
              Pools.Add(new PoolViewModel(pool));
            }
            
            StatusTextBlock.Text = $"Found {Pools.Count} pool(s)";
          });
        });
      } catch (Exception ex) {
        MessageBox.Show($"Error refreshing pools: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        StatusTextBlock.Text = "Error refreshing pools";
      }
    }
    
    private void PoolsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
      _selectedPool = PoolsDataGrid.SelectedItem as PoolViewModel;
      
      var hasSelection = _selectedPool != null;
      DeletePoolButton.IsEnabled = hasSelection;
      RebalanceButton.IsEnabled = hasSelection;
      CheckIntegrityButton.IsEnabled = hasSelection;
      AddDriveButton.IsEnabled = hasSelection;
      RemoveDriveButton.IsEnabled = hasSelection;
      DuplicationButton.IsEnabled = hasSelection;
      
      if (_selectedPool != null) {
        Volumes.Clear();
        foreach (var volume in _selectedPool.MountPoint.Volumes) {
          Volumes.Add(new VolumeViewModel(volume));
        }
        StatusTextBlock.Text = $"Selected pool: {_selectedPool.Name}";
      } else {
        Volumes.Clear();
        StatusTextBlock.Text = "Ready";
      }
    }
    
    private void CreatePoolButton_Click(object sender, RoutedEventArgs e) {
      var dialog = new CreatePoolDialog();
      if (dialog.ShowDialog() == true) {
        RefreshPools();
      }
    }
    
    private async void DeletePoolButton_Click(object sender, RoutedEventArgs e) {
      if (_selectedPool == null) return;
      
      var result = MessageBox.Show(
        $"Are you sure you want to delete pool '{_selectedPool.Name}'?\n\nThis will remove the pool structure but preserve the data.",
        "Confirm Delete",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning);
      
      if (result == MessageBoxResult.Yes) {
        try {
          StatusTextBlock.Text = "Deleting pool...";
          
          await Task.Run(() => {
            PoolManager.DeletePool(_selectedPool.Name, false);
          });
          
          await RefreshPools();
        } catch (Exception ex) {
          MessageBox.Show($"Error deleting pool: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
          StatusTextBlock.Text = "Error deleting pool";
        }
      }
    }
    
    private async void RebalanceButton_Click(object sender, RoutedEventArgs e) {
      if (_selectedPool == null) return;
      
      try {
        StatusTextBlock.Text = "Rebalancing pool...";
        
        await Task.Run(() => {
          _selectedPool.MountPoint.Rebalance();
        });
        
        await RefreshPools();
        StatusTextBlock.Text = "Rebalance completed";
      } catch (Exception ex) {
        MessageBox.Show($"Error rebalancing pool: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        StatusTextBlock.Text = "Error rebalancing pool";
      }
    }
    
    private async void CheckIntegrityButton_Click(object sender, RoutedEventArgs e) {
      if (_selectedPool == null) return;
      
      var dialog = new IntegrityCheckDialog(_selectedPool.MountPoint);
      dialog.Owner = this;
      dialog.Show();
    }
    
    private void AddDriveButton_Click(object sender, RoutedEventArgs e) {
      if (_selectedPool == null) return;
      
      var dialog = new AddDriveDialog(_selectedPool.Name);
      dialog.Owner = this;
      if (dialog.ShowDialog() == true) {
        RefreshPools();
      }
    }
    
    private void RemoveDriveButton_Click(object sender, RoutedEventArgs e) {
      if (_selectedPool == null) return;
      
      var selectedVolume = VolumesDataGrid.SelectedItem as VolumeViewModel;
      if (selectedVolume == null) {
        MessageBox.Show("Please select a volume to remove.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
        return;
      }
      
      var dialog = new RemoveDriveDialog(_selectedPool.Name, selectedVolume.Name);
      dialog.Owner = this;
      if (dialog.ShowDialog() == true) {
        RefreshPools();
      }
    }
    
    private void DuplicationButton_Click(object sender, RoutedEventArgs e) {
      if (_selectedPool == null) return;
      
      var dialog = new DuplicationManagerDialog(_selectedPool.MountPoint);
      dialog.Owner = this;
      dialog.ShowDialog();
    }
  }
}