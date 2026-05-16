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
                // and load configs from its assembly bytes instead of the latest.
                // Read the Release node via workspace.GetMeshNodeStream(releasePath)
                // — auto-dispatches own/local/remote — then hydrate the assembly
                // through IAssemblyStore.TryGetAssemblyPath. Symmetric with
                // NodeTypeEnrichmentHelpers' pinned-release branch.
                if (!string.IsNullOrEmpty(def.RequestedReleasePath))
                {
                    var requestedReleasePath = def.RequestedReleasePath!;
                    logger?.LogDebug(
                        "GetCompilationPathRequest at {HubPath}: resolving pinned release {ReleasePath}.",
                        hubPath, requestedReleasePath);
                    return hub.GetWorkspace().GetMeshNodeStream(requestedReleasePath)
                        .Take(1)
                        .SelectMany(releaseNode =>
                        {
                            if (releaseNode?.Content is not NodeTypeRelease release)
                            {
                                logger?.LogWarning(
                                    "GetCompilationPathRequest at {HubPath}: pinned release {ReleasePath} could not be resolved (releaseNode={ReleaseNode}).",
                                    hubPath, requestedReleasePath,
                                    releaseNode == null ? "null" : releaseNode.Content?.GetType().Name ?? "no-content");
                                return Observable.Return<GetCompilationPathResponse?>(Fail(
                                    null,
                                    $"Pinned release '{requestedReleasePath}' for '{hubPath}' could not be resolved."));
                            }
                            var releaseVersion = TryParseReleaseVersion(release.Version);
                            return ResolveAssembly(hub, release.AssemblyCollection, release.NodeTypePath, releaseVersion)
                                .SelectMany(localPath =>
                                {
                                    if (string.IsNullOrEmpty(localPath))
                                    {
                                        logger?.LogWarning(
                                            "GetCompilationPathRequest at {HubPath}: pinned release {ReleasePath} bytes not found in store (collection={Coll}, version={Version}).",
                                            hubPath, requestedReleasePath, release.AssemblyCollection, releaseVersion);
                                        return Observable.Return<GetCompilationPathResponse?>(Fail(
                                            null,
                                            $"Pinned release '{requestedReleasePath}' assembly not found in store."));
                                    }
                                    logger?.LogDebug(
                                        "GetCompilationPathRequest at {HubPath}: pinned release {ReleasePath} → {LocalPath}.",
                                        hubPath, requestedReleasePath, localPath);
                                    return compilationService.GetConfigurationsFromExistingAssembly(localPath!, hubPath)
                                        .Select(result => BuildResponseFromLocal(
                                            hubPath, node, localPath!, release.AssemblyCollection,
                                            release.AssemblyContentPath, result));
                                });
                        });
                }

                // Short-circuit: if a settled compile is published (status=Ok +
                // LatestAssembly{Collection,Path} set), hydrate via IAssemblyStore.
                // Gating on LatestReleasePath OR the cross-silo durable assembly
                // fields ensures we don't load the framework DLL by mistake (the
                // pre-Stage-0 failure mode behind CompileFailsWhenSourceCodeIsInvalid).
                var hasPublishedRelease = !string.IsNullOrEmpty(def.LatestReleasePath);
                if (hasPublishedRelease
                    && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
                    && !string.IsNullOrEmpty(def.LatestAssemblyPath))
                {
                    var compileVersion = def.LastCompiledVersion ?? node.Version;
                    return ResolveAssembly(hub, def.LatestAssemblyCollection, node.Path, compileVersion)
                        .SelectMany(localPath =>
                        {
                            if (string.IsNullOrEmpty(localPath))
                            {
                                logger?.LogDebug(
                                    "GetCompilationPathRequest at {HubPath}: latest assembly not in store (collection={Coll}, version={Version}) — falling back to fresh compile.",
                                    hubPath, def.LatestAssemblyCollection, compileVersion);
                                return compilationService.CompileAndGetConfigurations(node)
                                    .Select(result => BuildResponse(hubPath, node, result));
                            }
                            logger?.LogDebug(
                                "GetCompilationPathRequest at {HubPath}: using existing assembly at {LocalPath}.",
                                hubPath, localPath);
                            return compilationService.GetConfigurationsFromExistingAssembly(localPath!, hubPath)
                                .Select(result => BuildResponseFromLocal(
                                    hubPath, node, localPath!, def.LatestAssemblyCollection,
                                    def.LatestAssemblyPath, result));
                        });
                }

                return compilationService.CompileAndGetConfigurations(node)
                    .Select(result => BuildResponse(hubPath, node, result));
            })
            .SelectMany(response =>
            {
                // Write compile state back to the MeshNode FIRST, then post the
                // response. Sequencing matters: callers that bridge
                // GetCompilationPathRequest → Get the MeshNode immediately must
                // see the post-compile state. Previously this was fire-and-forget
                // — the response was posted before the .Update landed, leaving
                // a race window where the MeshNode still showed Pending/null.
                IObservable<GetCompilationPathResponse> writeBack;
                try
                {
                    writeBack = hub.GetWorkspace().GetMeshNodeStream().Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def)
                            return curr;
                        if (response!.Success && !string.IsNullOrEmpty(response.AssemblyLocation))
                            return curr with
                            {
                                Content = def with
                                {
                                    CompilationStatus = CompilationStatus.Ok,
                                    CompilationError = null,
                                    LastCompileSucceededAt = DateTimeOffset.UtcNow,
                                    // Cross-silo durable assembly reference from the
                                    // response (set by CompileAndGetConfigurations'
                                    // IAssemblyStore upload, or by the
                                    // BuildResponseFromLocal short-circuit). Falls back
                                    // to the previous values when the producer did not
                                    // populate them (Null store).
                                    LatestAssemblyCollection = response.Collection ?? def.LatestAssemblyCollection,
                                    LatestAssemblyPath = response.ContentPath ?? def.LatestAssemblyPath,
                                    LastCompiledVersion = curr.Version
                                }
                            };
                        return curr with
                        {
                            Content = def with
                            {
                                CompilationStatus = CompilationStatus.Error,
                                CompilationError = response.Error ?? "Compilation failed"
                            }
                        };
                    })
                    .Take(1)
                    .Select(_ => response!)
                    .Catch<GetCompilationPathResponse, Exception>(updEx =>
                    {
                        logger?.LogDebug(updEx,
                            "GetCompilationPathRequest at {HubPath}: failed to write-back compile state",
                            hubPath);
                        return Observable.Return(response!);
                    });
                }
                catch (Exception writeEx)
                {
                    logger?.LogDebug(writeEx,
                        "GetCompilationPathRequest at {HubPath}: write-back faulted",
                        hubPath);
                    writeBack = Observable.Return(response!);
                }
                return writeBack;
            })
            .Subscribe(
                response => hub.Post(response, o => o.ResponseFor(request)),
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
            Collection: result.Collection,
            Version: (result.Version ?? node.Version).ToString(),
            Error: null,
            HubConfiguration: matchingConfig?.HubConfiguration,
            Log: result.Log)
        {
            ContentPath = result.ContentPath
        };
    }

    /// <summary>
    /// Variant of <see cref="BuildResponse"/> for the pinned-release /
    /// latest-compile short-circuit paths: the caller already resolved the
    /// local DLL path via <see cref="IAssemblyStore"/>, so we don't have a
    /// fresh <see cref="NodeCompilationResult"/> shape with Collection/ContentPath
    /// — those come from the persisted reference fields on the Release /
    /// NodeTypeDefinition. The local path is what <c>MessageHubGrain</c> still
    /// needs to <c>Assembly.LoadFrom</c> during Stage 1.
    /// </summary>
    private static GetCompilationPathResponse BuildResponseFromLocal(
        string hubPath,
        MeshNode node,
        string localPath,
        string? collection,
        string? contentPath,
        NodeCompilationResult? result)
    {
        if (result == null)
            return Fail(null, $"Compilation pipeline returned no result for '{hubPath}'.");

        var status = result.Log?.Status ?? ActivityStatus.Succeeded;
        if (status == ActivityStatus.Failed)
        {
            var errors = result.Log?.Errors() ?? [];
            var summary = errors.Count > 0
                ? string.Join("; ", errors.Select(m => m.Message))
                : $"Compilation faulted for '{hubPath}'.";
            return Fail(null, summary, result.Log);
        }

        var matchingConfig = result.NodeTypeConfigurations
            .FirstOrDefault(c => string.Equals(c.NodeType, hubPath, StringComparison.OrdinalIgnoreCase))
            ?? result.NodeTypeConfigurations.FirstOrDefault();

        return new GetCompilationPathResponse(
            Success: true,
            AssemblyLocation: localPath,
            Collection: collection,
            Version: node.Version.ToString(),
            Error: null,
            HubConfiguration: matchingConfig?.HubConfiguration,
            Log: result.Log)
        {
            ContentPath = contentPath
        };
    }

    private static GetCompilationPathResponse Fail(string? version, string error, ActivityLog? log = null) =>
        new(Success: false,
            AssemblyLocation: null,
            Collection: null,
            Version: version,
            Error: error,
            HubConfiguration: null,
            Log: log);

    /// <summary>
    /// Dispatches an <see cref="IAssemblyStore"/> lookup. Sentinel collection
    /// <c>"framework"</c> routes to <see cref="FrameworkAssemblyStore"/>; any
    /// other value routes to the registered <see cref="IAssemblyStore"/>.
    /// </summary>
    private static IObservable<string?> ResolveAssembly(
        IMessageHub hub, string? collection, string nodeTypePath, long version)
    {
        if (string.IsNullOrEmpty(collection)) return Observable.Return<string?>(null);
        var store = string.Equals(collection, FrameworkAssemblyStore.CollectionName, StringComparison.Ordinal)
            ? (IAssemblyStore)FrameworkAssemblyStore.Instance
            : hub.ServiceProvider.GetService<IAssemblyStore>() ?? NullAssemblyStore.Instance;
        return store.TryGetAssemblyPath(nodeTypePath, version);
    }

    /// <summary>
    /// Parses the leading <c>yyyyMMddHHmmss</c> timestamp from a Release version
    /// (<c>{yyyyMMddHHmmss}-{8hash}</c>) into a long usable as the store version
    /// key. Returns 0 when the version string does not match — store miss
    /// falls through to the fail path.
    /// </summary>
    private static long TryParseReleaseVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return 0;
        var dash = version.IndexOf('-');
        var head = dash > 0 ? version[..dash] : version;
        return long.TryParse(head, out var v) ? v : 0;
    }
}
