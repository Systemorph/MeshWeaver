using MeshWeaver.Data;
using MeshWeaver.Mesh;
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

        // Register MeshNode with custom TypeSource that syncs to persistence
        config = config.AddData(data => data.AddSource(source =>
        {
            var persistence = source.Workspace.Hub.ServiceProvider.GetService<IPersistenceService>();
            var hubPath = source.Workspace.Hub.Address.ToString();

            if (persistence != null)
            {
                // Use MeshNodeTypeSource which handles init + update sync to persistence
                return source.WithTypeSource(typeof(MeshNode),
                    new MeshNodeTypeSource(source.Workspace, source.Id, persistence, hubPath)
                        .WithKey(n => n.Prefix));
            }

            // Fallback: no persistence, just register type with key
            return source.WithType<MeshNode>(ts => ts.WithKey(n => n.Prefix));
        }));

        // Register additional data type if specified, otherwise use NodeDescription as default
        if (DataType is not null)
        {
            config = config.AddData(data => data.AddSource(source => source.WithType(DataType)));
        }
        else
        {
            // Register NodeDescription as the default entity type for generic nodes
            config = config.AddData(data => data.AddSource(source =>
                source.WithType<NodeDescription>(ts => ts.WithKey(n => n.Id))));
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
