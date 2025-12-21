using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Main initializer that orchestrates loading configuration and calling type-specific initializers.
/// Used within hub initialization via WithInitialization.
/// </summary>
public class GraphConfigurationInitializer(
    IConfigurationStorageService configStorage,
    IEnumerable<IConfigurationInitializer> initializers,
    ILogger logger)
{
    /// <summary>
    /// Loads all configuration and runs initializers.
    /// Pattern: Load all using ToArrayAsync(), then call InitializeAsync(loadedObjects).
    /// </summary>
    public async Task InitializeAsync(IMessageHub hub, CancellationToken ct = default)
    {
        var meshNode = new MeshNode(hub.Address.ToString());

        logger?.LogInformation("Loading configuration for node {NodePath}...", meshNode.Path);

        // Load all configuration objects at once
        var configObjects = await configStorage.LoadAllAsync(meshNode, ct).ToArrayAsync(ct);

        logger?.LogInformation("Loaded {Count} configuration objects", configObjects.Length);

        // Run initializers in priority order
        var orderedInitializers = initializers.OrderBy(i => i.Priority).ToList();

        foreach (var initializer in orderedInitializers)
        {
            try
            {
                await initializer.InitializeAsync(hub, configObjects, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Initializer {Type} failed: {Message}",
                    initializer.GetType().Name, ex.Message);
            }
        }

        logger?.LogInformation("Graph configuration initialized for {NodePath}", meshNode.Path);
    }
}
