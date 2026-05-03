using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// <see cref="IStorageAdapter"/> backed by a remote MeshWeaver portal over its MCP
/// HTTP surface. Plug it in as the <em>target</em> of <see cref="StorageImporter"/>
/// to push a subtree out, or as the <em>source</em> to pull a subtree in. The
/// existing <see cref="StorageImporter.ImportAsync"/> recursive copy loop does the
/// rest — node-by-node walk, partition objects, optional missing-node deletion.
///
/// <para>All node-level operations route through <see cref="IRemoteMeshClient"/>
/// which has an <see cref="IObservable{T}"/>-based surface (per
/// AsynchronousCalls.md). This adapter implements <see cref="IStorageAdapter"/>'s
/// Task-based contract by bridging at the <em>single</em> entry point of each
/// method via <c>.FirstAsync().ToTask(ct)</c> — there are no nested awaits and
/// no hub round-trips, so the canonical "await hub.GetMeshNode().ToTask()"
/// deadlock cannot occur (HTTP I/O has no scheduler-bridging continuation back
/// to a hub action block).</para>
///
/// <para>Auth is the receiving portal's standard <c>Authorization: Bearer {token}</c>
/// ApiToken flow — no special trust between portals, just a token the user
/// issued on the destination.</para>
///
/// <para><b>Partition objects (v1 scope):</b> <see cref="GetPartitionObjectsAsync"/>,
/// <see cref="SavePartitionObjectsAsync"/>, and friends are no-ops. The current
/// MCP tool surface doesn't expose a generic "enumerate partition objects" call;
/// for the immediate use-case (markdown / Code / NodeType nodes whose data lives
/// inline on <c>node.Content</c>) the inline payload survives a Get + Create
/// round-trip cleanly. Partition data on satellites (Activity messages, Comment
/// trees, etc.) is a follow-up.</para>
/// </summary>
public sealed class HttpMeshStorageAdapter : IStorageAdapter
{
    private readonly IRemoteMeshClient _client;

    public HttpMeshStorageAdapter(IRemoteMeshClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => _client.Get(path).FirstAsync().ToTask(ct);

    public Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        // Upsert as a single observable chain: probe existence (Get) → choose
        // Create or Update → emit a final Unit. The bridge to Task happens
        // exactly once at the boundary; everything before is composed.
        => _client.Get(node.Path)
            .SelectMany(existing => existing is null
                ? _client.Create(node)
                : _client.Update(node))
            .FirstAsync()
            .ToTask(ct);

    public Task DeleteAsync(string path, CancellationToken ct = default)
        => _client.Delete(path).FirstAsync().ToTask(ct);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => _client.Get(path).Select(n => n is not null).FirstAsync().ToTask(ct);

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath, CancellationToken ct = default)
    {
        // Immediate children only — search by exact namespace, not by subtree.
        // (`scope:subtree` would return descendants too, which the recursive
        //  StorageImporter loop is already going to traverse one level at a time.)
        // Empty parent means root-level children: nodes whose namespace is empty.
        var query = string.IsNullOrEmpty(parentPath)
            ? "namespace:"
            : $"namespace:{parentPath}";
        // Remote-backed adapters have no notion of "directories without nodes" —
        // every container is also a node. Always return empty for DirectoryPaths.
        return _client.SearchPaths(query)
            .Select(paths => ((IEnumerable<string>)paths, (IEnumerable<string>)Array.Empty<string>()))
            .FirstAsync()
            .ToTask(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// V1: returns null (no partition object enumeration over MCP yet).
    /// Inline node content (`node.Content`) survives a normal read/write round-trip
    /// — only nodes whose data lives <em>outside</em> the inline content (e.g. file
    /// collections, satellite-table objects) need this method, and the current
    /// MCP tool surface doesn't expose a generic enumerate-partition call.
    /// </remarks>
    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    /// <remarks>V1: no-op. See <see cref="GetPartitionObjectsAsync"/>.</remarks>
    public Task SavePartitionObjectsAsync(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>V1: no-op. See <see cref="GetPartitionObjectsAsync"/>.</remarks>
    public Task DeletePartitionObjectsAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>V1: returns null (caches keyed off this timestamp will refresh as needed).</remarks>
    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(null);
}
