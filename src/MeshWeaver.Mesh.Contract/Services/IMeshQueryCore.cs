using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor")]

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Infrastructure query interface without access control.
/// Used by infrastructure code (login, NodeTypeService, compilation) that needs
/// raw queries without user context. Must not be exposed to application code.
/// </summary>
internal interface IMeshQueryCore
{
    /// <summary>
    /// Query nodes without access control filtering.
    /// </summary>
    IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        CancellationToken ct = default);
}
