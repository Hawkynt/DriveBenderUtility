using System.Collections.ObjectModel;
using DivisonM.App.Localization;
using DivisonM.Vfs;

namespace DivisonM.App.ViewModels;

/// <summary>A pool row on the dashboard with health at a glance (§6.13 pool dashboard).</summary>
public sealed class PoolRowViewModel(PoolRef pool, PoolHealth health) {
  public Guid PoolId => pool.PoolId;
  public string Name => pool.Name;
  public string Source => pool.IsVirtual ? "native scan" : "manifest";
  public bool IsDegraded => health.IsDegraded;
  public string Health => Localizer.Instance.Get(health.IsDegraded ? "dashboard.degraded" : "dashboard.healthy");
  public long BytesFree => health.BytesFree;
  public long BytesTotal => health.BytesTotal;
  public int OnlineMembers => health.OnlineMembers.Count();
  public int TotalMembers => health.Members.Count;
  public IReadOnlyList<string> Warnings => health.Warnings;
}

/// <summary>
/// Pool dashboard ViewModel (§6.13): every discovered pool with health, refreshable.
/// Headless-testable against a faked provider.
/// </summary>
public sealed class DashboardViewModel : ObservableObject {

  private readonly IPoolProvider _provider;
  private PoolRowViewModel? _selectedPool;
  private string _statusMessage = "";

  public DashboardViewModel(IPoolProvider provider) {
    this._provider = provider;
    this.RefreshCommand = new(this.Refresh);
    this.Refresh();
  }

  public ObservableCollection<PoolRowViewModel> Pools { get; } = [];

  public PoolRowViewModel? SelectedPool {
    get => this._selectedPool;
    set => this.SetProperty(ref this._selectedPool, value);
  }

  public string StatusMessage {
    get => this._statusMessage;
    private set => this.SetProperty(ref this._statusMessage, value);
  }

  public RelayCommand RefreshCommand { get; }

  public void Refresh() {
    this.Pools.Clear();
    foreach (var pool in this._provider.Discover()) {
      PoolHealth health;
      try {
        health = this._provider.Inspect(pool);
      } catch (PoolFsException) {
        continue;
      }

      this.Pools.Add(new(pool, health));
    }

    this.StatusMessage = this.Pools.Count == 0
      ? Localizer.Instance.Get("dashboard.noPools")
      : $"{this.Pools.Count}";
  }

}
