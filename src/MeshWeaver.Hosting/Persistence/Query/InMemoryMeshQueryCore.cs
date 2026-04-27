using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Unsecured in-memory implementation of <see cref="IMeshQueryCore"/>.
///
/// <para>Deliberately decoupled from <see cref="ISecurityService"/> so consumers
/// that NEED an insecure query path (NodeTypeService compilation lookups,
/// login flows, SecurityService's own AccessAssignment seed reads) can resolve
/// this <em>without</em> creating a DI cycle through SecurityService.</para>
///
/// <para>The cycle this avoids:
/// SecurityService → workspace.GetQuery → SyncedQueryMeshNodes →
/// <see cref="IMeshQueryProvider"/> (resolves <see cref="InMemoryMeshQuery"/>) →
/// ISecurityService.</para>
///
/// <para>Delegates to the same persistence + parser machinery
/// <see cref="InMemoryMeshQuery"/> uses, but does not apply per-node access
/// control filtering.</para>
/// </summary>
internal class InMemoryMeshQueryCore(
    IStorageService persistence,
    AccessService? accessService = null,
    IDataChangeNotifier? changeNotifier = null,
    MeshConfiguration? meshConfiguration = null,
    ILogger<InMemoryMeshQueryCore>? logger = null)
    : IMeshQueryCore
{
    /// <summary>
    /// Lazy-built inner: same parsing / sorting / paging machinery
    /// <see cref="InMemoryMeshQuery"/> uses, but constructed here with
    /// <c>securityService: null</c> so this class itself has zero
    /// ISecurityService dependency and no DI-cycle.
    /// </summary>
    private InMemoryMeshQuery? _inner;
    private InMemoryMeshQuery Inner => _inner ??= new InMemoryMeshQuery(
        persistence,
        securityService: null,
        accessService: accessService,
        changeNotifier: changeNotifier,
        meshConfiguration: meshConfiguration,
        nodeValidators: null,
        logger: null);

    /// <inheritdoc />
    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in ((IMeshQueryCore)Inner).QueryAsync(request, options, ct))
            yield return item;
    }

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> ObserveQuery<T>(
        MeshQueryRequest request, JsonSerializerOptions options)
        => ((IMeshQueryCore)Inner).ObserveQuery<T>(request, options);
}
