using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Blazor")]
[assembly: InternalsVisibleTo("MeshWeaver.Blazor.Portal")]

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Infrastructure query interface without access control.
/// Used by infrastructure code (login, NodeTypeService, compilation,
/// SecurityService's own AccessAssignment lookup) that needs raw queries
/// without user context. Must not be exposed to application code.
///
/// <para>Decouples consumers from <see cref="IMeshQueryProvider"/> which
/// pulls in <see cref="MeshWeaver.Mesh.Security.ISecurityService"/> as a constructor dependency
/// — the cycle source for SecurityService → workspace.GetQuery →
/// SyncedQueryMeshNodes → IMeshQueryProvider → InMemoryMeshQuery →
/// ISecurityService.</para>
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

    /// <summary>
    /// Observe nodes matching a query without access control filtering.
    /// Emits Initial / Added / Updated / Removed deltas as the underlying
    /// data changes. Same shape as
    /// <see cref="IMeshQueryProvider.ObserveQuery{T}(MeshQueryRequest, JsonSerializerOptions)"/>
    /// — minus the security filter on the result set.
    /// </summary>
    IObservable<QueryResultChange<T>> ObserveQuery<T>(
        MeshQueryRequest request,
        JsonSerializerOptions options);
}
