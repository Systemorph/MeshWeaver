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
    /// Resolves a full URL path to an address using score-based matching.
    /// Returns the best matching node's address and the remaining path segments.
    /// Score is the number of matching segments from the path start.
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
/// Result of path resolution containing the matched prefix and remaining path.
/// </summary>
public record AddressResolution(
    string Prefix,
    string? Remainder
);
