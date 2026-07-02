using DivisonM.App.Localization;
using DivisonM.Backends;
using DivisonM.Vfs;

namespace DivisonM.App.ViewModels;

/// <summary>Root ViewModel wiring the tabs to the shared engine services.</summary>
public sealed class MainWindowViewModel : ObservableObject {

  public MainWindowViewModel(IPoolProvider provider, PoolLifecycle lifecycle, CredentialStore credentials) {
    this.Dashboard = new(provider);
    this.CreatePool = new(lifecycle);
    this.Tuning = new();
    this.Credentials = new(credentials);
    this.Activity = new();
  }

  public Localizer L => Localizer.Instance;

  public string Title => this.L.Get("app.title");

  public DashboardViewModel Dashboard { get; }
  public CreatePoolViewModel CreatePool { get; }
  public TuningViewModel Tuning { get; }
  public CredentialsViewModel Credentials { get; }
  public ActivityViewModel Activity { get; }

}
