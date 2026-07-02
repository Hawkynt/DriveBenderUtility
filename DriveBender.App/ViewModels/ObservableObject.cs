using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DivisonM.App.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base — hand-rolled to avoid an extra MVVM dependency.</summary>
public abstract class ObservableObject : INotifyPropertyChanged {

  public event PropertyChangedEventHandler? PropertyChanged;

  protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return false;

    field = value;
    this.OnPropertyChanged(propertyName);
    return true;
  }

  protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    => this.PropertyChanged?.Invoke(this, new(propertyName));

}

/// <summary>Simple relay command for button bindings.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand {

  public event EventHandler? CanExecuteChanged;

  public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

  public void Execute(object? parameter) => execute();

  public void RaiseCanExecuteChanged() => this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);

}
