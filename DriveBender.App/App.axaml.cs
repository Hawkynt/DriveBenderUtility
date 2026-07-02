using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DivisonM.App.ViewModels;
using DivisonM.Backends;
using DivisonM.Vfs;

namespace DivisonM.App;

public sealed class App : Application {

  public override void Initialize() => AvaloniaXamlLoader.Load(this);

  public override void OnFrameworkInitializationCompleted() {
    if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
      var host = new RealHostEnvironment();
      var store = new ManifestStore(host);
      var credentials = new CredentialStore(host);
      var registry = BackendRegistry.CreateDefault(host);
      var remoteResolver = new BackendMemberResolver(registry, credentials);
      var provider = new PoolProvider(host, store, [new JsonManifestSource(store), new NativeScanSource(host)], remoteResolver: remoteResolver);
      var lifecycle = new PoolLifecycle(host, store);

      desktop.MainWindow = new MainWindow {
        DataContext = new MainWindowViewModel(provider, lifecycle, credentials),
      };
    }

    base.OnFrameworkInitializationCompleted();
  }

}
