using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service for managing NodeType data with thread-safe caching.
/// Uses release-based compilation caching for multi-process safety.
/// Each unique compilation input produces a deterministic release folder that is never modified once created.
/// </summary>
internal class NodeTypeService : INodeTypeService, IDisposable
{
    private readonly IMessageHub hub;
    private readonly IEnumerable<IMeshQueryProvider> queryProviders;
    private readonly IMeshStorage meshStorage;
    private readonly ILogger<NodeTypeService> logger;
    private readonly MeshNodeCompilationService? compilationService;
    private readonly MeshConfiguration meshConfiguration;
    private readonly ICompilationCacheService cacheService;
    private readonly CompilationCacheOptions cacheOptions;
    private readonly IAssemblyStore assemblyStore;

    // Compilation tasks by nodeTypePath - uses Task (not Lazy<Task>) to allow retry on failure
    private readonly ConcurrentDictionary<string, Task<NodeTypeCacheEntry?>> _compilationTasks = new();

    // Release keys by nodeTypePath - tracks which release is currently loaded
    private readonly ConcurrentDictionary<string, string> _releaseKeys = new();

    // Stream subscriptions for cache invalidation
    private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new();

    // Cached HubConfiguration functions for fast synchronous access
    private readonly ConcurrentDictionary<string, Func<MessageHubConfiguration, MessageHubConfiguration>> _hubConfigurations = new();

    // Cached CreatableTypesRules extracted from hub configurations (defines what can be created FROM this type)
    private readonly ConcurrentDictionary<string, CreatableTypesRules> _creatableTypesRules = new();

    // Cached NotCreatable markers (types that cannot be created via UI)
    private readonly ConcurrentDictionary<string, bool> _notCreatableTypes = new();

    // Compilation errors by nodeTypePath - tracks last compilation failure for error reporting
    private readonly ConcurrentDictionary<string, string> _compilationErrors = new();

    // NodeType paths whose compilation is currently running. Populated the moment a compile
    // task is kicked off; cleaned up when it finishes (success OR failure). Used by
    // GetDiagnostics / progress overlays so callers can show "Compiling…" while they wait.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _compilingInProgress = new();

    /// <summary>
    /// Timestamp of the last successful compile per NodeType path. Set when a compile
    /// finishes without errors; cleared by <see cref="InvalidateCache"/> and by a new
    /// compile failure. Distinguishes "compiled cleanly at least once" (status = Ok)
    /// from "no compile has run since invalidation" (status = Unknown).
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _compilationSucceededAt = new();

    // Cached access rules extracted from hub configurations
    private readonly ConcurrentDictionary<string, INodeTypeAccessRule> _accessRules = new();

    // Subscription to the cross-silo change feed — disposed with the service.
    private readonly IDisposable? _changeFeedSubscription;

    public NodeTypeService(
        IMessageHub hub,
        IEnumerable<IMeshQueryProvider> queryProviders,
        MeshConfiguration meshConfiguration,
        IMeshStorage meshStorage,
        ILogger<NodeTypeService> logger,
        ICompilationCacheService cacheService,
        IOptions<CompilationCacheOptions> cacheOptions,
        MeshNodeCompilationService? compilationService = null,
        IMeshChangeFeed? changeFeed = null,
        IAssemblyStore? assemblyStore = null)
    {
        this.hub = hub;
        this.queryProviders = queryProviders;
        this.meshStorage = meshStorage;
        this.meshConfiguration = meshConfiguration;
        this.logger = logger;
        this.cacheService = cacheService;
        this.cacheOptions = cacheOptions.Value;
        this.compilationService = compilationService;
        // Optional store: when present, compiled bytes are persisted and served from the
        // content-addressed (well, version-addressed) shared cache; when absent, fall back
        // to the legacy per-replica in-memory compile cache. DI registers a concrete
        // store via AddFileSystemAssemblyStore / AddBlobAssemblyStore — consumers that
        // don't register one keep the old behaviour.
        this.assemblyStore = assemblyStore ?? NullAssemblyStore.Instance;

        // Initialize cache from pre-registered nodes in MeshConfiguration
        InitializeFromMeshConfiguration();

        // Subscribe to the mesh change feed so cache invalidations reach every silo.
        // Defensive: wrap in try/catch because a construction-time throw here would
        // take down *every* silo's DI and deadlock the whole cluster — the feed impl
        // might not be ready, might throw on early subscription, etc. Log and move on.
        if (changeFeed != null)
        {
            try
            {
                _changeFeedSubscription = changeFeed.Subscribe(evt =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(evt.Path)) return;

                        // Direct match: the changed node IS a NodeType (or we already
                        // track its path for other reasons).
                        if (_hubConfigurations.ContainsKey(evt.Path)
                            || _compilationTasks.ContainsKey(evt.Path)
                            || _compilationErrors.ContainsKey(evt.Path)
                            || string.Equals(evt.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
                        {
                            logger.LogInformation(
                                "Cross-silo cache invalidation for {NodeTypePath} via MeshChangeFeed ({Kind})",
                                evt.Path, evt.Kind);
                            InvalidateCache(evt.Path);
                            return;
                        }

                        // Owning-NodeType match: the changed node lives under a NodeType's
                        // Source/ folder (convention: {NodeTypePath}/Source/{File}). Updates
                        // to these Code pieces change what the NodeType compiles to, so the
                        // owning NodeType's cache (in-memory + on-disk DLL) must be flushed —
                        // otherwise the stale DLL keeps being served because the NodeType's
                        // own LastModified hasn't moved.
                        var owning = TryResolveOwningNodeTypePath(evt.Path);
                        if (owning != null)
                        {
                            logger.LogInformation(
                                "Cross-silo cache invalidation for owning {NodeTypePath} after source change at {SourcePath} ({Kind})",
                                owning, evt.Path, evt.Kind);
                            InvalidateCache(owning);
                        }
                    }
                    catch (Exception handlerEx)
                    {
                        logger.LogWarning(handlerEx,
                            "MeshChangeFeed handler faulted while processing event for {Path}",
                            evt.Path);
                    }
                });
            }
            catch (Exception subscribeEx)
            {
                logger.LogWarning(subscribeEx,
                    "Failed to subscribe to IMeshChangeFeed — cross-silo cache invalidation disabled");
            }
        }
    }

    /// <summary>
    /// Initializes the HubConfiguration cache from pre-registered mesh nodes.
    /// </summary>
    private void InitializeFromMeshConfiguration()
    {
        foreach (var node in meshConfiguration.Nodes.Values)
        {
            if (node.HubConfiguration == null)
                continue;

            CacheHubConfiguration(node.Path, node.HubConfiguration);
        }
    }

    /// <summary>
    /// Queries all providers and returns typed results. Used instead of IMeshService
    /// to avoid circular dependency (NodeTypeService must not depend on IMeshService).
    /// </summary>
    private async IAsyncEnumerable<T> QueryAsync<T>(string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = MeshQueryRequest.FromQuery(query);
        var options = hub.JsonSerializerOptions;
        foreach (var provider in queryProviders)
        {
            await foreach (var item in provider.QueryAsync(request, options, ct))
            {
                if (item is T typed)
                    yield return typed;
            }
        }
    }

    /// <summary>
    /// Caches a hub configuration and extracts CreatableTypesRules and ContentType.
    /// </summary>
    private void CacheHubConfiguration(string nodeTypePath, Func<MessageHubConfiguration, MessageHubConfiguration> hubConfig)
    {
        _hubConfigurations[nodeTypePath] = hubConfig;
        logger.LogDebug("Cached HubConfiguration for {Path}", nodeTypePath);

        // Rules (CreatableTypesRules, NotCreatable, AccessRules) are extracted
        // when the hub is actually created — not here. We only cache the lambda.
    }

    /// <summary>
    /// Gets the cached CreatableTypesRules for a node type.
    /// </summary>
    public CreatableTypesRules? GetCreatableTypesRules(string nodeTypePath)
    {
        return _creatableTypesRules.GetValueOrDefault(nodeTypePath);
    }

    /// <summary>
    /// Gets the access rule extracted from the hub configuration for a node type.
    /// </summary>
    public INodeTypeAccessRule? GetAccessRule(string nodeTypePath)
    {
        return _accessRules.GetValueOrDefault(nodeTypePath);
    }

    /// <summary>
    /// Checks if a type is marked as not creatable.
    /// </summary>
    public bool IsNotCreatable(string nodeTypePath)
    {
        return _notCreatableTypes.GetValueOrDefault(nodeTypePath);
    }

    /// <summary>
    /// Gets the last compilation error for a node type, if any.
    /// </summary>
    public string? GetCompilationError(string nodeTypePath)
    {
        return _compilationErrors.GetValueOrDefault(nodeTypePath);
    }

    /// <summary>
    /// Returns <c>true</c> if compilation for the given NodeType path is currently running
    /// (started but not yet completed). Use this to render a "Compiling…" progress overlay
    /// so the user sees activity instead of a blank layout while they wait.
    /// </summary>
    public bool IsCompiling(string nodeTypePath) =>
        _compilingInProgress.ContainsKey(nodeTypePath);

    /// <summary>
    /// When compilation for <paramref name="nodeTypePath"/> is running, returns when it
    /// started (UTC); otherwise <c>null</c>. Consumers can display the elapsed time in a
    /// progress overlay.
    /// </summary>
    public DateTimeOffset? GetCompilationStartedAt(string nodeTypePath) =>
        _compilingInProgress.TryGetValue(nodeTypePath, out var start) ? start : null;

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetCompilingPaths() =>
        _compilingInProgress.Keys.ToArray();

    /// <inheritdoc />
    public DateTimeOffset? GetLastSuccessfulCompileAt(string nodeTypePath) =>
        _compilationSucceededAt.TryGetValue(nodeTypePath, out var ts) ? ts : null;

    /// <summary>
    /// Fully-reactive assembly path lookup. The flow:
    /// <list type="number">
    ///   <item>Fetch the current NodeType MeshNode (reactive — wraps the mesh read).</item>
    ///   <item>Ask <see cref="IAssemblyStore"/> for an assembly cached under the node's
    ///     current <see cref="MeshNode.Version"/>.</item>
    ///   <item>On hit — emit the local path straight away; no compile runs.</item>
    ///   <item>On miss — trigger a compile, read the produced DLL/PDB bytes, push them
    ///     into the store, and emit the store's path.</item>
    /// </list>
    /// Per <c>Doc/Architecture/AsynchronousCalls.md</c>: all steps return
    /// <see cref="IObservable{T}"/>; callers must <c>.Subscribe(...)</c>, not <c>await</c>.
    /// </summary>
    public IObservable<string> GetAssemblyPath(string nodeTypePath) =>
        meshStorage.GetNode(nodeTypePath)
            .SelectMany(node =>
            {
                if (node is null)
                {
                    return Observable.Throw<string>(
                        new InvalidOperationException($"NodeType not found at path: {nodeTypePath}"));
                }
                var version = node.Version;
                return assemblyStore.TryGetAssemblyPath(nodeTypePath, version)
                    .SelectMany(cached =>
                    {
                        if (!string.IsNullOrEmpty(cached))
                        {
                            logger.LogDebug(
                                "Assembly cache hit for {NodeTypePath}@v{Version} at {Path}",
                                nodeTypePath, version, cached);
                            return Observable.Return(cached);
                        }
                        // Cache miss: run the existing compile path, then persist the
                        // produced bytes to the shared store so every subsequent lookup
                        // (this replica, other replicas, next restart) gets a hit.
                        return CompileAndStore(nodeTypePath, version);
                    });
            });

    private IObservable<string> CompileAndStore(string nodeTypePath, long version) =>
        Observable.FromAsync(async ct =>
        {
            var compiled = await GetAssemblyPathAsync(nodeTypePath, ct);
            if (string.IsNullOrEmpty(compiled))
                throw new InvalidOperationException(
                    $"Compilation returned no assembly path for {nodeTypePath}");
            return compiled;
        })
        .SelectMany(compiledPath =>
        {
            // If no store is wired (NullAssemblyStore), skip the Put round-trip and
            // just emit the locally-compiled path — same as the pre-refactor behaviour.
            if (assemblyStore is NullAssemblyStore)
                return Observable.Return(compiledPath);

            try
            {
                var dllBytes = File.ReadAllBytes(compiledPath);
                var pdbPath = Path.ChangeExtension(compiledPath, ".pdb");
                var pdbBytes = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
                return assemblyStore.Put(nodeTypePath, version, dllBytes, pdbBytes);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to persist compiled assembly for {NodeTypePath}@v{Version}; " +
                    "returning local path. Next lookup on this replica hits local disk, " +
                    "other replicas will recompile.",
                    nodeTypePath, version);
                return Observable.Return(compiledPath);
            }
        });

    private Task<string?> GetAssemblyPathAsync(string nodeTypePath, CancellationToken ct = default)
    {
        var wasNewCompile = false;
        // Use ConcurrentDictionary.GetOrAdd with a Task to ensure only one compilation runs per key.
        // On failure, remove from dictionary to allow retry on next access.
        var task = _compilationTasks.GetOrAdd(nodeTypePath, path =>
        {
            // Only the first caller to miss the cache kicks off a compile — mark it started.
            wasNewCompile = true;
            _compilingInProgress[path] = DateTimeOffset.UtcNow;
            return CompileWithReleaseAsync(path, ct);
        });

        return task.ContinueWith(t =>
        {
            // Clear the in-progress marker whether we win the race or not; the task we
            // awaited on is finished, so the state ceases to be "running" for this caller.
            if (wasNewCompile)
                _compilingInProgress.TryRemove(nodeTypePath, out _);

            // On failure, remove from cache to allow retry and return null
            if (t.IsFaulted || t.IsCanceled)
            {
                _compilationTasks.TryRemove(nodeTypePath, out _);
                _releaseKeys.TryRemove(nodeTypePath, out _);
                // A new failure supersedes any prior success — clear the success marker so
                // diagnostics flip back to Error instead of reporting stale Ok.
                _compilationSucceededAt.TryRemove(nodeTypePath, out _);
                // Track the compilation error for error reporting in UI
                if (t.Exception?.InnerException is CompilationException compEx)
                    _compilationErrors[nodeTypePath] = compEx.Message;
                else if (t.Exception?.InnerException != null)
                    _compilationErrors[nodeTypePath] = t.Exception.InnerException.Message;
                return null;
            }
            _compilationErrors.TryRemove(nodeTypePath, out _);
            _compilationSucceededAt[nodeTypePath] = DateTimeOffset.UtcNow;
            return t.Result?.AssemblyPath;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <inheritdoc />
    public NodeTypeConfiguration? GetCachedConfiguration(string nodeTypePath)
    {
        // Check if we have a cached HubConfiguration
        if (_hubConfigurations.TryGetValue(nodeTypePath, out var hubConfig))
        {
            // Return a NodeTypeConfiguration with the cached HubConfiguration
            return new NodeTypeConfiguration
            {
                NodeType = nodeTypePath,
                DataType = typeof(object),
                HubConfiguration = hubConfig
            };
        }
        return null;
    }

    /// <summary>
    /// Gets the cached HubConfiguration function for a node type.
    /// Applies the DefaultNodeHubConfiguration from MeshConfiguration if available.
    /// </summary>
    public Func<MessageHubConfiguration, MessageHubConfiguration>? GetCachedHubConfiguration(string nodeTypePath)
    {
        // Return the raw cached config — composition with DefaultNodeHubConfiguration
        // is done by IMeshNodeHubFactory.ResolveHubConfigurationAsync.
        return _hubConfigurations.GetValueOrDefault(nodeTypePath);
    }

    /// <summary>
    /// Invalidates all cached state for <paramref name="nodeTypePath"/>. Public surface so
    /// MCP's Recycle tool (and other front-ends) can flush a stuck NodeType — disposing
    /// the hub alone is not enough, because `_compilationErrors` / `_compilationTasks`
    /// live on this singleton service and survive hub teardown.
    /// </summary>
    public void InvalidateCache(string nodeTypePath)
    {
        logger.LogDebug("Invalidating cache for {NodeTypePath}", nodeTypePath);

        // Remove from all caches — including the sticky error + in-progress markers
        // (previously forgotten, which meant a stuck error kept showing after Recycle).
        _compilationTasks.TryRemove(nodeTypePath, out _);
        _compilationErrors.TryRemove(nodeTypePath, out _);
        _compilingInProgress.TryRemove(nodeTypePath, out _);
        // Clearing the success marker here is what makes GetStatus() flip to Unknown
        // after Recycle instead of lingering on a stale Ok.
        _compilationSucceededAt.TryRemove(nodeTypePath, out _);
        _releaseKeys.TryRemove(nodeTypePath, out _);
        _hubConfigurations.TryRemove(nodeTypePath, out _);
        _creatableTypesRules.TryRemove(nodeTypePath, out _);
        _notCreatableTypes.TryRemove(nodeTypePath, out _);
        _accessRules.TryRemove(nodeTypePath, out _);

        // Path cache removed — EnrichWithNodeType always issues a fresh
        // GetCompilationPathRequest and lets the disk-level cache decide. Nothing
        // to drop here.

        // Also delete the on-disk DLL/PDB/source so the next access forces a fresh
        // compile. Without this, IsCacheValid can still return true when the NodeType's
        // own LastModified hasn't changed (e.g. a Source/ child was edited).
        try
        {
            var nodeName = cacheService.SanitizeNodeName(nodeTypePath);
            cacheService.InvalidateCache(nodeName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate on-disk compilation cache for {NodeTypePath}", nodeTypePath);
        }

        // Dispose subscription (will re-subscribe on next access)
        if (_subscriptions.TryRemove(nodeTypePath, out var subscription))
        {
            subscription.Dispose();
        }
    }

    /// <summary>
    /// Resolves the owning NodeType path for a node whose path contains a "Source"
    /// segment (the established convention for source-code pieces). Returns the parent
    /// of "Source". Example: "Org/MyType/Source/Foo" → "Org/MyType". Legacy
    /// "_Source" paths are recognised for backward compatibility. Returns null if
    /// the path doesn't follow the convention.
    /// </summary>
    private static string? TryResolveOwningNodeTypePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var segments = path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], "Source", StringComparison.Ordinal)
                || string.Equals(segments[i], "_Source", StringComparison.Ordinal))
                return string.Join("/", segments.Take(i));
        }
        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// New shape (2026-04-25): NodeTypeService is a path cache. The lookup itself runs on
    /// the per-NodeType hub via <see cref="GetCompilationPathRequest"/>. NodeTypeService
    /// only caches the (nodeType, version) → (assemblyPath, hubConfig) tuple it gets back.
    ///
    /// <para>
    /// Transitional fallback: when the hub responds <c>Success = false</c> (e.g. for
    /// built-in <c>AddMeshNodes</c> types that aren't yet routable as their own per-node
    /// hub — see plan §5 "AddMeshNodes parity"), we consult <c>meshConfiguration.Nodes</c>
    /// inline so the legacy registration path keeps working. Remove once routing covers
    /// every NodeType address.
    /// </para>
    /// </remarks>
    public IObservable<MeshNode> EnrichWithNodeType(MeshNode node)
    {
        // Already fully enriched? (built-in / static-provider node already has both fields.)
        if (node.HubConfiguration != null && node.AssemblyLocation != null)
            return Observable.Return(node);

        var nodeType = node.NodeType;
        if (string.IsNullOrEmpty(nodeType))
            return Observable.Return(ApplyDefaultConfig(node));

        // SYNC FAST PATH for static NodeType definitions registered via
        // AddMeshNodes: their MeshConfiguration entry carries both
        // HubConfiguration AND AssemblyLocation (the NodeType MeshNode
        // is fully self-describing). Apply them directly — no message
        // round-trip, no GetCompilationPathRequest cycle.
        // <para>This is what breaks the routing → ResolveHub →
        // GetCompilationPathRequest → routing recursion that otherwise
        // fires for every per-instance hub of a static NodeType
        // (Markdown, AccessAssignment, …).</para>
        if (meshConfiguration.Nodes.TryGetValue(nodeType, out var staticTypeNode)
            && staticTypeNode.HubConfiguration != null
            && !string.IsNullOrEmpty(staticTypeNode.AssemblyLocation))
        {
            _hubConfigurations[nodeType] = staticTypeNode.HubConfiguration;
            return Observable.Return(ApplyEntry(
                node,
                new PathCacheEntry(
                    staticTypeNode.AssemblyLocation,
                    Collection: null,
                    staticTypeNode.HubConfiguration),
                nodeType));
        }

        // (a) issue request to NodeType hub. (b) apply the AssemblyLocation +
        // HubConfiguration delegate the response carries. No request-level cache —
        // the disk-level CompilationCacheService is source-aware and the NodeType
        // hub returns the cached DLL path on a hot path.
        return hub.Observe(
                new GetCompilationPathRequest(/* HEAD */),
                o => o.WithTarget(new Address(nodeType)))
            .Select(d => d.Message)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Catch<GetCompilationPathResponse, Exception>(ex =>
            {
                logger.LogDebug(ex,
                    "GetCompilationPathRequest to '{NodeType}' faulted — falling back to default config",
                    nodeType);
                return Observable.Return(new GetCompilationPathResponse(
                    Success: false,
                    AssemblyLocation: null,
                    Collection: null,
                    Version: null,
                    Error: ex.Message,
                    HubConfiguration: null));
            })
            .Select(response =>
            {
                if (response.Success && !string.IsNullOrEmpty(response.AssemblyLocation))
                {
                    if (response.HubConfiguration != null)
                        _hubConfigurations[nodeType] = response.HubConfiguration;
                    return ApplyEntry(
                        node,
                        new PathCacheEntry(
                            response.AssemblyLocation!,
                            response.Collection,
                            response.HubConfiguration),
                        nodeType);
                }

                // Last-resort: return default config + (if compile failed) an error overlay.
                return WithCompilationErrorOverlay(node, nodeType, response.Error);
            });
    }

    private MeshNode ApplyDefaultConfig(MeshNode node)
    {
        if (node.HubConfiguration != null)
            return node;
        var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;
        if (defaultConfig != null)
            return node with { HubConfiguration = defaultConfig };
        return node;
    }

    private MeshNode ApplyEntry(MeshNode node, PathCacheEntry entry, string nodeType)
    {
        var hubConfig = node.HubConfiguration ?? entry.HubConfiguration;
        var assemblyLocation = node.AssemblyLocation
            ?? (string.IsNullOrEmpty(entry.AssemblyLocation) ? null : entry.AssemblyLocation);
        return CopyIconFromNodeType(node with
        {
            HubConfiguration = hubConfig,
            AssemblyLocation = assemblyLocation
        }, nodeType);
    }

    private MeshNode WithCompilationErrorOverlay(MeshNode node, string nodeType, string? error)
    {
        var baseConfig = node.HubConfiguration
            ?? GetCachedHubConfiguration(nodeType)
            ?? meshConfiguration.DefaultNodeHubConfiguration;
        if (string.IsNullOrEmpty(error))
            return CopyIconFromNodeType(node with { HubConfiguration = baseConfig }, nodeType);

        var overlay = CreateCompilationErrorConfiguration(error);
        Func<MessageHubConfiguration, MessageHubConfiguration> composed = baseConfig != null
            ? (config => overlay(baseConfig(config)))
            : overlay;
        return CopyIconFromNodeType(node with { HubConfiguration = composed }, nodeType);
    }

    private record PathCacheEntry(
        string AssemblyLocation,
        string? Collection,
        Func<MessageHubConfiguration, MessageHubConfiguration>? HubConfiguration);

    /// <summary>
    /// Copies the Icon from the built-in node type definition to the instance if the instance has no Icon set.
    /// </summary>
    private MeshNode CopyIconFromNodeType(MeshNode node, string nodeType)
    {
        if (string.IsNullOrEmpty(node.Icon) &&
            meshConfiguration.Nodes.TryGetValue(nodeType, out var builtInNode) &&
            !string.IsNullOrEmpty(builtInNode.Icon))
        {
            return node with { Icon = builtInNode.Icon };
        }
        return node;
    }

    /// <summary>
    /// Processes a MeshNode to extract/compile its HubConfiguration.
    /// </summary>
    private async Task<Func<MessageHubConfiguration, MessageHubConfiguration>?> ProcessNodeForHubConfigAsync(
        MeshNode? node, Address address)
    {
        if (node == null)
        {
            logger.LogDebug("No MeshNode received for {Address}", address);
            return null;
        }

        // If no NodeType, no HubConfiguration to return
        if (string.IsNullOrEmpty(node.NodeType))
        {
            logger.LogDebug("Node at {Address} has no NodeType", address);
            return null;
        }

        // 1. Try cached HubConfiguration
        var cachedHubConfig = GetCachedHubConfiguration(node.NodeType);
        if (cachedHubConfig != null)
        {
            logger.LogDebug("Found cached HubConfiguration for {NodeType}", node.NodeType);
            return cachedHubConfig;
        }

        // 2. Trigger compilation via GetAssemblyPathAsync (which populates _hubConfigurations)
        await GetAssemblyPathAsync(node.NodeType);

        // Now check cache again
        cachedHubConfig = GetCachedHubConfiguration(node.NodeType);
        if (cachedHubConfig != null)
        {
            logger.LogDebug("Got HubConfiguration after compilation for {NodeType}", node.NodeType);
            return cachedHubConfig;
        }

        logger.LogDebug("No HubConfiguration available for {NodeType}", node.NodeType);
        return null;
    }

    /// <summary>
    /// Gets the NodeTypeData for a node type path.
    /// Returns cached data if available, otherwise triggers compilation.
    /// </summary>
    public Task<NodeTypeData?> GetNodeTypeDataAsync(string nodeTypePath, CancellationToken ct = default)
    {
        var task = _compilationTasks.GetOrAdd(nodeTypePath, path =>
            CompileWithReleaseAsync(path, ct));

        return task.ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled)
            {
                _compilationTasks.TryRemove(nodeTypePath, out _);
                _releaseKeys.TryRemove(nodeTypePath, out _);
            }
            return t.Result?.Data;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// Compiles a NodeType using release-based caching.
    /// Uses file locks for multi-process safety and immutable release folders.
    /// </summary>
    private async Task<NodeTypeCacheEntry?> CompileWithReleaseAsync(string nodeTypePath, CancellationToken ct)
    {
        try
        {
            logger.LogDebug("CompileWithReleaseAsync for {NodeTypePath}", nodeTypePath);

            // 1. Gather compilation inputs and create release
            var (release, node) = await GatherInputsAsync(nodeTypePath, ct);
            if (release == null || node == null)
            {
                logger.LogDebug("No compilation input available for {NodeTypePath}", nodeTypePath);
                return null;
            }

            // 2. Track release key
            _releaseKeys[nodeTypePath] = release.Release;

            // 3. Get release folder path
            var releaseFolder = cacheService.GetReleaseFolderPath(release);

            logger.LogDebug("Release folder for {NodeTypePath}: {ReleaseFolder}", nodeTypePath, releaseFolder);

            // 4. Check if release already exists (fast path - no locking needed)
            if (cacheService.IsReleaseValid(releaseFolder))
            {
                logger.LogDebug("Using existing release for {NodeTypePath}", nodeTypePath);
                return await LoadFromReleaseAsync(release, releaseFolder, nodeTypePath, ct);
            }

            // 5. Acquire file lock for compilation (multi-process safe)
            var nodeName = cacheService.SanitizeNodeName(nodeTypePath);
            logger.LogDebug("Acquiring compilation lock for {NodeTypePath}", nodeTypePath);
            using var lockObj = await CompilationLock.AcquireAsync(
                cacheService.GetLockDirectory(),
                nodeName,
                cacheOptions.LockTimeout,
                logger,
                ct);

            // 6. Double-check after acquiring lock (another process may have compiled)
            if (cacheService.IsReleaseValid(releaseFolder))
            {
                logger.LogDebug("Release created by another process for {NodeTypePath}", nodeTypePath);
                return await LoadFromReleaseAsync(release, releaseFolder, nodeTypePath, ct);
            }

            // 7. Compile to release folder
            if (compilationService == null)
            {
                logger.LogWarning("No compilation service available for {NodeTypePath}", nodeTypePath);
                return CreateNodeTypeDataOnly(release);
            }

            var result = await compilationService.CompileToReleaseAsync(release, node, releaseFolder, ct);

            // 8. Cache hub configurations
            if (result?.NodeTypeConfigurations != null)
            {
                foreach (var config in result.NodeTypeConfigurations)
                {
                    _hubConfigurations[config.NodeType] = config.HubConfiguration;
                    logger.LogDebug("Cached HubConfiguration for {NodeType}", config.NodeType);
                }
            }

            return new NodeTypeCacheEntry(
                CreateNodeTypeData(release),
                result?.AssemblyLocation);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Operation cancelled for {NodeTypePath}", nodeTypePath);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compile NodeType {NodeTypePath}", nodeTypePath);
            throw; // Re-throw to trigger retry on next access
        }
    }

    /// <summary>
    /// Turns the NodeType's <see cref="NodeTypeDefinition.Sources"/> list into concrete
    /// storage paths to probe for Code nodes. Lines understood:
    /// <list type="bullet">
    ///   <item><c>"Source"</c> (or any value without <c>/</c>) — rebased onto <paramref name="nodeTypePath"/>.</item>
    ///   <item><c>"namespace:X"</c> / <c>"path:X"</c> — the X part is used as a storage path.</item>
    ///   <item><c>"@X"</c> / <c>"@@X"</c> — shorthand for the path X.</item>
    ///   <item><c>$self</c> inside any entry — expanded to <paramref name="nodeTypePath"/>.</item>
    /// </list>
    /// If the list is null or empty, defaults to <c>"{nodeTypePath}/Source"</c>.
    /// Query-syntax decoration like <c>scope:subtree</c> and <c>nodeType:Code</c> is
    /// stripped — this helper is only concerned with the path segment, since we feed
    /// <see cref="IMeshStorage.GetDescendantsAsync"/> below.
    /// </summary>
    private static IReadOnlyList<string> ResolveSourcePaths(
        IReadOnlyList<string>? sources,
        string nodeTypePath)
    {
        if (sources == null || sources.Count == 0)
            return [$"{nodeTypePath}/{CodeNodeType.SourceSubNamespace}"];

        var result = new List<string>(sources.Count);
        foreach (var raw in sources)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var expanded = raw.Replace("$self", nodeTypePath).Trim();

            // Strip the @/@@ shorthand.
            if (expanded.StartsWith("@@")) expanded = expanded[2..].TrimStart();
            else if (expanded.StartsWith("@")) expanded = expanded[1..].TrimStart();
            if (expanded.Length == 0) continue;

            // Pull out the value of the first recognised qualifier (namespace:/path:), if any.
            var value = expanded;
            foreach (var qualifier in new[] { "namespace:", "path:" })
            {
                var idx = value.IndexOf(qualifier, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var valueStart = idx + qualifier.Length;
                var valueEnd = valueStart;
                while (valueEnd < value.Length && !char.IsWhiteSpace(value[valueEnd]))
                    valueEnd++;
                value = value[valueStart..valueEnd];
                break;
            }

            // If the value is a single segment (no /), treat as self-relative folder.
            if (value.Length > 0 && !value.Contains('/'))
                value = $"{nodeTypePath}/{value}";

            if (value.Length > 0)
                result.Add(value);
        }
        return result.Count > 0 ? result : [$"{nodeTypePath}/{CodeNodeType.SourceSubNamespace}"];
    }

    /// <summary>
    /// Gathers all inputs needed for compilation from persistence.
    /// Returns a NodeTypeRelease with all compilation inputs and the MeshNode.
    /// </summary>
    private async Task<(NodeTypeRelease? Release, MeshNode? Node)> GatherInputsAsync(string nodeTypePath, CancellationToken ct)
    {
        // Use IMeshStorage directly to bypass routing/hub creation.
        // This avoids the circular dependency: compilation → routing → hub creation → needs compilation.

        // Get the node directly from persistence (no routing)
        var node = await meshStorage.GetNode(nodeTypePath).FirstAsync().ToTask(ct);
        if (node == null)
        {
            var msg = $"NodeType definition not found at path '{nodeTypePath}'. Check that the NodeType node exists in persistence or a static node provider.";
            logger.LogWarning(msg);
            _compilationErrors[nodeTypePath] = msg;
            return (null, null);
        }

        // Get NodeTypeDefinition from node content
        var definition = node.Content as NodeTypeDefinition;
        if (definition == null)
        {
            var msg = $"Node at '{nodeTypePath}' exists but has no NodeTypeDefinition content (actual content type: {node.Content?.GetType().Name ?? "null"}).";
            logger.LogWarning(msg);
            _compilationErrors[nodeTypePath] = msg;
            return (null, null);
        }

        // Collect Code nodes from the configured sources. Default: the sibling "Source"
        // subtree. `GetAllDescendantsAsync` (not `GetDescendantsAsync`) is used as a
        // belt-and-braces include — Code nodes are primary content (IsSatelliteType =
        // false), but historic data may still carry satellite-style MainNode values
        // from when Code was registered as a satellite type.
        // We also check the parent path as a single-node fetch so `path:X` shorthand
        // with a leaf Code node path works.
        var codeFiles = new List<string>();
        var codeFilePaths = new List<string>();
        var seenCodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sourcePaths = ResolveSourcePaths(definition.Sources, nodeTypePath);
        foreach (var sourcePath in sourcePaths)
        {
            // Path-exact fetch first (handles `path:X` / `@X` pointing at a single Code node).
            var single = await meshStorage.GetNode(sourcePath).FirstAsync().ToTask(ct);
            if (single != null) AddIfCodeNode(single);

            // Then all descendants INCLUDING satellites — that's the Code-file case.
            await foreach (var descendant in meshStorage.GetAllDescendantsAsync(sourcePath))
                AddIfCodeNode(descendant);
        }

        void AddIfCodeNode(MeshNode candidate)
        {
            if (candidate.NodeType != CodeNodeType.NodeType) return;
            if (candidate.Content is not CodeConfiguration codeConfig) return;
            if (string.IsNullOrEmpty(codeConfig.Code)) return;
            if (candidate.Path is { Length: > 0 } p && !seenCodePaths.Add(p)) return;
            codeFiles.Add(codeConfig.Code);
            if (candidate.Path != null) codeFilePaths.Add(candidate.Path);
        }

        logger.LogInformation(
            "NodeType '{NodeTypePath}' source discovery: {Count} Code nodes from [{Paths}]",
            nodeTypePath, codeFiles.Count, string.Join(", ", codeFilePaths));

        // Resolve @@ include references in code files (e.g., @@FutuRe/LineOfBusiness/Source/LineOfBusiness)
        if (compilationService != null)
        {
            for (int i = 0; i < codeFiles.Count; i++)
            {
                codeFiles[i] = await compilationService.ResolveCodeIncludesAsync(codeFiles[i], meshStorage, ct);
            }
        }

        if (codeFiles.Count == 0)
        {
            logger.LogWarning("NodeType '{NodeTypePath}' has no code files under /Source. Hub will use default configuration only.", nodeTypePath);
        }

        var code = codeFiles.Count > 0 ? string.Join("\n\n", codeFiles) : null;

        var frameworkVersion = typeof(NodeTypeService).Assembly.GetName().Version?.ToString() ?? "unknown";
        var release = NodeTypeRelease.Create(
            nodeTypePath,
            code,
            definition.Configuration,
            definition.ContentCollections,
            cacheService.GetFrameworkTimestamp(),
            frameworkVersion);

        var enrichedNode = node with
        {
            Content = definition,
            NodeType = nodeTypePath
        };

        return (release, enrichedNode);
    }

    /// <summary>
    /// Loads a compiled assembly from an existing release folder.
    /// </summary>
    private async Task<NodeTypeCacheEntry?> LoadFromReleaseAsync(
        NodeTypeRelease release,
        string releaseFolder,
        string nodeTypePath,
        CancellationToken ct)
    {
        try
        {
            var assembly = cacheService.LoadAssemblyFromRelease(release, releaseFolder);
            if (assembly == null)
            {
                logger.LogWarning("Failed to load assembly from release {ReleaseFolder}", releaseFolder);
                return CreateNodeTypeDataOnly(release);
            }

            // Extract NodeTypeConfigurations from the loaded assembly
            var configurations = new List<NodeTypeConfiguration>();
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(MeshNodeProviderAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var attribute = (MeshNodeProviderAttribute?)Activator.CreateInstance(type);
                    if (attribute != null)
                    {
                        foreach (var meshNode in attribute.Nodes)
                        {
                            var hubConfig = meshNode.HubConfiguration;
                            if (hubConfig != null)
                            {
                                _hubConfigurations[meshNode.Path] = hubConfig;

                                configurations.Add(new NodeTypeConfiguration
                                {
                                    NodeType = meshNode.Path,
                                    DataType = typeof(object),
                                    HubConfiguration = hubConfig
                                });
                            }
                        }
                    }
                }
            }

            var dllPath = Path.Combine(releaseFolder, $"{release.GetSanitizedPath()}.dll");
            logger.LogDebug("Loaded {Count} configurations from release {ReleaseFolder}", configurations.Count, releaseFolder);

            return new NodeTypeCacheEntry(CreateNodeTypeData(release), dllPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load from release {ReleaseFolder}", releaseFolder);
            return CreateNodeTypeDataOnly(release);
        }
    }

    /// <summary>
    /// Creates NodeTypeData from a NodeTypeRelease.
    /// </summary>
    private static NodeTypeData CreateNodeTypeData(NodeTypeRelease release)
    {
        var codeConfigs = new List<CodeConfiguration>();
        if (!string.IsNullOrEmpty(release.Code))
        {
            codeConfigs.Add(new CodeConfiguration { Code = release.Code });
        }

        // Create a minimal definition from the release
        var definition = new NodeTypeDefinition
        {
            Configuration = release.HubConfiguration,
            ContentCollections = release.ContentCollections?.ToList()
        };

        return new NodeTypeData
        {
            Definition = definition,
            CodeConfigurations = codeConfigs
        };
    }

    /// <summary>
    /// Creates a cache entry with NodeTypeData but no assembly path.
    /// </summary>
    private static NodeTypeCacheEntry CreateNodeTypeDataOnly(NodeTypeRelease release)
    {
        return new NodeTypeCacheEntry(CreateNodeTypeData(release), null);
    }

    /// <summary>
    /// Called when the remote stream emits an update after the initial value.
    /// Invalidates the cache so next access will refetch and recompile.
    /// </summary>
    private void OnStreamUpdated(string nodeTypePath)
    {
        logger.LogInformation("NodeType {NodeTypePath} updated, invalidating cache", nodeTypePath);
        InvalidateCache(nodeTypePath);
    }

    /// <summary>
    /// Creates a hub configuration that shows a compilation error in the Overview area.
    /// Renders as markdown so it respects the current theme (readable in both light and
    /// dark modes) and gets code-block formatting for the Roslyn diagnostics.
    /// </summary>
    private static Func<MessageHubConfiguration, MessageHubConfiguration> CreateCompilationErrorConfiguration(string errorMessage)
    {
        return config => config.AddLayout(layout =>
            layout.WithView(MeshNodeLayoutAreas.OverviewArea, (host, ctx) =>
                Observable.Return<UiControl?>(BuildCompilationErrorMarkdown(errorMessage))));
    }

    private static UiControl BuildCompilationErrorMarkdown(string errorMessage)
    {
        // Split "Compilation failed for 'X':\n<diagnostics>" into header + body so the
        // diagnostics land in a fenced code block — much easier to read than one long
        // HTML blob, and uses the theme's code/text colours.
        var newlineIdx = errorMessage.IndexOf('\n');
        var header = newlineIdx >= 0 ? errorMessage[..newlineIdx].TrimEnd(':') : errorMessage;
        var body = newlineIdx >= 0 ? errorMessage[(newlineIdx + 1)..].TrimEnd() : string.Empty;

        var markdown =
$@"> **⚠ {header}**
>
> Fix the source code or the NodeType's `sources` list, then use the **Recycle** menu to flush the cached grain (or call `GetDiagnostics` via MCP to re-check).

```text
{body}
```";

        return Controls.Stack
            .WithStyle("padding: 16px;")
            .WithView(Controls.Markdown(markdown));
    }

    #region Creatable Types

    /// <summary>
    /// Gets the global creatable types from configuration or uses defaults.
    /// </summary>
    private IReadOnlyList<string> GlobalTypes => meshConfiguration.GlobalCreatableTypes;

    /// <inheritdoc />
    public async IAsyncEnumerable<CreatableTypeInfo> GetCreatableTypesAsync(
        string nodePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {

        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MeshNode? parentNode = null;
        string? currentNodeType = null;

        // Get the parent node context
        if (!string.IsNullOrEmpty(nodePath))
        {
            var nodeQuery = $"path:{nodePath}";
            await foreach (var node in QueryAsync<MeshNode>(nodeQuery, ct).WithCancellation(ct))
            {
                parentNode = node;
                currentNodeType = node.NodeType;
                break;
            }
        }

        // Collect all creatable types from multiple sources
        var creatableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includeGlobalTypes = true;

        // 1. ALWAYS auto-discover child NodeTypes (this is the default behavior)
        if (!string.IsNullOrEmpty(nodePath))
        {
            // Child NodeTypes under this node's path
            var childQuery = $"namespace:{nodePath} nodeType:NodeType";
            await foreach (var childType in QueryAsync<MeshNode>(childQuery, ct).WithCancellation(ct))
            {
                creatableTypes.Add(childType.Path);
            }

            // Child NodeTypes under the node's NodeType path
            // e.g., ProductLaunch (nodeType: ACME/Project) can create ACME/Project/Todo
            if (!string.IsNullOrEmpty(currentNodeType) && currentNodeType != "NodeType")
            {
                var nodeTypeChildQuery = $"namespace:{currentNodeType} nodeType:NodeType";
                await foreach (var childType in QueryAsync<MeshNode>(nodeTypeChildQuery, ct).WithCancellation(ct))
                {
                    creatableTypes.Add(childType.Path);
                }
            }
        }
        else
        {
            // At root: include root-level NodeTypes
            var rootQuery = "nodeType:NodeType";
            await foreach (var typeNode in QueryAsync<MeshNode>(rootQuery, ct).WithCancellation(ct))
            {
                if (!typeNode.Path.Contains('/'))
                    creatableTypes.Add(typeNode.Path);
            }
        }

        // 2. Check for fluent API rules (AddCreatableTypes) - these ADD to the auto-discovered types
        CreatableTypesRules? rules = null;
        if (!string.IsNullOrEmpty(currentNodeType))
        {
            rules = GetCreatableTypesRules(currentNodeType);
        }

        if (rules != null)
        {
            logger.LogDebug("Applying CreatableTypesRules from {NodeType}", currentNodeType);
            includeGlobalTypes = rules.IncludeDefaults;

            // Add types from rules
            foreach (var typePath in rules.Rules.SelectMany(r => r(parentNode)))
            {
                creatableTypes.Add(typePath);
            }

            // Track excluded types
            foreach (var t in rules.ExcludedTypes)
            {
                excludedTypes.Add(t);
            }
        }
        else
        {
            // Fallback: check JSON-based configuration in NodeTypeDefinition
            if (!string.IsNullOrEmpty(currentNodeType) && currentNodeType != "NodeType")
            {
                var nodeTypeQuery = $"path:{currentNodeType}";
                await foreach (var nodeTypeNode in QueryAsync<MeshNode>(nodeTypeQuery, ct).WithCancellation(ct))
                {
                    if (nodeTypeNode.Content is NodeTypeDefinition parentTypeDef)
                    {
                        // Add explicit CreatableTypes from JSON
                        if (parentTypeDef.CreatableTypes != null)
                        {
                            foreach (var t in parentTypeDef.CreatableTypes)
                                creatableTypes.Add(t);
                        }

                        includeGlobalTypes = parentTypeDef.IncludeGlobalTypes;
                    }
                    break;
                }
            }
        }

        // 3. Add global types if enabled
        if (includeGlobalTypes)
        {
            foreach (var t in GlobalTypes)
                creatableTypes.Add(t);
        }

        // 4. Remove excluded types
        foreach (var t in excludedTypes)
        {
            creatableTypes.Remove(t);
        }

        // Yield CreatableTypeInfo for each type
        foreach (var typePath in creatableTypes)
        {
            // Skip types marked as NotCreatable
            if (IsNotCreatable(typePath))
                continue;

            if (!addedPaths.Add(typePath))
                continue;

            // Try to get type info from cache or query
            var typeInfo = await GetTypeInfoAsync(typePath, ct);
            if (typeInfo != null)
                yield return typeInfo;
        }
    }

    /// <summary>
    /// Gets CreatableTypeInfo for a type path.
    /// </summary>
    private async Task<CreatableTypeInfo?> GetTypeInfoAsync(
        string typePath,
        CancellationToken ct)
    {
        // Check for global types first
        if (GlobalTypes.Contains(typePath))
        {
            return new CreatableTypeInfo(
                NodeTypePath: typePath,
                DisplayName: typePath,
                Icon: GetGlobalTypeIcon(typePath),
                Description: GetGlobalTypeDescription(typePath),
                Order: GetGlobalTypeOrder(typePath),
                SubNamespace: GetGlobalTypeSubNamespace(typePath)
            );
        }

        // Query for the type node
        var typeQuery = $"path:{typePath}";
        await foreach (var typeNode in QueryAsync<MeshNode>(typeQuery, ct).WithCancellation(ct))
        {
            return CreateCreatableTypeInfoFromNode(typeNode);
        }

        // Fallback: create basic info
        return new CreatableTypeInfo(
            NodeTypePath: typePath,
            DisplayName: typePath.Split('/').Last(),
            Icon: "Cube",
            Description: $"Create a {typePath.Split('/').Last()}",
            Order: 0
        );
    }

    private static string GetGlobalTypeIcon(string globalType) => globalType switch
    {
        "Markdown" => "Document",
        "NodeType" => "Wrench",
        "Agent" => "Bot",
        "Thread" => "Chat",
        _ => "Cube"
    };

    private static string GetGlobalTypeDescription(string globalType) => globalType switch
    {
        "Markdown" => "Create a markdown document",
        "NodeType" => "Create a new node type definition",
        "Agent" => "Create a new AI agent",
        "Thread" => "Create a new conversation thread",
        _ => $"Create a {globalType}"
    };

    private static int GetGlobalTypeOrder(string globalType) => globalType switch
    {
        "Markdown" => 1000,
        "NodeType" => 1001,
        "Agent" => 1002,
        "Thread" => 1003,
        _ => 1000
    };

    private static string? GetGlobalTypeSubNamespace(string globalType) => globalType switch
    {
        _ => null // Default: use last segment of NodeTypePath
    };

    /// <summary>
    /// Creates CreatableTypeInfo from a MeshNode.
    /// </summary>
    private static CreatableTypeInfo CreateCreatableTypeInfoFromNode(MeshNode node)
    {
        var typeDef = node.Content as NodeTypeDefinition;

        // Emoji takes precedence over Icon (SVG path)
        var icon = typeDef?.Emoji ?? node.Icon;

        return new CreatableTypeInfo(
            NodeTypePath: node.Path,
            DisplayName: node.Name ?? GetLastPathSegment(node.Path),
            Icon: icon,
            Description: typeDef?.Description,
            Order: node.Order ?? 0
        );
    }

    /// <summary>
    /// Gets the last segment of a path.
    /// </summary>
    private static string GetLastPathSegment(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }

    #endregion

    public void Dispose()
    {
        _changeFeedSubscription?.Dispose();
        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
        _compilationTasks.Clear();
        _releaseKeys.Clear();
        _hubConfigurations.Clear();
        _accessRules.Clear();
    }

    /// <summary>
    /// Internal cache entry holding both NodeTypeData and compiled assembly path.
    /// </summary>
    private record NodeTypeCacheEntry(NodeTypeData Data, string? AssemblyPath);
}
