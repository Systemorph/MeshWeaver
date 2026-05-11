using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// Internal helper that does the recursive copy work behind a
/// <see cref="MirrorRequest"/>. Not a public service — the hub handler
/// (<see cref="MirrorHubExtensions.HandleMirror"/>) creates one of these per
/// request and subscribes to its result observable. Callers post a
/// <see cref="MirrorRequest"/> at the mesh hub and observe the response;
/// they never touch this class directly.
///
/// <para>Both directions reuse <see cref="StorageImporter"/> as the recursive
/// copy engine; the only thing that varies is which side is the source and
/// which is the target.</para>
///
/// <para><b>Public methods return <see cref="IObservable{T}"/></b> per the
/// AsynchronousCalls.md convention. The only Task bridges in the
/// implementation are at the persistence-I/O leaves (StorageImporter and
/// the per-node enumeration in <see cref="EnumerateRecursive"/>) — none of
/// these touch a hub round-trip, so the deadlock pattern documented in
/// AsynchronousCalls.md does not apply.</para>
/// </summary>
internal sealed class MirrorOperations
{
    private readonly IStorageAdapter _localAdapter;
    private readonly IRemoteMeshClientFactory _remoteFactory;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public MirrorOperations(
        IStorageAdapter localAdapter,
        IRemoteMeshClientFactory remoteFactory,
        ILogger logger,
        JsonSerializerOptions jsonOptions)
    {
        _localAdapter = localAdapter;
        _remoteFactory = remoteFactory;
        _logger = logger;
        _jsonOptions = jsonOptions;
    }

    public IObservable<MirrorResult> Run(MirrorRequest request) =>
        Observable.Defer(() =>
        {
            ValidateRequest(request);

            // Open the remote client through the injected factory — production
            // gives us McpRemoteMeshClient over HTTPS, tests get a stub that
            // records the calls. Either way we own disposal: McpRemoteMeshClient
            // is IAsyncDisposable; the stub may or may not be — handled below.
            var remote = _remoteFactory.Create(request.RemoteBaseUrl, request.RemoteToken);
            IStorageAdapter remoteAdapter = new HttpMeshStorageAdapter(remote);

            // If the caller asked for a different namespace on the destination
            // (e.g. push `rbuergi/Story` to `Systemorph/Story`), wrap the
            // *remote* adapter with a PathRemappingStorageAdapter so every
            // path that flows through gets rewritten on the way out.
            var hasRemap = !string.IsNullOrEmpty(request.TargetPath)
                           && !string.Equals(request.TargetPath, request.SourcePath, StringComparison.Ordinal);
            if (hasRemap)
                remoteAdapter = new PathRemappingStorageAdapter(remoteAdapter, request.SourcePath, request.TargetPath!);

            var isPush = !string.Equals(request.Direction, "Pull", StringComparison.OrdinalIgnoreCase);
            var (source, target) = isPush
                ? (_localAdapter, remoteAdapter)
                : (remoteAdapter, _localAdapter);

            var run = request.DryRun
                ? RunDryRun(source, request, isPush)
                : RunImport(source, target, request, isPush);

            return run.Finally(() =>
            {
                if (remote is IAsyncDisposable adisp) _ = adisp.DisposeAsync();
                else if (remote is IDisposable disp) disp.Dispose();
            });
        });

    private IObservable<MirrorResult> RunDryRun(IStorageAdapter source, MirrorRequest request, bool isPush) =>
        Observable.FromAsync(ct => EnumerateRecursive(source, request.SourcePath, ct))
            .Select(paths => new MirrorResult
            {
                Status = "DryRun",
                Direction = isPush ? "Push" : "Pull",
                SourcePath = request.SourcePath,
                TargetPath = request.TargetPath ?? request.SourcePath,
                NodesScanned = paths.Count,
                Paths = paths,
            });

    private IObservable<MirrorResult> RunImport(
        IStorageAdapter source, IStorageAdapter target, MirrorRequest request, bool isPush) =>
        Observable.FromAsync(async ct =>
        {
            var importer = new StorageImporter(source, target, _logger);
            var importOptions = new StorageImportOptions
            {
                RootPath = string.IsNullOrEmpty(request.SourcePath) ? null : request.SourcePath,
                JsonOptions = _jsonOptions,
                ImportPartitions = true,
                RemoveMissing = request.RemoveMissing,
            };

            var result = await importer.ImportAsync(importOptions, ct).ConfigureAwait(false);

            return new MirrorResult
            {
                Status = "Ok",
                Direction = isPush ? "Push" : "Pull",
                SourcePath = request.SourcePath,
                TargetPath = request.TargetPath ?? request.SourcePath,
                NodesImported = result.NodesImported,
                NodesSkipped = result.NodesSkipped,
                NodesRemoved = result.NodesRemoved,
                PartitionsImported = result.PartitionsImported,
                ElapsedMs = (long)result.Elapsed.TotalMilliseconds,
            };
        });

    private static async Task<List<string>> EnumerateRecursive(
        IStorageAdapter adapter, string? rootPath, CancellationToken ct)
    {
        var collected = new List<string>();
        var queue = new Queue<string?>();
        queue.Enqueue(string.IsNullOrEmpty(rootPath) ? null : rootPath);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            ct.ThrowIfCancellationRequested();
            var (nodePaths, dirPaths) = await adapter.ListChildPaths(path).FirstAsync().ToTask(ct).ConfigureAwait(false);
            foreach (var nodePath in nodePaths)
            {
                collected.Add(nodePath);
                queue.Enqueue(nodePath);
            }
            foreach (var dirPath in dirPaths)
                queue.Enqueue(dirPath);
        }
        return collected
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidateRequest(MirrorRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RemoteBaseUrl))
            throw new ArgumentException("RemoteBaseUrl is required.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.RemoteToken))
            throw new ArgumentException("RemoteToken is required.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.SourcePath))
            throw new ArgumentException("SourcePath is required.", nameof(req));
    }
}
