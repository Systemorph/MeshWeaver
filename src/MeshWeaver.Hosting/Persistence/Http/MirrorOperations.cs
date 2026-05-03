using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Http;

/// <summary>
/// Cross-instance mirror — push or pull a subtree of MeshNodes between two
/// MeshWeaver portals over MCP-HTTP. Both directions reuse
/// <see cref="StorageImporter"/> as the recursive copy engine; the only thing
/// that varies is which side is the source and which is the target.
///
/// <para>The instance running this code is always the orchestrator (it's the
/// one with outbound HTTP reach). The <em>remote</em> portal must be reachable
/// over HTTPS and accept Bearer-token auth via its <c>/mcp</c> endpoint.</para>
///
/// <para><b>Public surface is <see cref="IObservable{T}"/></b> per the
/// AsynchronousCalls.md convention. Callers compose with <c>.Select</c> /
/// <c>.Subscribe</c>; the only Task bridges in the implementation are
/// (a) the leaf <c>StorageImporter.ImportAsync</c> call (Task-based by
/// <see cref="IStorageAdapter"/> contract) and (b) the per-node enumeration
/// calls inside <see cref="EnumerateRecursive"/>. None of these touch a hub
/// round-trip — they're persistence I/O — so the deadlock pattern documented
/// in AsynchronousCalls.md does not apply.</para>
///
/// <para>Returns a structured <see cref="MirrorResult"/> the caller can serialise
/// to a single JSON summary; this is the wire shape both the MCP tool and the UI
/// surface to the user.</para>
/// </summary>
public sealed class MirrorOperations
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MirrorOperations> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public MirrorOperations(IMessageHub hub)
    {
        _services = hub.ServiceProvider;
        _logger = _services.GetService<ILogger<MirrorOperations>>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MirrorOperations>.Instance;
        _jsonOptions = hub.JsonSerializerOptions;
    }

    /// <summary>
    /// Push <paramref name="request"/>'s subtree from the local portal up to a
    /// remote portal at <c>request.RemoteBaseUrl</c>. The remote portal becomes
    /// the target of the recursive copy.
    /// </summary>
    public IObservable<MirrorResult> Push(MirrorRequest request) => Run(request, isPush: true);

    /// <summary>
    /// Pull <paramref name="request"/>'s subtree from a remote portal down to
    /// the local portal. The remote portal is the source; local is the target.
    /// </summary>
    public IObservable<MirrorResult> Pull(MirrorRequest request) => Run(request, isPush: false);

    private IObservable<MirrorResult> Run(MirrorRequest request, bool isPush) =>
        Observable.Defer(() =>
        {
            ValidateRequest(request);

            // The local IStorageAdapter is supplied by the host's persistence
            // configuration (FileSystem / Postgres / Cosmos / …). Resolve it
            // here rather than wiring through DI so MirrorOperations can be
            // constructed without coupling to a specific persistence backend.
            var localAdapter = _services.GetService<IStorageAdapter>()
                ?? throw new InvalidOperationException(
                    "No IStorageAdapter is registered on this hub. " +
                    "Mirroring requires a local persistence backend.");

            // The remote client owns resources (HttpClient + McpClient session)
            // we must dispose. McpRemoteMeshClient is IAsyncDisposable (not
            // IDisposable), so Observable.Using doesn't fit; use Finally to
            // schedule the async disposal when the subscription terminates
            // (success or failure). Fire-and-forget — disposal is best-effort
            // and the next mirror call opens a fresh client anyway.
            var remote = new McpRemoteMeshClient(
                request.RemoteBaseUrl,
                request.RemoteToken,
                _jsonOptions,
                _services.GetService<ILoggerFactory>());
            var remoteAdapter = new HttpMeshStorageAdapter(remote);
            var (source, target) = isPush
                ? ((IStorageAdapter)localAdapter, (IStorageAdapter)remoteAdapter)
                : (remoteAdapter, localAdapter);

            var run = request.DryRun
                ? RunDryRun(source, request, isPush)
                : RunImport(source, target, request, isPush);

            return run.Finally(() => _ = remote.DisposeAsync());
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
            var (nodePaths, dirPaths) = await adapter.ListChildPathsAsync(path, ct).ConfigureAwait(false);
            foreach (var nodePath in nodePaths)
            {
                collected.Add(nodePath);
                queue.Enqueue(nodePath);
            }
            foreach (var dirPath in dirPaths)
                queue.Enqueue(dirPath);
        }
        // De-dupe and stable-order for the report.
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

/// <summary>Inputs to a mirror operation. Same shape for both push and pull.</summary>
public sealed record MirrorRequest
{
    public required string RemoteBaseUrl { get; init; }
    public required string RemoteToken { get; init; }
    public required string SourcePath { get; init; }
    public string? TargetPath { get; init; }
    public bool RemoveMissing { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>Wire-friendly mirror summary — JSON-serialised by both the MCP tool and the UI.</summary>
public sealed record MirrorResult
{
    public required string Status { get; init; }              // "Ok" | "DryRun" | "Error"
    public required string Direction { get; init; }           // "Push" | "Pull"
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public int NodesImported { get; init; }
    public int NodesSkipped { get; init; }
    public int NodesRemoved { get; init; }
    public int PartitionsImported { get; init; }
    public long ElapsedMs { get; init; }
    public int NodesScanned { get; init; }                    // dry-run only
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}
