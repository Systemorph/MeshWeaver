using MeshWeaver.Data;
using MeshWeaver.Mesh;
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
    /// MeshNode data type is registered automatically with persistence sync.
    /// </summary>
    public MeshHubBuilder(MessageHubConfiguration configuration)
    {
        _configuration = configuration;
        // No longer need DefaultInitializeAsync - MeshNodeTypeSource handles initialization
        _initializationFunc = null;
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
    /// Registers a data type for this hub using a runtime Type.
    /// Use this for dynamically compiled types.
    /// </summary>
    public MeshHubBuilder WithDataType(Type dataType)
    {
        DataType = dataType;
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
    /// By default, loads data from IMeshStorage for the hub's address.
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
        var dataType = DataType; // Capture for lambda

        // Configure data source with MeshNode
        config = config.AddMeshDataSource(source =>
        {
            var logger = source.Workspace.Hub.ServiceProvider.GetService<ILogger<MeshHubBuilder>>();

            logger?.LogDebug("MeshHubBuilder: Building data source for {HubPath}, dataType={DataType}",
                source.Workspace.Hub.Address, dataType?.Name ?? "null");

            // Add MeshNode with persistence sync
            var result = source.WithMeshNodes();

            // Add content type if specified
            if (dataType is not null)
            {
                logger?.LogDebug("MeshHubBuilder: Adding ContentTypeSource for {DataType}", dataType.Name);
                result = result.WithContentType(dataType);
            }
            else
            {
                // Register NodeDescription as the default entity type for generic nodes
                result = result.WithType<NodeDescription>(ts => ts.WithKey(n => n.Id));
            }

            return result;
        });

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

        // Add custom initialization if configured
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
