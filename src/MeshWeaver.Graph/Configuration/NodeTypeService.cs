using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
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
    private readonly IMessageHub meshHub;
    private readonly ILogger<NodeTypeService> logger;
    private readonly MeshNodeCompilationService? compilationService;
    private readonly MeshConfiguration meshConfiguration;
    private readonly ICompilationCacheService cacheService;
    private readonly CompilationCacheOptions cacheOptions;

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

    public NodeTypeService(
        IMessageHub meshHub,
        MeshConfiguration meshConfiguration,
        ILogger<NodeTypeService> logger,
        ICompilationCacheService cacheService,
        IOptions<CompilationCacheOptions> cacheOptions,
        MeshNodeCompilationService? compilationService = null)
    {
        this.meshHub = meshHub;
        this.meshConfiguration = meshConfiguration;
        this.logger = logger;
        this.cacheService = cacheService;
        this.cacheOptions = cacheOptions.Value;
        this.compilationService = compilationService;

        // Initialize cache from pre-registered nodes in MeshConfiguration
        InitializeFromMeshConfiguration();
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
    /// Caches a hub configuration and extracts CreatableTypesRules and ContentType.
    /// </summary>
    private void CacheHubConfiguration(string nodeTypePath, Func<MessageHubConfiguration, MessageHubConfiguration> hubConfig)
    {
        _hubConfigurations[nodeTypePath] = hubConfig;
        logger.LogDebug("Cached HubConfiguration for {Path}", nodeTypePath);

        // Extract rules by applying the configuration to a base config
        try
        {
            var baseConfig = new MessageHubConfiguration(meshHub.ServiceProvider, new Address(nodeTypePath));
            var configured = hubConfig(baseConfig);

            var rules = configured.GetCreatableTypesRules();
            if (rules != null)
            {
                _creatableTypesRules[nodeTypePath] = rules;
                logger.LogDebug("Cached CreatableTypesRules for {Path}", nodeTypePath);
            }

            if (configured.IsNotCreatable())
            {
                _notCreatableTypes[nodeTypePath] = true;
                logger.LogDebug("Marked {Path} as NotCreatable", nodeTypePath);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - rules are optional
            logger.LogDebug(ex, "Could not extract rules from hub config for {Path}", nodeTypePath);
        }
    }

    /// <summary>
    /// Gets the cached CreatableTypesRules for a node type.
    /// </summary>
    public CreatableTypesRules? GetCreatableTypesRules(string nodeTypePath)
    {
        return _creatableTypesRules.GetValueOrDefault(nodeTypePath);
    }

    /// <summary>
    /// Checks if a type is marked as not creatable.
    /// </summary>
    public bool IsNotCreatable(string nodeTypePath)
    {
        return _notCreatableTypes.GetValueOrDefault(nodeTypePath);
    }

    /// <inheritdoc />
    public Task<string?> GetAssemblyPathAsync(string nodeTypePath, CancellationToken ct = default)
    {
        // Use ConcurrentDictionary.GetOrAdd with a Task to ensure only one compilation runs per key.
        // On failure, remove from dictionary to allow retry on next access.
        var task = _compilationTasks.GetOrAdd(nodeTypePath, path =>
            CompileWithReleaseAsync(path, ct));

        return task.ContinueWith(t =>
        {
            // On failure, remove from cache to allow retry
            if (t.IsFaulted || t.IsCanceled)
            {
                _compilationTasks.TryRemove(nodeTypePath, out _);
                _releaseKeys.TryRemove(nodeTypePath, out _);
            }
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
        var hubConfig = _hubConfigurations.GetValueOrDefault(nodeTypePath);
        var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;

        // Return combined config if both exist
        // Apply defaultConfig first (sets defaults like DetailsArea),
        // then hubConfig (node type can override, e.g., Markdown sets $Content)
        if (hubConfig != null && defaultConfig != null)
            return config => hubConfig(defaultConfig(config));

        // Return whichever one exists, or null if neither
        return hubConfig ?? defaultConfig;
    }

    /// <inheritdoc />
    public void InvalidateCache(string nodeTypePath)
    {
        logger.LogDebug("Invalidating cache for {NodeTypePath}", nodeTypePath);

        // Remove from all caches
        _compilationTasks.TryRemove(nodeTypePath, out _);
        _releaseKeys.TryRemove(nodeTypePath, out _);
        _hubConfigurations.TryRemove(nodeTypePath, out _);
        _creatableTypesRules.TryRemove(nodeTypePath, out _);
        _notCreatableTypes.TryRemove(nodeTypePath, out _);

        // Dispose subscription (will re-subscribe on next access)
        if (_subscriptions.TryRemove(nodeTypePath, out var subscription))
        {
            subscription.Dispose();
        }
    }

    /// <inheritdoc />
    public MeshNode EnrichWithNodeType(MeshNode node)
    {
        // Skip if HubConfiguration is already set
        if (node.HubConfiguration != null)
            return node;

        var nodeType = node.NodeType;

        // If NodeType is not set, try to infer it from the namespace (first path segment)
        // This handles legacy nodes that were created without NodeType
        if (string.IsNullOrEmpty(nodeType) && !string.IsNullOrEmpty(node.Namespace))
        {
            var firstSegment = node.Namespace.Split('/')[0];
            // Check if this segment matches a built-in node type
            if (meshConfiguration.Nodes.ContainsKey(firstSegment) || _hubConfigurations.ContainsKey(firstSegment))
            {
                nodeType = firstSegment;
            }
        }

        if (!string.IsNullOrEmpty(nodeType))
        {
            // Check if this specific nodeType config is cached
            if (_hubConfigurations.ContainsKey(nodeType))
            {
                // NodeType config is compiled - return combined config immediately
                var cachedHubConfig = GetCachedHubConfiguration(nodeType);
                return node with { HubConfiguration = cachedHubConfig };
            }

            // Check if this is a built-in NodeType (registered via AddMeshNodes)
            if (meshConfiguration.Nodes.TryGetValue(nodeType, out var builtInNode) &&
                builtInNode.HubConfiguration != null)
            {
                // Use the built-in configuration directly
                return node with { HubConfiguration = builtInNode.HubConfiguration };
            }

            // NodeType not compiled yet - return with whatever default config is available
            // Use EnrichWithNodeTypeAsync for full async compilation support
            return node with { HubConfiguration = GetCachedHubConfiguration(nodeType) };
        }

        // No NodeType - return default config if available
        var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;
        if (defaultConfig != null)
        {
            return node with { HubConfiguration = defaultConfig };
        }

        return node;
    }

    /// <inheritdoc />
    public async Task<MeshNode> EnrichWithNodeTypeAsync(MeshNode node, CancellationToken ct = default)
    {
        // Skip if HubConfiguration is already set
        if (node.HubConfiguration != null)
            return node;

        var nodeType = node.NodeType;

        if (!string.IsNullOrEmpty(nodeType))
        {
            // Check if this specific nodeType config is cached
            if (_hubConfigurations.ContainsKey(nodeType))
            {
                // NodeType config is compiled - return combined config immediately
                return node with { HubConfiguration = GetCachedHubConfiguration(nodeType) };
            }

            // Check if this is a built-in NodeType (registered via AddMeshNodes)
            if (meshConfiguration.Nodes.TryGetValue(nodeType, out var builtInNode) &&
                builtInNode.HubConfiguration != null)
            {
                // Use the built-in configuration directly
                return node with { HubConfiguration = builtInNode.HubConfiguration };
            }

            // NodeType not compiled yet - trigger async compilation and wait
            logger.LogDebug("Triggering async compilation for NodeType {NodeType}", nodeType);
            await GetAssemblyPathAsync(nodeType, ct);

            // After compilation, check cache again
            return node with { HubConfiguration = GetCachedHubConfiguration(nodeType) };
        }

        // No NodeType - return default config if available
        var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;
        if (defaultConfig != null)
        {
            return node with { HubConfiguration = defaultConfig };
        }

        return node;
    }

    /// <inheritdoc />
    public async Task<Func<MessageHubConfiguration, MessageHubConfiguration>?> GetHubConfigurationAsync(Address address, CancellationToken ct = default)
    {
        logger.LogDebug("Getting HubConfiguration for {Address}", address);

        // Subscribe to the node hub with empty DataPathReference to get the MeshNode
        var workspace = meshHub.GetWorkspace();
        var stream = workspace.GetRemoteStream<MeshNode, DataPathReference>(
            address,
            new DataPathReference(""));

        // Get the first value from the stream
        var node = await stream.Select(c => c.Value).FirstOrDefaultAsync();

        return await ProcessNodeForHubConfigAsync(node, address);
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
    /// Gathers all inputs needed for compilation from persistence.
    /// Returns a NodeTypeRelease with all compilation inputs and the MeshNode.
    /// </summary>
    private async Task<(NodeTypeRelease? Release, MeshNode? Node)> GatherInputsAsync(string nodeTypePath, CancellationToken ct)
    {
        var persistence = meshHub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null)
        {
            logger.LogWarning("IPersistenceService not available for {NodeTypePath}", nodeTypePath);
            return (null, null);
        }

        // Get the node from persistence
        var node = await persistence.GetNodeAsync(nodeTypePath, ct);
        if (node == null)
        {
            logger.LogDebug("No node found in persistence for {NodeTypePath}", nodeTypePath);
            return (null, null);
        }

        // Get NodeTypeDefinition from node content
        var definition = node.Content as NodeTypeDefinition;
        if (definition == null)
        {
            logger.LogDebug("Node at {NodeTypePath} has no NodeTypeDefinition content", nodeTypePath);
            return (null, null);
        }

        // Get CodeConfigurations from /Code sub-partition
        // Collect ALL CodeConfiguration files (dataModel.json, views.json, etc.) and combine them
        var codeFiles = new List<string>();
        var codePartition = $"{nodeTypePath}/Code";
        await foreach (var obj in persistence.GetPartitionObjectsAsync(codePartition, null).WithCancellation(ct))
        {
            if (obj is CodeConfiguration codeConfig && !string.IsNullOrEmpty(codeConfig.Code))
            {
                codeFiles.Add(codeConfig.Code);
            }
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
                if (typeof(MeshNodeAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var attribute = (MeshNodeAttribute?)Activator.CreateInstance(type);
                    if (attribute != null)
                    {
                        foreach (var meshNode in attribute.Nodes)
                        {
                            var hubConfig = meshNode.HubConfiguration;
                            if (hubConfig != null)
                            {
                                var meshNodeTypePath = meshNode.NodeType ?? meshNode.Path;
                                _hubConfigurations[meshNodeTypePath] = hubConfig;

                                configurations.Add(new NodeTypeConfiguration
                                {
                                    NodeType = meshNodeTypePath,
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
            Namespace = string.Join("/", release.NodeTypePath.Split('/').SkipLast(1)),
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
        var meshQuery = meshHub.ServiceProvider.GetService<IMeshQuery>();

        if (meshQuery == null)
        {
            logger.LogWarning("IMeshQuery not available for GetCreatableTypesAsync");
            yield break;
        }

        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MeshNode? parentNode = null;
        string? currentNodeType = null;

        // Get the parent node context
        if (!string.IsNullOrEmpty(nodePath))
        {
            var nodeQuery = $"path:{nodePath}";
            await foreach (var node in meshQuery.QueryAsync<MeshNode>(nodeQuery, ct: ct).WithCancellation(ct))
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
            var childQuery = $"path:{nodePath} nodeType:NodeType scope:children";
            await foreach (var childType in meshQuery.QueryAsync<MeshNode>(childQuery, ct: ct).WithCancellation(ct))
            {
                creatableTypes.Add(childType.Path);
            }

            // Child NodeTypes under the node's NodeType path
            // e.g., ProductLaunch (nodeType: ACME/Project) can create ACME/Project/Todo
            if (!string.IsNullOrEmpty(currentNodeType) && currentNodeType != "NodeType")
            {
                var nodeTypeChildQuery = $"path:{currentNodeType} nodeType:NodeType scope:children";
                await foreach (var childType in meshQuery.QueryAsync<MeshNode>(nodeTypeChildQuery, ct: ct).WithCancellation(ct))
                {
                    creatableTypes.Add(childType.Path);
                }
            }
        }
        else
        {
            // At root: include root-level NodeTypes
            var rootQuery = "nodeType:NodeType";
            await foreach (var typeNode in meshQuery.QueryAsync<MeshNode>(rootQuery, ct: ct).WithCancellation(ct))
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
                await foreach (var nodeTypeNode in meshQuery.QueryAsync<MeshNode>(nodeTypeQuery, ct: ct).WithCancellation(ct))
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
            var typeInfo = await GetTypeInfoAsync(typePath, meshQuery, ct);
            if (typeInfo != null)
                yield return typeInfo;
        }
    }

    /// <summary>
    /// Gets CreatableTypeInfo for a type path.
    /// </summary>
    private async Task<CreatableTypeInfo?> GetTypeInfoAsync(
        string typePath,
        IMeshQuery meshQuery,
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
                DisplayOrder: GetGlobalTypeDisplayOrder(typePath)
            );
        }

        // Query for the type node
        var typeQuery = $"path:{typePath}";
        await foreach (var typeNode in meshQuery.QueryAsync<MeshNode>(typeQuery, ct: ct).WithCancellation(ct))
        {
            return CreateCreatableTypeInfoFromNode(typeNode);
        }

        // Fallback: create basic info
        return new CreatableTypeInfo(
            NodeTypePath: typePath,
            DisplayName: typePath.Split('/').Last(),
            Icon: "Code",
            Description: $"Create a {typePath.Split('/').Last()}",
            DisplayOrder: 0
        );
    }

    private static string GetGlobalTypeIcon(string globalType) => globalType switch
    {
        "Markdown" => "Document",
        "NodeType" => "Code",
        "Agent" => "Bot",
        "Thread" => "Chat",
        _ => "Code"
    };

    private static string GetGlobalTypeDescription(string globalType) => globalType switch
    {
        "Markdown" => "Create a markdown document",
        "NodeType" => "Create a new node type definition",
        "Agent" => "Create a new AI agent",
        "Thread" => "Create a new conversation thread",
        _ => $"Create a {globalType}"
    };

    private static int GetGlobalTypeDisplayOrder(string globalType) => globalType switch
    {
        "Markdown" => 1000,
        "NodeType" => 1001,
        "Agent" => 1002,
        "Thread" => 1003,
        _ => 1000
    };

    /// <summary>
    /// Creates CreatableTypeInfo from a MeshNode.
    /// </summary>
    private static CreatableTypeInfo CreateCreatableTypeInfoFromNode(MeshNode node)
    {
        var typeDef = node.Content as NodeTypeDefinition;

        return new CreatableTypeInfo(
            NodeTypePath: node.Path,
            DisplayName: typeDef?.DisplayName ?? node.Name ?? GetLastPathSegment(node.Path),
            Icon: typeDef?.Icon ?? node.Icon,
            Description: typeDef?.Description ?? node.Description,
            DisplayOrder: typeDef?.DisplayOrder ?? node.DisplayOrder ?? 0
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
        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
        _compilationTasks.Clear();
        _releaseKeys.Clear();
        _hubConfigurations.Clear();
    }

    /// <summary>
    /// Internal cache entry holding both NodeTypeData and compiled assembly path.
    /// </summary>
    private record NodeTypeCacheEntry(NodeTypeData Data, string? AssemblyPath);
}
