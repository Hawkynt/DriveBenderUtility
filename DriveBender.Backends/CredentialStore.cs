using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DivisonM.Vfs;

namespace DivisonM.Backends;

/// <summary>
/// Credential resolution by reference (SEC-CRED): manifests carry only handles like
/// "cred-ref:MyPool-server"; secrets live in the Windows Credential Manager, with a
/// restrictive-permission JSON file under the config root as the cross-platform
/// fallback. Secrets are never written to manifests, logs or metrics.
/// </summary>
public sealed class CredentialStore(IHostEnvironment host, bool useOsStore = true) : ICredentialResolver {

  private const string _TARGET_PREFIX = "DriveBenderUtility/";
  private const string _REFERENCE_PREFIX = "cred-ref:";

  private bool _UseOsStore => useOsStore && OperatingSystem.IsWindows();

  private string _FileStorePath => Path.Combine(host.ConfigRoot, "credentials.json");

  public static string NormalizeReference(string reference)
    => reference.StartsWith(_REFERENCE_PREFIX, StringComparison.OrdinalIgnoreCase) ? reference[_REFERENCE_PREFIX.Length..] : reference;

  public NetworkCredential? Resolve(string credentialReference) {
    var name = NormalizeReference(credentialReference);

    if (this._UseOsStore && _WindowsCredentialManager.TryRead(_TARGET_PREFIX + name, out var user, out var secret))
      return new(user, secret);

    var store = this._LoadFileStore();
    return store.TryGetValue(name, out var entry) ? new(entry.User ?? "", entry.Secret ?? "") : null;
  }

  /// <summary>Stores a secret under a reference name; Windows Credential Manager first, file store elsewhere.</summary>
  public void Store(string reference, string userName, string secret) {
    var name = NormalizeReference(reference);

    if (this._UseOsStore) {
      _WindowsCredentialManager.Write(_TARGET_PREFIX + name, userName, secret);
      return;
    }

    var store = this._LoadFileStore();
    store[name] = new(userName, secret);
    this._SaveFileStore(store);
  }

  public void Remove(string reference) {
    var name = NormalizeReference(reference);
    if (this._UseOsStore && _WindowsCredentialManager.Delete(_TARGET_PREFIX + name))
      return;

    var store = this._LoadFileStore();
    if (store.Remove(name))
      this._SaveFileStore(store);
  }

  private sealed record FileEntry(string? User, string? Secret);

  private Dictionary<string, FileEntry> _LoadFileStore() {
    if (!host.FileExists(this._FileStorePath))
      return new(StringComparer.OrdinalIgnoreCase);

    try {
      return JsonSerializer.Deserialize<Dictionary<string, FileEntry>>(host.ReadAllText(this._FileStorePath))
             ?? new(StringComparer.OrdinalIgnoreCase);
    } catch (JsonException) {
      return new(StringComparer.OrdinalIgnoreCase);
    }
  }

  private void _SaveFileStore(Dictionary<string, FileEntry> store) {
    host.CreateDirectory(host.ConfigRoot);
    host.WriteAllTextAtomic(this._FileStorePath, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));

    // the fallback file is secrets-at-rest: clamp it to owner-only where the OS supports it
    if (!OperatingSystem.IsWindows() && File.Exists(this._FileStorePath))
      File.SetUnixFileMode(this._FileStorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
  }

  /// <summary>Windows Credential Manager (CRED_TYPE_GENERIC) P/Invoke.</summary>
  private static class _WindowsCredentialManager {

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL {
      public uint Flags;
      public uint Type;
      public string TargetName;
      public string? Comment;
      public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
      public uint CredentialBlobSize;
      public IntPtr CredentialBlob;
      public uint Persist;
      public uint AttributeCount;
      public IntPtr Attributes;
      public string? TargetAlias;
      public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static bool TryRead(string target, out string userName, out string secret) {
      userName = "";
      secret = "";
      if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var handle))
        return false;

      try {
        var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
        userName = credential.UserName ?? "";
        secret = credential.CredentialBlobSize > 0
          ? Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2) ?? ""
          : "";
        return true;
      } finally {
        CredFree(handle);
      }
    }

    public static void Write(string target, string userName, string secret) {
      var blob = Marshal.StringToCoTaskMemUni(secret);
      try {
        var credential = new CREDENTIAL {
          Type = CRED_TYPE_GENERIC,
          TargetName = target,
          UserName = userName,
          CredentialBlob = blob,
          CredentialBlobSize = (uint)(secret.Length * 2),
          Persist = CRED_PERSIST_LOCAL_MACHINE,
        };

        if (!CredWrite(ref credential, 0))
          throw new PoolFsException(PoolFsError.IoError, $"Windows Credential Manager rejected the write (error {Marshal.GetLastWin32Error()})");
      } finally {
        Marshal.FreeCoTaskMem(blob);
      }
    }

    public static bool Delete(string target) => CredDelete(target, CRED_TYPE_GENERIC, 0);

  }

}

/// <summary>
/// Helpers for structured secrets: providers with multiple fields (S3 keys, SFTP private
/// keys, connection strings) store a JSON object as the secret; simple providers store a
/// plain password.
/// </summary>
public static class CredentialPayload {

  public static bool TryGetJsonField(string secret, string field, out string value) {
    value = "";
    var trimmed = secret.TrimStart();
    if (!trimmed.StartsWith('{'))
      return false;

    try {
      using var document = JsonDocument.Parse(trimmed);
      if (document.RootElement.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String) {
        value = property.GetString() ?? "";
        return value.Length > 0;
      }
    } catch (JsonException) {
      // plain-text secret that merely starts with a brace
    }

    return false;
  }

  /// <summary>The effective password: the "password" field of a JSON payload, or the raw secret.</summary>
  public static string Password(string secret)
    => TryGetJsonField(secret, "password", out var password) ? password : secret;

}
