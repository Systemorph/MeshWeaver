using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Builder for configuring mesh hub with graph navigation features.
/// Provides a fluent API for data type registration and persistence-based initialization.
/// </summary>
public class MeshHubBuilder
{
    private readonly MessageHubConfiguration _configuration;
    private readonly List<Func<MessageHubConfiguration, MessageHubConfiguration>> _hubConfigurations = new();
    private Func<IMessageHub, CancellationToken, Task>? _initializationFunc;
    private bool _addMeshNavigation = true;
    private Type? DataType { get; set; }

    /// <summary>
    /// Creates a new MeshHubBuilder for the given hub configuration.
    /// Registers MeshNode as a data type by default.
    /// </summary>
    public MeshHubBuilder(MessageHubConfiguration configuration)
    {
        _configuration = configuration;
        _initializationFunc = DefaultInitializeAsync;
    }

    /// <summary>
    /// Registers a data type for this hub.
    /// </summary>
    public MeshHubBuilder WithDataType<T>()
    {
        DataType = typeof(T);
        return this;
    }


    /// <summary>
    /// Adds additional hub configuration.
    /// </summary>
    public MeshHubBuilder WithHubConfiguration(Func<MessageHubConfiguration, MessageHubConfiguration> configure)
    {
        _hubConfigurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Sets a custom initialization function.
    /// By default, loads data from IPersistenceService for the hub's address.
    /// </summary>
    public MeshHubBuilder WithInitialization(Func<IMessageHub, CancellationToken, Task>? initializationFunc)
    {
        _initializationFunc = initializationFunc;
        return this;
    }


    /// <summary>
    /// Enables or disables mesh navigation (autocomplete + catalog view).
    /// Default is true.
    /// </summary>
    public MeshHubBuilder WithMeshNavigation(bool enabled = true)
    {
        _addMeshNavigation = enabled;
        return this;
    }

    /// <summary>
    /// Builds the final MessageHubConfiguration with all configured features.
    /// </summary>
    public MessageHubConfiguration Build()
    {
        var config = _configuration;

        // Register all data types
        if (DataType is not null)
        {
            config = config.AddData(data => data.AddSource(source => source.WithType(DataType)));
        }

        // Add mesh navigation (autocomplete + catalog view)
        if (_addMeshNavigation)
        {
            config = config.AddMeshNavigation();
        }


        // Apply additional hub configurations
        foreach (var hubConfig in _hubConfigurations)
        {
            config = hubConfig(config);
        }

        // Add initialization if configured
        if (_initializationFunc != null)
        {
            config = config.WithInitialization(hub =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _initializationFunc(hub, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        var logger = hub.ServiceProvider.GetService<ILogger<MeshHubBuilder>>();
                        logger?.LogError(ex, "Error during mesh hub initialization for {Address}", hub.Address);
                    }
                });
            });
        }

        return config;
    }


    /// <summary>
    /// Default initialization that loads data from IPersistenceService.
    /// Loads all nodes from the persistence service for this hub's address partition
    /// and initializes the corresponding data streams.
    /// </summary>
    private static async Task DefaultInitializeAsync(IMessageHub hub, CancellationToken ct)
    {
        var persistence = hub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null)
            return;

        var logger = hub.ServiceProvider.GetService<ILogger<MeshHubBuilder>>();
        var parentPath = hub.Address.ToString();

        logger?.LogDebug("Initializing mesh hub at {Address} from persistence", parentPath);

        // Load children from persistence
        var children = await persistence.GetChildrenAsync(parentPath, ct);
        var childList = children.ToList();

        if (childList.Count == 0)
        {
            logger?.LogDebug("No children found for {Address}", parentPath);
            return;
        }

        logger?.LogDebug("Loaded {Count} children for {Address}", childList.Count, parentPath);

        // Get workspace and initialize with loaded data
        var workspace = hub.ServiceProvider.GetService<IWorkspace>();
        if (workspace == null)
        {
            logger?.LogWarning("No workspace available for {Address}", parentPath);
            return;
        }

        // Collect all instances to update
        var allInstances = new List<object>();

        // Add MeshNode instances
        allInstances.AddRange(childList);

        // Add content instances from nodes
        var contentInstances = childList
            .Where(n => n.Content != null)
            .Select(n => n.Content!)
            .ToList();
        allInstances.AddRange(contentInstances);

        if (allInstances.Count > 0)
        {
            logger?.LogDebug("Updating workspace with {Count} instances ({NodeCount} nodes, {ContentCount} content instances)",
                allInstances.Count, childList.Count, contentInstances.Count);

            // Use workspace's Update method to add all instances
            workspace.Update(allInstances, null, null!);
        }
    }
}

/// <summary>
/// Extension methods for creating MeshHubBuilder from MessageHubConfiguration.
/// </summary>
public static class MeshHubBuilderExtensions
{
    /// <summary>
    /// Creates a MeshHubBuilder for configuring a mesh hub with graph navigation features.
    /// </summary>
    public static MeshHubBuilder ConfigureMeshHub(this MessageHubConfiguration configuration)
        => new(configuration);
}
