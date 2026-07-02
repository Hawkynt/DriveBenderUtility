using DivisonM.App.Localization;
using DivisonM.Backends;

namespace DivisonM.App.ViewModels;

/// <summary>
/// Credential picker ViewModel (§6.13): stores remote-member secrets in the OS keychain
/// by reference (SEC-CRED). Secrets never leave the store — the manifest keeps only the
/// cred-ref handle.
/// </summary>
public sealed class CredentialsViewModel : ObservableObject {

  private readonly CredentialStore _store;
  private string _name = "";
  private string _user = "";
  private string _secret = "";
  private string _statusMessage = "";

  public CredentialsViewModel(CredentialStore store) {
    this._store = store;
    this.StoreCommand = new(this.Store, () => this.Name.Trim().Length > 0 && this.Secret.Length > 0);
    this.RemoveCommand = new(this.Remove, () => this.Name.Trim().Length > 0);
  }

  public string Name {
    get => this._name;
    set { if (this.SetProperty(ref this._name, value)) { this.StoreCommand.RaiseCanExecuteChanged(); this.RemoveCommand.RaiseCanExecuteChanged(); } }
  }

  public string User {
    get => this._user;
    set => this.SetProperty(ref this._user, value);
  }

  public string Secret {
    get => this._secret;
    set { if (this.SetProperty(ref this._secret, value)) this.StoreCommand.RaiseCanExecuteChanged(); }
  }

  public string StatusMessage {
    get => this._statusMessage;
    private set => this.SetProperty(ref this._statusMessage, value);
  }

  public RelayCommand StoreCommand { get; }
  public RelayCommand RemoveCommand { get; }

  public void Store() {
    this._store.Store(this.Name.Trim(), this.User.Trim(), this.Secret);
    this.Secret = ""; // never keep the plaintext around after it is stored
    this.StatusMessage = Localizer.Instance.Get("credentials.stored");
  }

  public void Remove() {
    this._store.Remove(this.Name.Trim());
    this.StatusMessage = Localizer.Instance.Get("credentials.removed");
  }

}
