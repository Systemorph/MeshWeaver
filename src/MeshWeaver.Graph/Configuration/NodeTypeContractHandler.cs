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

        // Reactive resolution — handler stays sync, response is posted from inside the
        // Subscribe(onNext) callback. NEVER await hub round-trips inside Resolve* — see
        // Doc/Architecture/AsynchronousCalls.md.
        Resolve(hub, requestedVersion, hubPath)
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

    private static IObservable<GetCompilationPathResponse> Resolve(
        IMessageHub hub, string? requestedVersion, string hubPath)
    {
        // Step 1: get the type-def MeshNode at the requested version (or HEAD).
        // HEAD: own workspace stream. Historical: IVersionQuery (file-system / store read,
        // no hub round-trip). NEVER await; compose with .Select / .SelectMany.
        IObservable<(MeshNode? typeDef, string? resolvedVersion, string? earlyError)> typeDefSource;

        if (string.IsNullOrEmpty(requestedVersion))
        {
            typeDefSource = hub.GetWorkspace().GetMeshNodeStream()
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(15))
                .Select(typeDef => (typeDef, typeDef?.Version.ToString(), (string?)null))
                .Catch<(MeshNode?, string?, string?), TimeoutException>(_ => Observable.Return<(MeshNode?, string?, string?)>(
                    (null, requestedVersion, $"Timed out reading MeshNode at '{hubPath}' from workspace stream.")));
        }
        else
        {
            var versionQuery = hub.ServiceProvider.GetService<IVersionQuery>();
            if (versionQuery == null)
                return Observable.Return(Fail(requestedVersion,
                    "IVersionQuery is not registered — cannot load historical NodeType snapshots."));

            if (!long.TryParse(requestedVersion, out var versionNumber))
                return Observable.Return(Fail(requestedVersion,
                    $"Version '{requestedVersion}' is not a valid long."));

            // IVersionQuery.GetVersionAsync is store I/O (no hub round-trip) — bridging via
            // Observable.FromAsync is safe here.
            typeDefSource = Observable.FromAsync(ct => versionQuery.GetVersionAsync(
                hubPath, versionNumber, hub.JsonSerializerOptions, ct))
                .Select(typeDef => (typeDef, requestedVersion, (string?)null));
        }

        return typeDefSource.SelectMany(t =>
        {
            if (t.earlyError != null)
                return Observable.Return(Fail(t.resolvedVersion, t.earlyError));

            if (t.typeDef == null)
                return Observable.Return(Fail(t.resolvedVersion,
                    $"NodeType definition not found at path '{hubPath}'"
                    + (string.IsNullOrEmpty(requestedVersion) ? "." : $" at version '{requestedVersion}'.")));

            // Step 2: short-circuit if AssemblyLocation + HubConfiguration are already set
            // (the static-provider / AddMeshNodes case).
            if (!string.IsNullOrEmpty(t.typeDef.AssemblyLocation) && t.typeDef.HubConfiguration != null)
            {
                return Observable.Return(new GetCompilationPathResponse(
                    Success: true,
                    AssemblyLocation: t.typeDef.AssemblyLocation,
                    Collection: null,
                    Version: t.resolvedVersion,
                    Error: null,
                    HubConfiguration: t.typeDef.HubConfiguration));
            }

            // Step 3: dynamic compile path.
            if (t.typeDef.Content is not NodeTypeDefinition)
            {
                return Observable.Return(Fail(t.resolvedVersion,
                    $"Node at '{hubPath}' is not a valid NodeType definition "
                    + $"(Content type: {t.typeDef.Content?.GetType().Name ?? "null"})."));
            }

            var compilationService = hub.ServiceProvider.GetService<IMeshNodeCompilationService>();
            if (compilationService == null)
            {
                return Observable.Return(Fail(t.resolvedVersion,
                    $"No IMeshNodeCompilationService registered — cannot compile '{hubPath}'."));
            }

            return compilationService.CompileAndGetConfigurations(t.typeDef)
                .Select(result =>
                {
                    if (result == null || string.IsNullOrEmpty(result.AssemblyLocation))
                        return Fail(t.resolvedVersion,
                            $"Compilation produced no assembly location for '{hubPath}'.");

                    var matchingConfig = result.NodeTypeConfigurations
                        .FirstOrDefault(c => string.Equals(c.NodeType, hubPath, StringComparison.OrdinalIgnoreCase))
                        ?? result.NodeTypeConfigurations.FirstOrDefault();

                    return new GetCompilationPathResponse(
                        Success: true,
                        AssemblyLocation: result.AssemblyLocation,
                        Collection: null,
                        Version: t.resolvedVersion,
                        Error: null,
                        HubConfiguration: matchingConfig?.HubConfiguration);
                });
        });
    }

    private static GetCompilationPathResponse Fail(string? version, string error) =>
        new(Success: false,
            AssemblyLocation: null,
            Collection: null,
            Version: version,
            Error: error,
            HubConfiguration: null);
}
