using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Fixture;

/// <summary>
/// Test-only sanctioned bridge from <see cref="IObservable{T}"/> back to <see cref="Task{T}"/>
/// at the test boundary. Production code MUST NOT use these — it composes
/// <c>IObservable&lt;T&gt;</c> end-to-end per <c>Doc/Architecture/AsynchronousCalls.md</c>.
/// Tests are allowed to bridge because their assertion phase requires waiting for completion.
/// Lives in <c>MeshWeaver.Fixture</c> so production code can't accidentally take a reference.
/// </summary>
public static class IStorageAdapterTestExtensions
{
    /// <summary>Test bridge: <c>Read</c> → awaitable single node (or null).</summary>
    public static Task<MeshNode?> ReadAsync(this IStorageAdapter adapter, string path, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.Read(path, options).FirstAsync().ToTask(ct);

    /// <summary>Legacy alias for tests: <c>GetNodeAsync</c> → <c>Read</c>.</summary>
    public static Task<MeshNode?> GetNodeAsync(this IStorageAdapter adapter, string path, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.Read(path, options).FirstAsync().ToTask(ct);

    /// <summary>Test bridge: <c>Write</c> → awaitable persisted node (may be null if the adapter declined).</summary>
    public static Task<MeshNode?> WriteAsync(this IStorageAdapter adapter, MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.Write(node, options).FirstAsync().ToTask(ct);

    /// <summary>Legacy alias for tests: <c>SaveNode</c> → <c>Write</c>.</summary>
    public static IObservable<MeshNode?> SaveNode(this IStorageAdapter adapter, MeshNode node, JsonSerializerOptions options)
        => adapter.Write(node, options);

    /// <summary>Legacy alias for tests: <c>SaveNodeAsync</c> → <c>Write</c>.</summary>
    public static Task<MeshNode?> SaveNodeAsync(this IStorageAdapter adapter, MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.Write(node, options).FirstAsync().ToTask(ct);

    /// <summary>Test bridge: <c>Delete</c> → awaitable deleted path.</summary>
    public static Task<string> DeleteAsync(this IStorageAdapter adapter, string path, CancellationToken ct = default)
        => adapter.Delete(path).FirstAsync().ToTask(ct);

    /// <summary>Test bridge: <c>Exists</c> → awaitable bool.</summary>
    public static Task<bool> ExistsAsync(this IStorageAdapter adapter, string path, CancellationToken ct = default)
        => adapter.Exists(path).FirstAsync().ToTask(ct);

    /// <summary>Test bridge: <c>ListChildPaths</c> → awaitable (node paths, directory paths).</summary>
    public static Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        this IStorageAdapter adapter, string? parentPath, CancellationToken ct = default)
        => adapter.ListChildPaths(parentPath).FirstAsync().ToTask(ct);

    /// <summary>Legacy alias for tests: <c>GetChildrenAsync</c> → walks via ListChildPaths + Read per path.</summary>
    public static async IAsyncEnumerable<MeshNode> GetChildrenAsync(
        this IStorageAdapter adapter, string? parentPath, JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (nodePaths, _) = await adapter.ListChildPaths(parentPath).FirstAsync().ToTask(ct);
        foreach (var path in nodePaths)
        {
            var node = await adapter.Read(path, options).FirstAsync().ToTask(ct);
            if (node != null) yield return node;
        }
    }

    /// <summary>Legacy alias for tests: <c>DeleteNode</c> → <c>Delete</c>.</summary>
    public static IObservable<string> DeleteNode(this IStorageAdapter adapter, string path, bool recursive = false)
        => adapter.Delete(path);

    /// <summary>Legacy alias for tests: <c>MoveNode</c> via copy-delete (in-memory tests only — production uses per-node-hub fan-out).</summary>
    public static IObservable<MeshNode?> MoveNode(this IStorageAdapter adapter, string sourcePath, string targetPath, JsonSerializerOptions options)
        => adapter.Read(sourcePath, options)
            .SelectMany(source =>
            {
                if (source is null)
                    return Observable.Throw<MeshNode>(new InvalidOperationException($"Source node not found: {sourcePath}"));
                var moved = MeshNode.FromPath(targetPath) with
                {
                    Name = source.Name,
                    NodeType = source.NodeType,
                    Icon = source.Icon,
                    Order = source.Order,
                    Content = source.Content,
                    HubConfiguration = source.HubConfiguration,
                    GlobalServiceConfigurations = source.GlobalServiceConfigurations
                };
                return adapter.Write(moved, options).SelectMany(_ => adapter.Delete(sourcePath).Select(_ => (MeshNode?)moved));
            });

    /// <summary>Test bridge: <c>GetPartitionObjects</c> → async-enumerable of partition objects.</summary>
    public static async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        this IStorageAdapter adapter, string nodePath, string? subPath, JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var list = await adapter.GetPartitionObjects(nodePath, subPath, options).ToList().FirstAsync().ToTask(ct);
        foreach (var obj in list) yield return obj;
    }

    /// <summary>Test bridge: <c>SavePartitionObjects</c> → awaitable completion.</summary>
    public static Task<System.Reactive.Unit> SavePartitionObjectsAsync(
        this IStorageAdapter adapter, string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.SavePartitionObjects(nodePath, subPath, objects, options).FirstAsync().ToTask(ct);

    /// <summary>Test bridge: <c>DeletePartitionObjects</c> → awaitable completion.</summary>
    public static Task<System.Reactive.Unit> DeletePartitionObjectsAsync(
        this IStorageAdapter adapter, string nodePath, string? subPath = null, CancellationToken ct = default)
        => adapter.DeletePartitionObjects(nodePath, subPath).FirstAsync().ToTask(ct);

    /// <summary>Test bridge: <c>GetPartitionMaxTimestamp</c> → awaitable timestamp (or null).</summary>
    public static Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        this IStorageAdapter adapter, string nodePath, string? subPath = null, CancellationToken ct = default)
        => adapter.GetPartitionMaxTimestamp(nodePath, subPath).FirstAsync().ToTask(ct);

    /// <summary>Test bridge: <c>ListPartitionSubPaths</c> → awaitable sub-path list.</summary>
    public static Task<IEnumerable<string>> ListPartitionSubPathsAsync(
        this IStorageAdapter adapter, string nodePath, CancellationToken ct = default)
        => adapter.ListPartitionSubPaths(nodePath).FirstAsync().ToTask(ct);

    /// <summary>Test bridge: <c>FindBestPrefixMatch</c> → awaitable (node, matched-segment-count).</summary>
    public static Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        this IStorageAdapter adapter, string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.FindBestPrefixMatch(fullPath, options).FirstAsync().ToTask(ct);
}
