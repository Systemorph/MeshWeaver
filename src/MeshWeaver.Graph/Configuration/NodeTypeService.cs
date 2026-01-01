using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service for managing NodeType data with thread-safe caching.
/// CRITICAL: Uses Lazy&lt;Task&gt; to ensure factory runs exactly once per key.
/// ConcurrentDictionary.GetOrAdd with a factory lambda can run the factory multiple times,
/// but Lazy ensures the inner factory executes only once.
/// </summary>
internal class NodeTypeService : INodeTypeService, IDisposable
{
    private readonly IMessageHub meshHub;
    private readonly ILogger<NodeTypeService> logger;
    private readonly IMeshNodeCompilationService? compilationService;
    private readonly MeshConfiguration meshConfiguration;

    // CRITICAL: Use Lazy<Task> to ensure factory runs exactly once per key.
    // ConcurrentDictionary.GetOrAdd can run the factory multiple times concurrently,
    // but Lazy ensures only one actual compilation happens.
    private readonly ConcurrentDictionary<string, Lazy<Task<NodeTypeCacheEntry?>>> _cache = new();

    // Stream subscriptions for cache invalidation
    private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new();

    // Cached HubConfiguration functions for fast synchronous access
    private readonly ConcurrentDictionary<string, Func<MessageHubConfiguration, MessageHubConfiguration>> _hubConfigurations = new();

    public NodeTypeService(
        IMessageHub meshHub,
        MeshConfiguration meshConfiguration,
        ILogger<NodeTypeService> logger,
        IMeshNodeCompilationService? compilationService = null)
    {
        this.meshHub = meshHub;
        this.meshConfiguration = meshConfiguration;
        this.logger = logger;
        this.compilationService = compilationService;

        // Initialize cache from pre-registered nodes in MeshConfiguration
        InitializeFromMeshConfiguration();
    }

    /// <summary>
    /// Initializes the HubConfiguration cache from pre-registered mesh nodes.
    /// Subscribes to HubConfiguration observables and caches the results.
    /// </summary>
    private void InitializeFromMeshConfiguration()
    {
        foreach (var node in meshConfiguration.Nodes.Values)
        {
            if (node.HubConfiguration == null)
                continue;

            var path = node.Path;
            var subscription = node.HubConfiguration.Subscribe(
                hubConfig =>
                {
                    if (hubConfig != null)
                    {
                        _hubConfigurations[path] = hubConfig;
                        logger.LogDebug("Cached HubConfiguration from MeshConfiguration for {Path}", path);
                    }
                },
                ex => logger.LogError(ex, "Error subscribing to HubConfiguration for {Path}", path));

            _subscriptions[path] = subscription;
        }
    }

    /// <inheritdoc />
    public Task<string?> GetAssemblyPathAsync(string nodeTypePath, CancellationToken ct = default)
    {
        // Use Lazy<Task> to ensure the factory runs exactly once per key.
        // ConcurrentDictionary.GetOrAdd can run the factory multiple times,
        // but Lazy ensures only one actual SubscribeAndProcessAsync call.
        var lazyTask = _cache.GetOrAdd(nodeTypePath, path =>
            new Lazy<Task<NodeTypeCacheEntry?>>(() => SubscribeAndProcessAsync(path, ct)));

        return lazyTask.Value.ContinueWith(t => t.Result?.AssemblyPath, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <inheritdoc />
    public NodeTypeConfiguration? GetCachedConfiguration(string nodeTypePath)
    {
        // Check if we have a cached HubConfiguration
        if (_hubConfigurations.TryGetValue(nodeTypePath, out var hubConfig))
        {
            // Return a minimal NodeTypeConfiguration with the cached HubConfiguration
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
    /// </summary>
    public Func<MessageHubConfiguration, MessageHubConfiguration>? GetCachedHubConfiguration(string nodeTypePath)
    {
        return _hubConfigurations.GetValueOrDefault(nodeTypePath);
    }

    /// <inheritdoc />
    public void InvalidateCache(string nodeTypePath)
    {
        logger.LogDebug("Invalidating cache for {NodeTypePath}", nodeTypePath);

        // Remove from all caches (Lazy<Task> will be disposed by GC)
        _cache.TryRemove(nodeTypePath, out _);
        _hubConfigurations.TryRemove(nodeTypePath, out _);

        // Dispose subscription (will re-subscribe on next access)
        if (_subscriptions.TryRemove(nodeTypePath, out var subscription))
        {
            subscription.Dispose();
        }
    }

    /// <inheritdoc />
    public MeshNode EnrichWithNodeType(MeshNode node)
    {
        // Skip if no NodeType or HubConfiguration is already set
        if (string.IsNullOrEmpty(node.NodeType) || node.HubConfiguration != null)
            return node;

        var nodeType = node.NodeType;

        // Try cached HubConfiguration (sync fast path) - wrap in Observable.Return
        var cachedHubConfig = GetCachedHubConfiguration(nodeType);
        if (cachedHubConfig != null)
        {
            return node with { HubConfiguration = Observable.Return<Func<MessageHubConfiguration, MessageHubConfiguration>?>(cachedHubConfig) };
        }

        // Not cached - return node with Observable that will emit when compiled
        // The caller will subscribe only when actually creating the hub
        return node with { HubConfiguration = GetHubConfigurationForNodeType(nodeType) };
    }

    /// <summary>
    /// Gets or compiles the HubConfiguration for a node type.
    /// Returns an Observable that can be stored without blocking.
    /// </summary>
    private IObservable<Func<MessageHubConfiguration, MessageHubConfiguration>?> GetHubConfigurationForNodeType(string nodeType)
    {
        var subject = new ReplaySubject<Func<MessageHubConfiguration, MessageHubConfiguration>?>(1);

        // Fire off compilation asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                // Trigger compilation
                await GetAssemblyPathAsync(nodeType);

                // Emit the cached config
                var hubConfig = GetCachedHubConfiguration(nodeType);
                subject.OnNext(hubConfig);
                subject.OnCompleted();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get HubConfiguration for NodeType {NodeType}", nodeType);
                subject.OnNext(null);
                subject.OnCompleted();
            }
        });

        return subject.AsObservable();
    }

    // Cache for HubConfiguration observables by address - uses ReplaySubject to cache the latest value
    private readonly ConcurrentDictionary<string, ReplaySubject<Func<MessageHubConfiguration, MessageHubConfiguration>?>> _addressHubConfigCache = new();

    /// <inheritdoc />
    public IObservable<Func<MessageHubConfiguration, MessageHubConfiguration>?> GetHubConfiguration(Address address)
    {
        var addressKey = address.ToString();

        // GetOrAdd ensures thread-safe lazy initialization
        // Returns the ReplaySubject as IObservable - caller subscribes when needed
        return _addressHubConfigCache.GetOrAdd(addressKey, _ => CreateHubConfigSubject(address));
    }

    /// <summary>
    /// Creates a ReplaySubject that subscribes to the remote stream for a node,
    /// extracts NodeType, compiles and emits the HubConfiguration.
    /// </summary>
    private ReplaySubject<Func<MessageHubConfiguration, MessageHubConfiguration>?> CreateHubConfigSubject(Address address)
    {
        // ReplaySubject(1) caches the latest value for late subscribers
        var subject = new ReplaySubject<Func<MessageHubConfiguration, MessageHubConfiguration>?>(1);

        logger.LogDebug("Creating HubConfiguration subject for {Address}", address);

        // Subscribe to the node hub with empty DataPathReference
        var workspace = meshHub.GetWorkspace();
        var stream = workspace.GetRemoteStream<MeshNode, DataPathReference>(
            address,
            new DataPathReference(""));

        // Subscribe to stream - process each value and emit HubConfiguration
        var subscription = stream
            .Subscribe(
                async change =>
                {
                    try
                    {
                        var hubConfig = await ProcessNodeForHubConfigAsync(change.Value, address);
                        subject.OnNext(hubConfig);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing node for HubConfiguration at {Address}", address);
                        subject.OnNext(null);
                    }
                },
                ex =>
                {
                    logger.LogError(ex, "Error in node stream for {Address}", address);
                    subject.OnError(ex);
                });

        // Store subscription for cleanup
        _subscriptions[address.ToString()] = subscription;

        return subject;
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
    /// Returns cached data if available, otherwise subscribes to remote stream.
    /// </summary>
    public Task<NodeTypeData?> GetNodeTypeDataAsync(string nodeTypePath, CancellationToken ct = default)
    {
        // Use Lazy<Task> to ensure the factory runs exactly once per key.
        var lazyTask = _cache.GetOrAdd(nodeTypePath, path =>
            new Lazy<Task<NodeTypeCacheEntry?>>(() => SubscribeAndProcessAsync(path, ct)));

        // Return a projection of the cached task
        return lazyTask.Value.ContinueWith(t => t.Result?.Data, TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// Fetches NodeTypeData directly from persistence and compiles it.
    /// Uses direct persistence access to avoid circular dependency when hub is being created.
    /// This method is called once per nodeTypePath due to ConcurrentDictionary.GetOrAdd.
    /// </summary>
    private async Task<NodeTypeCacheEntry?> SubscribeAndProcessAsync(string nodeTypePath, CancellationToken ct)
    {
        try
        {
            logger.LogDebug("Fetching NodeType data for {NodeTypePath}", nodeTypePath);

            // Get persistence service to fetch data directly (avoiding hub subscription deadlock)
            var persistence = meshHub.ServiceProvider.GetService<IPersistenceService>();
            if (persistence == null)
            {
                logger.LogWarning("IPersistenceService not available for {NodeTypePath}", nodeTypePath);
                return null;
            }

            // Get the node from persistence
            var node = await persistence.GetNodeAsync(nodeTypePath, ct);
            if (node == null)
            {
                logger.LogDebug("No node found in persistence for {NodeTypePath}", nodeTypePath);
                return null;
            }

            // Get NodeTypeDefinition from node content
            var definition = node.Content as NodeTypeDefinition;
            if (definition == null)
            {
                logger.LogDebug("Node at {NodeTypePath} has no NodeTypeDefinition content", nodeTypePath);
                return null;
            }

            // Get CodeConfigurations from partition
            var codeConfigurations = new List<CodeConfiguration>();
            await foreach (var obj in persistence.GetPartitionObjectsAsync(nodeTypePath, null).WithCancellation(ct))
            {
                if (obj is CodeConfiguration codeConfig)
                {
                    codeConfigurations.Add(codeConfig);
                }
            }

            var nodeTypeData = new NodeTypeData
            {
                Definition = definition,
                CodeConfigurations = codeConfigurations
            };

            logger.LogDebug("Loaded NodeTypeData for {NodeTypePath} with {CodeConfigCount} code configurations",
                nodeTypePath, codeConfigurations.Count);

            // Compile if compilation service available
            string? assemblyPath = null;
            if (compilationService != null)
            {
                // Create a minimal MeshNode for compilation
                var compileNode = MeshNode.FromPath(nodeTypePath) with
                {
                    Content = nodeTypeData.Definition,
                    NodeType = nodeTypePath
                };

                var compilationResult = await compilationService.CompileAndGetConfigurationsAsync(compileNode, ct);
                if (compilationResult != null)
                {
                    assemblyPath = compilationResult.AssemblyLocation;

                    // Cache the HubConfiguration functions for fast synchronous access
                    // Note: AddMeshDataSource is already added by the generator for NodeTypeDefinition content
                    // via ConfigureMeshHub().WithCodeConfiguration().Build()
                    foreach (var config in compilationResult.NodeTypeConfigurations)
                    {
                        _hubConfigurations[config.NodeType] = config.HubConfiguration;
                        logger.LogDebug("Cached HubConfiguration for {NodeType}", config.NodeType);
                    }
                }
            }

            return new NodeTypeCacheEntry(nodeTypeData, assemblyPath);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Operation cancelled for {NodeTypePath}", nodeTypePath);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch NodeType data for {NodeTypePath}", nodeTypePath);
            return null;
        }
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

    public void Dispose()
    {
        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
        _cache.Clear();
        _hubConfigurations.Clear();

        // Dispose ReplaySubjects
        foreach (var subject in _addressHubConfigCache.Values)
        {
            subject.Dispose();
        }
        _addressHubConfigCache.Clear();
    }

    /// <summary>
    /// Internal cache entry holding both NodeTypeData and compiled assembly path.
    /// </summary>
    private record NodeTypeCacheEntry(NodeTypeData Data, string? AssemblyPath);
}
