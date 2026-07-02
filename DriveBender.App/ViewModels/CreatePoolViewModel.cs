using System.Collections.ObjectModel;
using DivisonM.App.Localization;
using DivisonM.Backends;
using DivisonM.Vfs;

namespace DivisonM.App.ViewModels;

/// <summary>One kind of member the wizard can add (§6.13 create/edit wizard).</summary>
public sealed record MemberKind(string Scheme, string DisplayKey) {
  public bool IsLocal => this.Scheme is "file";
  public string Display => Localizer.Instance.Get(this.DisplayKey);
}

/// <summary>A member the user has added to the new pool.</summary>
public sealed record NewMember(MemberKind Kind, string Location, MemberRole Role, string? Credential);

/// <summary>
/// Create/edit wizard ViewModel (FR-UI): builds a manifest from members of any supported
/// kind — local folder, UNC, FTP/SFTP, WebDAV and every cloud provider — validated with
/// the same rules the CLI uses (CFG-VALIDATE parity), then persisted through
/// <see cref="PoolLifecycle"/>. Headless-testable: no view dependency.
/// </summary>
public sealed class CreatePoolViewModel : ObservableObject {

  public static readonly IReadOnlyList<MemberKind> Kinds = [
    new("file", "kind.local"),
    new("unc", "kind.unc"),
    new("ftp", "kind.ftp"),
    new("ftps", "kind.ftps"),
    new("sftp", "kind.sftp"),
    new("webdav", "kind.webdav"),
    new("webdavs", "kind.webdavs"),
    new("s3", "kind.s3"),
    new("azblob", "kind.azblob"),
    new("azfile", "kind.azfile"),
    new("dropbox", "kind.dropbox"),
    new("onedrive", "kind.onedrive"),
    new("gdrive", "kind.gdrive"),
    new("gcs", "kind.gcs"),
  ];

  public static readonly IReadOnlyList<MemberRole> Roles = [MemberRole.Capacity, MemberRole.Landing, MemberRole.ReadOnly];

  private readonly PoolLifecycle _lifecycle;
  private string _poolName = "";
  private string _mountTarget = "";
  private MemberKind _selectedKind = Kinds[0];
  private string _memberLocation = "";
  private MemberRole _selectedRole = MemberRole.Capacity;
  private string _credentialReference = "";
  private NewMember? _selectedMember;
  private string _statusMessage = "";

  public CreatePoolViewModel(PoolLifecycle lifecycle) {
    this._lifecycle = lifecycle;
    this.AddMemberCommand = new(this.AddMember, () => this.MemberLocation.Trim().Length > 0);
    this.RemoveMemberCommand = new(this.RemoveSelectedMember, () => this.SelectedMember != null);
    this.CreateCommand = new(() => this.Create(), () => this.PoolName.Trim().Length > 0 && this.Members.Count > 0);
  }

  public ObservableCollection<NewMember> Members { get; } = [];

  public string PoolName {
    get => this._poolName;
    set { if (this.SetProperty(ref this._poolName, value)) this.CreateCommand.RaiseCanExecuteChanged(); }
  }

  public string MountTarget {
    get => this._mountTarget;
    set => this.SetProperty(ref this._mountTarget, value);
  }

  public MemberKind SelectedKind {
    get => this._selectedKind;
    set {
      if (this.SetProperty(ref this._selectedKind, value))
        this.OnPropertyChanged(nameof(this.IsLocationBrowsable));
    }
  }

  public bool IsLocationBrowsable => this.SelectedKind.IsLocal;

  public string MemberLocation {
    get => this._memberLocation;
    set { if (this.SetProperty(ref this._memberLocation, value)) this.AddMemberCommand.RaiseCanExecuteChanged(); }
  }

  public MemberRole SelectedRole {
    get => this._selectedRole;
    set => this.SetProperty(ref this._selectedRole, value);
  }

  public string CredentialReference {
    get => this._credentialReference;
    set => this.SetProperty(ref this._credentialReference, value);
  }

  public NewMember? SelectedMember {
    get => this._selectedMember;
    set { if (this.SetProperty(ref this._selectedMember, value)) this.RemoveMemberCommand.RaiseCanExecuteChanged(); }
  }

  public string StatusMessage {
    get => this._statusMessage;
    private set => this.SetProperty(ref this._statusMessage, value);
  }

  public RelayCommand AddMemberCommand { get; }
  public RelayCommand RemoveMemberCommand { get; }
  public RelayCommand CreateCommand { get; }

  public void AddMember() {
    var location = this.MemberLocation.Trim();
    if (location.Length == 0 || this.Members.Any(m => m.Location.Equals(location, StringComparison.OrdinalIgnoreCase)))
      return;

    var credential = this.CredentialReference.Trim();
    this.Members.Add(new(this.SelectedKind, location, this.SelectedRole, credential.Length == 0 ? null : credential));
    this.MemberLocation = "";
    this.CredentialReference = "";
    this.CreateCommand.RaiseCanExecuteChanged();
  }

  public void RemoveSelectedMember() {
    if (this.SelectedMember != null)
      this.Members.Remove(this.SelectedMember);

    this.CreateCommand.RaiseCanExecuteChanged();
  }

  /// <summary>Creates the manifest pool; returns the manifest or throws a <see cref="ManifestException"/> with a precise message.</summary>
  public PoolManifest Create() {
    var specs = this.Members.Select(m => new PoolLifecycle.MemberSpec(
      m.Location,
      m.Role,
      Credential: m.Credential == null ? null : "cred-ref:" + CredentialStore.NormalizeReference(m.Credential),
      Network: MemberSchemes.IsRemote(m.Kind.Scheme)));

    var manifest = this._lifecycle.Create(this.PoolName.Trim(), specs, this.MountTarget.Trim().Length == 0 ? null : this.MountTarget.Trim());
    this.StatusMessage = Localizer.Instance.Get("common.success");
    return manifest;
  }

}
