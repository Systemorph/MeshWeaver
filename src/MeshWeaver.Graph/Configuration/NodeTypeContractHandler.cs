using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Per-NodeType-hub handler for <see cref="GetCompilationPathRequest"/>.
/// Reads the hub's own <see cref="MeshNode"/>, delegates to
/// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>, and
/// posts the resulting assembly path / hub-config delegate / activity log
/// back to the requester.
/// <para>
/// No per-version response cache, no historical-version branch, no
/// static-provider short-circuit. The underlying disk-level cache in
/// <see cref="ICompilationCacheService"/> is the only cache layer — it's
/// source-aware (re-compiles when any Source/Test Code node's LastModified
/// exceeds the cached DLL's mtime), so a stable response without
/// cross-request caching is the intended behaviour.
/// </para>
/// </summary>
internal static class NodeTypeContractHandler
{
    public static IMessageDelivery Handle(
        IMessageHub hub,
        IMessageDelivery<GetCompilationPathRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeTypeContractHandler");
        var hubPath = hub.Address.ToString();

        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (compilationService == null)
        {
            logger?.LogWarning("No IMeshNodeCompilationService registered — cannot compile {HubPath}.", hubPath);
            hub.Post(Fail(null, $"No IMeshNodeCompilationService registered — cannot compile '{hubPath}'."),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // Reactive chain: read own MeshNode → compile if needed → post response.
        // No await, no Task in the hub flow (Doc/Architecture/AsynchronousCalls.md).
        //
        // AwaitCompilationSettled holds the read until any in-progress compile
        // finishes: a naive Take(1) would hand the requester the previous
        // release's AssemblyLocation while V2 is mid-compile, and every fresh
        // instance hub activated in that gap would render the stale layout.
        // Same primitive used by HandleCreateRelease so request handling is
        // serialised across the compile critical section.
        hub.GetWorkspace().GetMeshNodeStream()
            .AwaitCompilationSettled()
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(60))
            .SelectMany(node =>
            {
                if (node.Content is not NodeTypeDefinition def)
                {
                    logger?.LogDebug(
                        "GetCompilationPathRequest at {HubPath}: MeshNode has no NodeTypeDefinition (Content={ContentType}).",
                        hubPath, node.Content?.GetType().Name ?? "null");
                    return Observable.Return((GetCompilationPathResponse?)Fail(
                        null,
                        $"Node at '{hubPath}' is not a valid NodeType definition "
                        + $"(Content type: {node.Content?.GetType().Name ?? "null"}).")!);
                }

                // Pinned release: resolve the explicitly requested Release MeshNode
                // and load configs from its AssemblyPath instead of the latest. Lets
                // production hosts pin a specific historical release while authors
                // keep iterating in the dev loop. See
                // NodeTypeDefinition.RequestedReleasePath.
                if (!string.IsNullOrEmpty(def.RequestedReleasePath))
                {
                    var requestedReleasePath = def.RequestedReleasePath!;
                    logger?.LogDebug(
                        "GetCompilationPathRequest at {HubPath}: resolving pinned release {ReleasePath}.",
                        hubPath, requestedReleasePath);
                    return hub.GetMeshNode(requestedReleasePath, TimeSpan.FromSeconds(15))
                        .SelectMany(releaseNode =>
                        {
                            if (releaseNode?.Content is NodeTypeRelease release
                                && !string.IsNullOrEmpty(release.AssemblyPath)
                                && File.Exists(release.AssemblyPath))
                            {
                                logger?.LogDebug(
                                    "GetCompilationPathRequest at {HubPath}: pinned release {ReleasePath} → {AssemblyPath}.",
                                    hubPath, requestedReleasePath, release.AssemblyPath);
                                var pinnedNode = node with { AssemblyLocation = release.AssemblyPath };
                                return compilationService.GetConfigurationsFromExistingAssembly(pinnedNode)
                                    .Select(result => BuildResponse(hubPath, pinnedNode, result));
                            }
                            logger?.LogWarning(
                                "GetCompilationPathRequest at {HubPath}: pinned release {ReleasePath} could not be resolved.",
                                hubPath, requestedReleasePath);
                            return Observable.Return<GetCompilationPathResponse?>(Fail(
                                null,
                                $"Pinned release '{requestedReleasePath}' for '{hubPath}' could not be resolved."));
                        });
                }

                // Short-circuit: if the NodeType already has a compiled assembly
                // (set by the compile watcher on the previous successful compile),
                // just load configs from that ALC — no Roslyn round-trip needed.
                if (!string.IsNullOrEmpty(node.AssemblyLocation)
                    && (node.AssemblyLocation.StartsWith("memory://", StringComparison.Ordinal)
                        || File.Exists(node.AssemblyLocation)))
                {
                    logger?.LogDebug(
                        "GetCompilationPathRequest at {HubPath}: using existing assembly at {AssemblyLocation}.",
                        hubPath, node.AssemblyLocation);
                    return compilationService.GetConfigurationsFromExistingAssembly(node)
                        .Select(result => BuildResponse(hubPath, node, result));
                }

                return compilationService.CompileAndGetConfigurations(node)
                    .Select(result => BuildResponse(hubPath, node, result));
            })
            .Subscribe(
                response => hub.Post(response!, o => o.ResponseFor(request)),
                ex =>
                {
                    logger?.LogWarning(ex,
                        "GetCompilationPathRequest at {HubPath}: resolution faulted.", hubPath);
                    hub.Post(Fail(null, ex.Message), o => o.ResponseFor(request));
                });

        return request.Processed();
    }

    private static GetCompilationPathResponse BuildResponse(
        string hubPath, MeshNode node, NodeCompilationResult? result)
    {
        if (result == null)
            return Fail(null, $"Compilation pipeline returned no result for '{hubPath}'.");

        var status = result.Log?.Status ?? ActivityStatus.Succeeded;
        var faulted = status == ActivityStatus.Failed;

        if (faulted || string.IsNullOrEmpty(result.AssemblyLocation))
        {
            var errors = result.Log?.Errors() ?? [];
            var warnings = result.Log?.Warnings() ?? [];
            var summary = errors.Count > 0
                ? string.Join("; ", errors.Select(m => m.Message))
                : warnings.Count > 0
                    ? string.Join("; ", warnings.Select(m => m.Message))
                    : $"Compilation faulted for '{hubPath}'.";
            return Fail(null, summary, result.Log);
        }

        var matchingConfig = result.NodeTypeConfigurations
            .FirstOrDefault(c => string.Equals(c.NodeType, hubPath, StringComparison.OrdinalIgnoreCase))
            ?? result.NodeTypeConfigurations.FirstOrDefault();

        return new GetCompilationPathResponse(
            Success: true,
            AssemblyLocation: result.AssemblyLocation,
            Collection: null,
            Version: node.Version.ToString(),
            Error: null,
            HubConfiguration: matchingConfig?.HubConfiguration,
            Log: result.Log);
    }

    private static GetCompilationPathResponse Fail(string? version, string error, ActivityLog? log = null) =>
        new(Success: false,
            AssemblyLocation: null,
            Collection: null,
            Version: version,
            Error: error,
            HubConfiguration: null,
            Log: log);
}
