using System.Windows;

namespace DriveBender.UI {
  public partial class App : Application {
    protected override void OnStartup(StartupEventArgs e) {
      base.OnStartup(e);
      
      // Set up logging
      DivisonM.DriveBender.Logger = message => {
        System.Diagnostics.Debug.WriteLine(message);
      };
    }
  }
}