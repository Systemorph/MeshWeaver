using MeshWeaver.Messaging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Mesh.Services;

public interface IMeshCatalog
{
    MeshConfiguration Configuration { get; }
    Task<MeshNode?> GetNodeAsync(Address address);

    Task UpdateAsync(MeshNode node);

    Task<StreamInfo> GetStreamInfoAsync(Address address);

    /// <summary>
    /// Global registry for unified path prefixes.
    /// Enables resolution of paths like "pricing:MS-2024" to target address and workspace reference.
    /// </summary>
    IUnifiedPathRegistry PathRegistry { get; }

    /// <summary>
    /// Gets all namespaces that describe available address types for autocomplete.
    /// Used for autocomplete to show top-level items like "pricing/", "agent/", etc.
    /// </summary>
    Task<IReadOnlyList<MeshNamespace>> GetNamespacesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a namespace by its prefix (address type).
    /// Returns null if the namespace is not registered.
    /// </summary>
    MeshNamespace? GetNamespace(string prefix);

    /// <summary>
    /// Resolves an address from URL components using registered namespaces.
    /// Returns the resolved Address and namespace, or null if the address type is not registered.
    /// </summary>
    AddressResolution? ResolveAddress(string addressType, string? id = null);

    /// <summary>
    /// Resolves a full URL path to an address using registered namespace patterns.
    /// Returns the resolved Address, namespace, and parsed area/id from the path.
    /// </summary>
    AddressResolution? ResolvePath(string path);
}



public record StreamInfo(
    StreamType Type,
    string Provider, 
    string Namespace);
public enum StreamType{Stream, Channel}
public record StorageInfo(
    string Id, 
    string BaseDirectory, 
    string AssemblyLocation, 
    string AddressType);


public record StartupInfo(Address Address, string PackageName, string AssemblyLocation);

/// <summary>
/// Result of address resolution containing the resolved address and remaining path.
/// </summary>
public record AddressResolution(
    Address Address,
    string? Remainder
);
