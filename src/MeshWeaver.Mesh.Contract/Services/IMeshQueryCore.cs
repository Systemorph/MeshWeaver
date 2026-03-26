using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Internal core query interface without access control.
/// Follows the same pattern as IStorageService (core) vs IMeshStorage (wrapper).
/// Used by infrastructure code (NodeTypeService, MeshCatalog, compilation) that needs
/// raw queries without user context.
/// </summary>
public interface IMeshQueryCore
{
    /// <summary>
    /// Query nodes without access control filtering.
    /// </summary>
    IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        CancellationToken ct = default);
}
