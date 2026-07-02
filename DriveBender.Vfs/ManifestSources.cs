using System.Security.Cryptography;
using System.Text;

namespace DivisonM.Vfs;

/// <summary>
/// A definition-source adapter (§6.0.7): both the JSON registry and the native drive
/// scan yield the same <see cref="PoolManifest"/> — the one true pool model. Nothing
/// downstream branches on the source.
/// </summary>
public interface IManifestSource {
  IEnumerable<PoolManifest> Enumerate();
}

/// <summary>Reads explicit manifests from the machine registry directory (§6.0.2).</summary>
public sealed class JsonManifestSource(ManifestStore store) : IManifestSource {
  public IEnumerable<PoolManifest> Enumerate() => store.LoadRegistry();
}

/// <summary>
/// The classic Drive Bender drive scan as a discovery adapter: groups physical volumes
/// by their on-disk pool GUID (from *.MP.$DRIVEBENDER info files) and synthesizes a
/// virtual manifest whose members are the drives' pool-GUID root folders (§1.3). A
/// native pool is a manifest pool whose membership is derived by scanning.
/// </summary>
public sealed class NativeScanSource(IHostEnvironment host) : IManifestSource {

  public IEnumerable<PoolManifest> Enumerate() {
    var membersByPool = new Dictionary<Guid, (string label, string? description, List<PoolMemberDefinition> members)>();

    foreach (var root in host.EnumerateVolumeRoots()) {
      IEnumerable<string> infoFiles;
      try {
        infoFiles = host.EnumerateFiles(root, "*." + DriveBender.DriveBenderConstants.INFO_EXTENSION).ToArray();
      } catch (IOException) {
        continue;
      } catch (UnauthorizedAccessException) {
        continue;
      }

      foreach (var infoFile in infoFiles) {
        var data = _ParseInfoFile(host, infoFile);
        if (data == null)
          continue;

        var (id, label, description) = data.Value;
        var poolRoot = Path.Combine(root, $"{{{id}}}");
        if (!host.DirectoryExists(poolRoot))
          continue;

        var physicalVolumeId = host.GetVolumeIdentity(poolRoot).PhysicalVolumeId;
        var member = new PoolMemberDefinition {
          MemberId = DeriveMemberId(id, physicalVolumeId),
          Path = poolRoot,
          Role = MemberRole.Capacity,
          Label = label,
        };

        if (membersByPool.TryGetValue(id, out var existing))
          existing.members.Add(member);
        else
          membersByPool.Add(id, (label, description, [member]));
      }
    }

    foreach (var (poolId, (label, _, members)) in membersByPool)
      yield return new PoolManifest {
        PoolId = poolId,
        Name = label,
        Members = members,
        Version = 0,
        IsVirtual = true,
      };
  }

  private static (Guid id, string label, string? description)? _ParseInfoFile(IHostEnvironment host, string infoFile) {
    string content;
    try {
      content = host.ReadAllText(infoFile);
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }

    var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in content.Split('\n')) {
      var parts = line.Trim().Split(':', 2);
      if (parts.Length == 2)
        data[parts[0]] = parts[1];
    }

    if (!data.TryGetValue("volumelabel", out var label))
      return null;
    if (!data.TryGetValue("id", out var idText) || !Guid.TryParse(idText, out var id))
      return null;

    return (id, label, data.GetValueOrDefault("description"));
  }

  /// <summary>
  /// Native members carry no marker, so their identity is derived deterministically from
  /// (pool id, physical volume): the same drive resolves to the same member across scans
  /// and letter changes.
  /// </summary>
  public static Guid DeriveMemberId(Guid poolId, string physicalVolumeId) {
    var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"{poolId:N}|{physicalVolumeId.ToUpperInvariant()}"));
    return new(bytes);
  }

}
