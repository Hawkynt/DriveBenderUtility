using System.ComponentModel;
using System.Linq;
using DivisonM;
using IMountPoint = DivisonM.DriveBender.IMountPoint;
using IVolume = DivisonM.DriveBender.IVolume;

namespace DriveBender.UI {
  
  public class PoolViewModel : INotifyPropertyChanged {
    public IMountPoint MountPoint { get; }
    
    public PoolViewModel(IMountPoint mountPoint) {
      MountPoint = mountPoint;
    }
    
    public string Name => MountPoint.Name;
    public string Description => MountPoint.Description;
    public string Id => MountPoint.Id.ToString();
    
    public ulong TotalSize => MountPoint.BytesTotal;
    public ulong UsedSize => MountPoint.BytesUsed;
    public ulong FreeSize => MountPoint.BytesFree;
    
    public string TotalSizeFormatted => DivisonM.DriveBender.SizeFormatter.Format(TotalSize);
    public string UsedSizeFormatted => DivisonM.DriveBender.SizeFormatter.Format(UsedSize);
    public string FreeSizeFormatted => DivisonM.DriveBender.SizeFormatter.Format(FreeSize);
    
    public int VolumeCount => MountPoint.Volumes.Count();
    public string UsagePercentage => TotalSize > 0 ? $"{UsedSize * 100.0 / TotalSize:F1}%" : "0%";
    
    public event PropertyChangedEventHandler PropertyChanged;
    
    protected virtual void OnPropertyChanged(string propertyName) {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
  }
  
  public class VolumeViewModel : INotifyPropertyChanged {
    public IVolume Volume { get; }
    
    public VolumeViewModel(IVolume volume) {
      Volume = volume;
    }
    
    public string Name => Volume.Name;
    public string Label => Volume.Label;
    public string Id => Volume.Id.ToString();
    
    public ulong TotalSize => Volume.BytesTotal;
    public ulong UsedSize => Volume.BytesUsed;
    public ulong FreeSize => Volume.BytesFree;
    
    public string TotalSizeFormatted => DivisonM.DriveBender.SizeFormatter.Format(TotalSize);
    public string UsedSizeFormatted => DivisonM.DriveBender.SizeFormatter.Format(UsedSize);
    public string FreeSizeFormatted => DivisonM.DriveBender.SizeFormatter.Format(FreeSize);
    
    public string UsagePercentage => TotalSize > 0 ? $"{UsedSize * 100.0 / TotalSize:F1}%" : "0%";
    
    public event PropertyChangedEventHandler PropertyChanged;
    
    protected virtual void OnPropertyChanged(string propertyName) {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
  }
  
  public class IntegrityIssueViewModel : INotifyPropertyChanged {
    public IntegrityChecker.IntegrityIssue Issue { get; }
    
    public IntegrityIssueViewModel(IntegrityChecker.IntegrityIssue issue) {
      Issue = issue;
    }
    
    public string FilePath => Issue.FilePath;
    public string IssueType => Issue.IssueType.ToString();
    public string Description => Issue.Description;
    public string SuggestedAction => Issue.SuggestedAction;
    public int LocationCount => Issue.FileLocations?.Count() ?? 0;
    
    public event PropertyChangedEventHandler PropertyChanged;
    
    protected virtual void OnPropertyChanged(string propertyName) {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
  }
}