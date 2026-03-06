namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Public interface for resolving URL paths to mesh node addresses.
/// Used by Blazor navigation to map browser URLs to hub addresses.
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Resolves a full URL path to an address using score-based matching.
    /// Returns the best matching node's address and the remaining path segments.
    /// </summary>
    Task<AddressResolution?> ResolvePathAsync(string path);
}
