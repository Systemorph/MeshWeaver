using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Per-NodeType-hub handler for <see cref="GetCompilationPathRequest"/>. Owns the
/// per-version compile cache for the NodeType this hub is responsible for.
///
/// <para>
/// Lookup order for a request with <c>Version == null</c>:
/// <list type="number">
///   <item>HEAD MeshNode read via <c>workspace.GetMeshNodeStream().FirstAsync()</c>.</item>
///   <item>If the MeshNode already carries <see cref="MeshNode.AssemblyLocation"/> +
///     <see cref="MeshNode.HubConfiguration"/> (the static-provider /
///     <c>AddMeshNodes</c> case) → respond immediately, cache by HEAD version.</item>
///   <item>Else if <see cref="MeshNode.Content"/> is a <see cref="NodeTypeDefinition"/>
///     → invoke <see cref="IMeshNodeCompilationService.CompileAndGetConfigurationsAsync"/>,
///     cache the resulting (assembly, hubConfig) tuple, respond.</item>
///   <item>Else → respond <c>Success = false</c> so the consumer can fall back.</item>
/// </list>
/// </para>
///
/// <para>
/// For a request with a non-null <c>Version</c> the handler short-circuits on the
/// per-version cache; on miss it loads the historical MeshNode via
/// <see cref="IVersionQuery.GetVersionAsync"/> and feeds it through the same compile
/// step. Source-subtree history is not currently re-resolved — for older snapshots the
/// historical type-def's compilation uses live source nodes; full historical source
/// resolution is a follow-up.
/// </para>
///
/// <para>
/// Handler is sync (returns <see cref="IMessageDelivery"/>). Compilation + version-history
/// reads are wrapped in <c>Observable.FromAsync</c> and the response is posted from
/// inside the <c>.Subscribe(onNext)</c> callback. No <c>await</c> in the handler body —
/// see <c>Doc/Architecture/AsynchronousCalls.md</c>.
/// </para>
/// </summary>
internal static class NodeTypeContractHandler
{
    /// <summary>
    /// Per-hub compile cache, keyed by the resolved version string. Lives on the hub's
    /// configuration via <see cref="MessageHubConfigurationExtensions.Set{T}"/> /
    /// <see cref="MessageHubConfigurationExtensions.Get{T}"/> so it's naturally bounded by
    /// the hub's lifetime and reset across grain re-activations.
    /// </summary>
    private sealed class CompilationPathCache
    {
        public readonly ConcurrentDictionary<string, GetCompilationPathResponse> Entries = new();
    }

    private static CompilationPathCache GetOrCreateCache(IMessageHub hub)
    {
        // Cache lives on the hub via a singleton DI service so it survives multiple
        // requests within the hub's lifetime but is GC'd when the hub is torn down.
        var cache = hub.ServiceProvider.GetService<CompilationPathCache>();
        if (cache != null)
            return cache;

        // Fall back to a hub-local static dictionary keyed by hub address — works in
        // process. The point is to share entries across requests on the same hub.
        return _byHubAddress.GetOrAdd(hub.Address.ToString(), _ => new CompilationPathCache());
    }

    private static readonly ConcurrentDictionary<string, CompilationPathCache> _byHubAddress = new();

    /// <summary>
    /// Handles <see cref="GetCompilationPathRequest"/> for the NodeType owned by this hub.
    /// </summary>
    public static IMessageDelivery Handle(
        IMessageHub hub,
        IMessageDelivery<GetCompilationPathRequest> request)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeTypeContractHandler");
        var requestedVersion = request.Message.Version;
        var hubPath = hub.Address.ToString();
        var cache = GetOrCreateCache(hub);

        // Cache hit (version-keyed). For Version == null we cache under "" until we know
        // HEAD's actual version — overwritten with the resolved version below.
        var cacheKey = requestedVersion ?? string.Empty;
        if (cache.Entries.TryGetValue(cacheKey, out var hit))
        {
            hub.Post(hit, o => o.ResponseFor(request));
            return request.Processed();
        }

        // Async work wrapped in Observable.FromAsync — handler stays sync, response is
        // posted from inside the Subscribe(onNext) callback.
        Observable.FromAsync(ct => ResolveAsync(hub, requestedVersion, hubPath, ct))
            .Subscribe(
                response =>
                {
                    try
                    {
                        if (response.Success)
                        {
                            cache.Entries[cacheKey] = response;
                            // Also cache under the resolved version when we now know it.
                            if (!string.IsNullOrEmpty(response.Version)
                                && !string.Equals(cacheKey, response.Version, StringComparison.Ordinal))
                            {
                                cache.Entries[response.Version!] = response;
                            }
                        }
                        hub.Post(response, o => o.ResponseFor(request));
                    }
                    catch (Exception postEx)
                    {
                        logger?.LogWarning(postEx,
                            "GetCompilationPathRequest: failed to post response for {Path}", hubPath);
                    }
                },
                ex =>
                {
                    logger?.LogWarning(ex,
                        "GetCompilationPathRequest: resolution failed for {Path}", hubPath);
                    hub.Post(
                        new GetCompilationPathResponse(
                            Success: false,
                            AssemblyLocation: null,
                            Collection: null,
                            Version: requestedVersion,
                            Error: ex.Message,
                            HubConfiguration: null),
                        o => o.ResponseFor(request));
                });

        return request.Processed();
    }

    private static async Task<GetCompilationPathResponse> ResolveAsync(
        IMessageHub hub, string? requestedVersion, string hubPath, CancellationToken ct)
    {
        // Step 1: get the type-def MeshNode at the requested version (or HEAD).
        MeshNode? typeDefNode;
        string? resolvedVersion = requestedVersion;

        if (string.IsNullOrEmpty(requestedVersion))
        {
            // HEAD — read via the hub's own workspace stream. Bounded with Take(1) +
            // Timeout because the hub initialises this stream synchronously from
            // its data source (static-provider node OR persistence).
            try
            {
                typeDefNode = await hub.GetWorkspace().GetMeshNodeStream()
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(15))
                    .ToTask(ct);
            }
            catch (TimeoutException)
            {
                return Fail(requestedVersion,
                    $"Timed out reading MeshNode at '{hubPath}' from workspace stream.");
            }

            resolvedVersion = typeDefNode?.Version.ToString();
        }
        else
        {
            // Historical version — IVersionQuery.
            var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
            if (versionQuery == null)
                return Fail(requestedVersion,
                    "IVersionQuery is not registered — cannot load historical NodeType snapshots.");

            if (!long.TryParse(requestedVersion, out var versionNumber))
                return Fail(requestedVersion,
                    $"Version '{requestedVersion}' is not a valid long.");

            typeDefNode = await versionQuery.GetVersionAsync(hubPath, versionNumber,
                hub.JsonSerializerOptions, ct);
        }

        if (typeDefNode == null)
            return Fail(resolvedVersion,
                $"NodeType definition not found at path '{hubPath}'"
                + (string.IsNullOrEmpty(requestedVersion) ? "." : $" at version '{requestedVersion}'."));

        // Step 2: short-circuit if AssemblyLocation + HubConfiguration are already set
        // (the static-provider / AddMeshNodes case).
        if (!string.IsNullOrEmpty(typeDefNode.AssemblyLocation) && typeDefNode.HubConfiguration != null)
        {
            return new GetCompilationPathResponse(
                Success: true,
                AssemblyLocation: typeDefNode.AssemblyLocation,
                Collection: null,
                Version: resolvedVersion,
                Error: null,
                HubConfiguration: typeDefNode.HubConfiguration);
        }

        // Step 3: dynamic compile path. Requires a NodeTypeDefinition Content payload
        // and a registered IMeshNodeCompilationService.
        if (typeDefNode.Content is not NodeTypeDefinition)
        {
            return Fail(resolvedVersion,
                $"Node at '{hubPath}' is not a valid NodeType definition "
                + $"(Content type: {typeDefNode.Content?.GetType().Name ?? "null"}).");
        }

        var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (compilationService == null)
        {
            return Fail(resolvedVersion,
                $"No IMeshNodeCompilationService registered — cannot compile '{hubPath}'.");
        }

        var result = await compilationService.CompileAndGetConfigurationsAsync(typeDefNode, ct);
        if (result == null || string.IsNullOrEmpty(result.AssemblyLocation))
        {
            return Fail(resolvedVersion,
                $"Compilation produced no assembly location for '{hubPath}'.");
        }

        // Pick the HubConfiguration matching this NodeType's path; fall back to the
        // first available one (the typical case is a single-attribute NodeType).
        var matchingConfig = result.NodeTypeConfigurations
            .FirstOrDefault(c => string.Equals(c.NodeType, hubPath, StringComparison.OrdinalIgnoreCase))
            ?? result.NodeTypeConfigurations.FirstOrDefault();

        return new GetCompilationPathResponse(
            Success: true,
            AssemblyLocation: result.AssemblyLocation,
            Collection: null,
            Version: resolvedVersion,
            Error: null,
            HubConfiguration: matchingConfig?.HubConfiguration);
    }

    private static GetCompilationPathResponse Fail(string? version, string error) =>
        new(Success: false,
            AssemblyLocation: null,
            Collection: null,
            Version: version,
            Error: error,
            HubConfiguration: null);
}
