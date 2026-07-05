using Hawkynt.CloudStorage.OAuth;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using IAuthenticationProvider = Microsoft.Kiota.Abstractions.Authentication.IAuthenticationProvider;

namespace Hawkynt.CloudStorage.Stores;

/// <summary>
/// Microsoft OneDrive store over Microsoft.Graph (drive-item path addressing), covering both
/// personal and business (SharePoint/OneDrive-for-Business) accounts on the shared
/// <c>/common</c> endpoint. A <see cref="driveId"/> of empty/"me"/"root" resolves to the
/// signed-in user's default drive on connect. The bearer token is stamped per request from
/// the <see cref="IAccessTokenProvider"/>.
/// </summary>
public sealed class OneDriveCloudStore : ICloudStore {

  private readonly GraphServiceClient _client;
  private readonly string _rootPath;
  private string _driveId;
  private bool _resolved;

  public OneDriveCloudStore(IAccessTokenProvider tokens, string driveId, string rootPath) {
    this._rootPath = rootPath.Trim('/');
    this._driveId = driveId;
    this._client = new(new _TokenAccessAuthProvider(tokens));
  }

  private sealed class _TokenAccessAuthProvider(IAccessTokenProvider tokens) : IAuthenticationProvider {
    public Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) {
      request.Headers.Remove("Authorization");
      request.Headers.Add("Authorization", $"Bearer {tokens.GetAccessToken()}");
      return Task.CompletedTask;
    }
  }

  private string _Drive {
    get {
      if (this._resolved)
        return this._driveId;

      if (string.IsNullOrEmpty(this._driveId) || this._driveId is "me" or "root") {
        var drive = this._client.Me.Drive.GetAsync().GetAwaiter().GetResult()
                    ?? throw new CloudStorageException(CloudStorageError.NotFound, "No default OneDrive for this account");
        this._driveId = drive.Id ?? throw new CloudStorageException(CloudStorageError.NotFound, "Default OneDrive has no id");
      }

      this._resolved = true;
      return this._driveId;
    }
  }

  private string _ItemId(string path) {
    var combined = this._rootPath.Length == 0 ? path : $"{this._rootPath}/{path}".Trim('/');
    return combined.Length == 0 ? "root" : $"root:/{combined}:";
  }

  public void Connect() => _ = this._Drive;

  public bool Probe() {
    try {
      this._client.Drives[this._Drive].Items["root"].GetAsync().GetAwaiter().GetResult();
      return true;
    } catch (Exception) {
      return false;
    }
  }

  private DriveItem? _TryGetItem(string path) {
    try {
      return this._client.Drives[this._Drive].Items[this._ItemId(path)].GetAsync().GetAwaiter().GetResult();
    } catch (ODataError e) when (e.ResponseStatusCode == 404) {
      return null;
    }
  }

  public byte[] Download(string path) {
    var stream = this._client.Drives[this._Drive].Items[this._ItemId(path)].Content.GetAsync().GetAwaiter().GetResult()
                 ?? throw new CloudStorageException(CloudStorageError.NotFound, $"File not found: {path}");

    using (stream) {
      using var buffer = new MemoryStream();
      stream.CopyTo(buffer);
      return buffer.ToArray();
    }
  }

  public void Upload(string path, byte[] content)
    => this._client.Drives[this._Drive].Items[this._ItemId(path)].Content.PutAsync(new MemoryStream(content)).GetAwaiter().GetResult();

  public void DeleteFile(string path)
    => this._client.Drives[this._Drive].Items[this._ItemId(path)].DeleteAsync().GetAwaiter().GetResult();

  public CloudMeta? Stat(string path) {
    var item = this._TryGetItem(path);
    if (item == null)
      return null;

    var modified = item.LastModifiedDateTime?.UtcDateTime ?? DateTime.MinValue;
    return new(item.Folder != null, item.Size ?? 0, item.CreatedDateTime?.UtcDateTime ?? modified, modified);
  }

  public void CreateFolder(string path) {
    var name = CloudPath.GetName(path);
    var parent = CloudPath.GetParent(path);
    this._client.Drives[this._Drive].Items[this._ItemId(parent)].Children.PostAsync(new DriveItem {
      Name = name,
      Folder = new Folder(),
    }).GetAwaiter().GetResult();
  }

  public void DeleteFolder(string path)
    => this._client.Drives[this._Drive].Items[this._ItemId(path)].DeleteAsync().GetAwaiter().GetResult();

  public IEnumerable<CloudEntry> List(string folder) {
    var page = this._client.Drives[this._Drive].Items[this._ItemId(folder)].Children.GetAsync().GetAwaiter().GetResult();
    while (page != null) {
      foreach (var item in page.Value ?? [])
        yield return new(item.Name ?? "", item.Folder != null, item.Size ?? 0, item.LastModifiedDateTime?.UtcDateTime ?? DateTime.MinValue);

      if (page.OdataNextLink == null)
        break;

      page = this._client.Drives[this._Drive].Items[this._ItemId(folder)].Children
        .WithUrl(page.OdataNextLink).GetAsync().GetAwaiter().GetResult();
    }
  }

  public void Dispose() {
  }

}
