using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Fixture;

/// <summary>
/// Test-only awaitable wrappers over the <see cref="IStorageAdapter"/> reactive surface.
/// Production code MUST NOT use these — it composes <c>IObservable&lt;T&gt;</c> end-to-end per
/// <c>Doc/Architecture/AsynchronousCalls.md</c>. Lives in <c>MeshWeaver.Fixture</c> so production
/// code can't accidentally take a reference.
///
/// <para>🚨 <b>Every wait here goes through
/// <see cref="ReactiveCompletion.ObserveCompletion{T}(System.IObservable{T}, System.Action{System.Exception}, System.Threading.CancellationToken)"/> — never an Rx-to-Task bridge</b>
/// (maintainer, 2026-08-30: <i>"no ToTask ever"</i>; the old "tests are the sanctioned edge"
/// exemption is RETRACTED). Rx's own bridge completes its <c>TaskCompletionSource</c> from INSIDE
/// the pipeline without <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so the
/// awaiting test resumes INLINE on the adapter's signalling thread, still inside Rx's trampoline
/// (<c>Producer.SubscribeRaw</c>) — and everything the test body then does inherits that flag.
/// That is not a test-only concern: it changes how the code under test runs, and it is the
/// documented root of #2377. <c>ObserveCompletion</c> queues the continuation instead and keeps
/// its error arm attached so a late fault is reported rather than orphaned.</para>
/// </summary>
public static class IStorageAdapterTestExtensions
{
    /// <summary>Test bridge: <c>Read</c> → awaitable single node (or null).</summary>
    public static Task<MeshNode?> ReadAsync(this IStorageAdapter adapter, string path, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.Read(path, options).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Legacy alias for tests: <c>GetNodeAsync</c> → <c>Read</c>.</summary>
    public static Task<MeshNode?> GetNodeAsync(this IStorageAdapter adapter, string path, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.Read(path, options).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Test bridge: <c>Write</c> → awaitable persisted node (may be null if the adapter declined).</summary>
    public static Task<MeshNode?> WriteAsync(this IStorageAdapter adapter, MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.Write(node, options).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Legacy alias for tests: <c>SaveNode</c> → <c>Write</c>.</summary>
    public static IObservable<MeshNode?> SaveNode(this IStorageAdapter adapter, MeshNode node, JsonSerializerOptions options)
        => adapter.Write(node, options);

    /// <summary>
    /// Synchronous seed bridge, for use from a <c>ConfigureMesh(MeshBuilder)</c> override —
    /// synchronous by framework contract, called during test construction before any async
    /// lifecycle hook exists, so there is no `await`-able call site to bridge from. In practice
    /// this is always called against an in-memory adapter (e.g.
    /// <c>MeshWeaver.Hosting.Persistence.InMemoryStorageAdapter</c>), typed against the
    /// <see cref="IStorageAdapter"/> interface here so <c>MeshWeaver.Fixture</c> does not need a
    /// reference to <c>MeshWeaver.Hosting</c>.
    ///
    /// <para>🚨 This is NOT a <c>.FirstAsync().ToTask().GetAwaiter().GetResult()</c> bridge (#2013).
    /// That shape parks the calling thread in a native wait until the Task completes — safe only
    /// while the source happens to complete synchronously, which is a property of today's
    /// implementation, not a guarantee, and was flagged as the first place to look when a wedge
    /// with no failing test recurs. This helper never blocks at all: it subscribes directly (no
    /// Task in between) and asserts, loudly and by name, that the write already landed by the time
    /// <c>Subscribe</c> returns — which holds for an in-memory adapter backed by
    /// <c>Observable.Defer</c> + <c>Observable.Return</c>, both synchronous on the calling thread
    /// with no scheduler indirection. If that invariant is ever broken (or this is pointed at an
    /// adapter that genuinely goes async), this throws a named exception pointing at the seeded
    /// path instead of hanging the shard for 8 minutes with no failing test recorded.</para>
    /// </summary>
    public static void SaveNodeSynchronously(
        this IStorageAdapter adapter, MeshNode node, JsonSerializerOptions options)
    {
        var completed = false;
        Exception? failure = null;
        // Explicit onError (never the throwing default-stub) so a fault surfaces as OUR named
        // exception with the seeded path, not a raw rethrow on whatever thread raised it. Disposing
        // right after Subscribe returns — success, failure, or neither — stops a subscription that
        // turned out to be genuinely async instead of leaving it running in the background after
        // this method has already thrown "did not complete synchronously".
        var subscription = adapter.Write(node, options)
            .Subscribe(_ => completed = true, ex => failure = ex);
        subscription.Dispose();

        if (failure is not null)
            throw new InvalidOperationException($"Synchronous seed of '{node.Path}' failed.", failure);
        if (!completed)
            throw new InvalidOperationException(
                $"Synchronous seed of '{node.Path}' did not complete synchronously on Subscribe. "
                + "InMemoryStorageAdapter.Write must stay Observable.Defer + Observable.Return "
                + "(no scheduler indirection) for ConfigureMesh-time seeding via "
                + "SaveNodeSynchronously to be safe — see #2013.");
    }

    /// <summary>Legacy alias for tests: <c>SaveNodeAsync</c> → <c>Write</c>.</summary>
    public static Task<MeshNode?> SaveNodeAsync(this IStorageAdapter adapter, MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.Write(node, options).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Test bridge: <c>Delete</c> → awaitable deleted path.</summary>
    /// <remarks>The <c>!</c> is sound, not a shrug: <c>ObserveCompletion</c> yields
    /// <c>default</c> only when the source completes WITHOUT a value, and <c>FirstAsync()</c>
    /// faults on an empty source instead of completing — so null is unreachable here.</remarks>
    public static Task<string> DeleteAsync(this IStorageAdapter adapter, string path, CancellationToken ct = default)
        => adapter.Delete(path).FirstAsync().ObserveCompletion(ReportLateFault, ct)!;

    /// <summary>Test bridge: <c>Exists</c> → awaitable bool.</summary>
    public static Task<bool> ExistsAsync(this IStorageAdapter adapter, string path, CancellationToken ct = default)
        => adapter.Exists(path).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Test bridge: <c>ListChildPaths</c> → awaitable (node paths, directory paths).</summary>
    public static Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        this IStorageAdapter adapter, string? parentPath, CancellationToken ct = default)
        => adapter.ListChildPaths(parentPath).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Legacy alias for tests: <c>GetChildrenAsync</c> → walks via ListChildPaths + Read per path.</summary>
    public static async IAsyncEnumerable<MeshNode> GetChildrenAsync(
        this IStorageAdapter adapter, string? parentPath, JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (nodePaths, _) = await adapter.ListChildPaths(parentPath).FirstAsync().ObserveCompletion(ReportLateFault, ct);
        foreach (var path in nodePaths)
        {
            var node = await adapter.Read(path, options).FirstAsync().ObserveCompletion(ReportLateFault, ct);
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
        // `!`: FirstAsync() faults rather than completing empty, so the wait cannot settle with null.
        var list = (await adapter.GetPartitionObjects(nodePath, subPath, options)
            .ToList().FirstAsync().ObserveCompletion(ReportLateFault, ct))!;
        foreach (var obj in list) yield return obj;
    }

    /// <summary>Test bridge: <c>SavePartitionObjects</c> → awaitable completion.</summary>
    public static Task<System.Reactive.Unit> SavePartitionObjectsAsync(
        this IStorageAdapter adapter, string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.SavePartitionObjects(nodePath, subPath, objects, options).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Test bridge: <c>DeletePartitionObjects</c> → awaitable completion.</summary>
    public static Task<System.Reactive.Unit> DeletePartitionObjectsAsync(
        this IStorageAdapter adapter, string nodePath, string? subPath = null, CancellationToken ct = default)
        => adapter.DeletePartitionObjects(nodePath, subPath).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Test bridge: <c>GetPartitionMaxTimestamp</c> → awaitable timestamp (or null).</summary>
    public static Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        this IStorageAdapter adapter, string nodePath, string? subPath = null, CancellationToken ct = default)
        => adapter.GetPartitionMaxTimestamp(nodePath, subPath).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>Test bridge: <c>ListPartitionSubPaths</c> → awaitable sub-path list.</summary>
    public static Task<IEnumerable<string>> ListPartitionSubPathsAsync(
        this IStorageAdapter adapter, string nodePath, CancellationToken ct = default)
        => adapter.ListPartitionSubPaths(nodePath).FirstAsync().ObserveCompletion(ReportLateFault, ct)!;

    /// <summary>Test bridge: <c>FindBestPrefixMatch</c> → awaitable (node, matched-segment-count).</summary>
    public static Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        this IStorageAdapter adapter, string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
        => adapter.FindBestPrefixMatch(fullPath, options).FirstAsync().ObserveCompletion(ReportLateFault, ct);

    /// <summary>
    /// Late-fault sink for every wait above — a fault that arrives AFTER the wait already
    /// settled, which a <see cref="Task"/> cannot represent. Never empty: an ignored late fault
    /// is half of what <see cref="ReactiveCompletion.ObserveCompletion{T}(System.IObservable{T}, System.Action{System.Exception}, System.Threading.CancellationToken)"/> exists to remove,
    /// and an unobserved one detonates on the finalizer as
    /// <see cref="TaskScheduler.UnobservedTaskException"/>, which xUnit v3 escalates to a
    /// Catastrophic failure that poisons the NEXT test class.
    /// </summary>
    private static void ReportLateFault(Exception ex)
        => Trace.TraceError(
            "IStorageAdapterTestExtensions: the storage adapter faulted AFTER the wait settled — "
            + "reported, not orphaned: {0}: {1}", ex.GetType().Name, ex.Message);
}
