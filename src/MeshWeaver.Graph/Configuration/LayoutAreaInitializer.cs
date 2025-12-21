using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Initializer for registering layout areas from configuration.
/// Converts LayoutAreaConfig objects to LayoutAreaDefinitions and registers them
/// with the hub's layout definition.
/// </summary>
public class LayoutAreaInitializer : IConfigurationInitializer
{
    public int Priority => 200; // Run after data model initialization

    public Task InitializeAsync(IMessageHub hub, object[] configObjects, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<LayoutAreaInitializer>>();
        var layoutAreas = configObjects.OfType<LayoutAreaConfig>().ToList();

        if (layoutAreas.Count == 0)
        {
            logger?.LogDebug("No layout areas to register");
            return Task.CompletedTask;
        }

        logger?.LogInformation("Registering {Count} layout areas...", layoutAreas.Count);

        // Get the layout definition from the hub
        var layoutDefinition = hub.GetLayoutDefinition();

        // Register each layout area
        foreach (var config in layoutAreas)
        {
            try
            {
                var areaDefinition = new LayoutAreaDefinition(config.Area, config.Id)
                {
                    Title = config.Title,
                    Group = config.Group,
                    Order = config.Order
                };

                // Add to layout definition via extension
                layoutDefinition = layoutDefinition.WithAreaDefinition(areaDefinition);

                logger?.LogDebug("Registered layout area: {Area} (Title: {Title}, Group: {Group})",
                    config.Area, config.Title, config.Group);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Failed to register layout area {Area}", config.Area);
            }
        }

        logger?.LogInformation("Layout area initialization complete");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Extension method to get the layout definition from a hub.
/// </summary>
internal static class LayoutDefinitionHubExtensions
{
    internal static LayoutDefinition GetLayoutDefinition(this IMessageHub hub) =>
        hub.Configuration
            .Get<System.Collections.Immutable.ImmutableList<Func<LayoutDefinition, LayoutDefinition>>>()
            ?.Aggregate(new LayoutDefinition(hub), (x, y) => y.Invoke(x))
        ?? new LayoutDefinition(hub);
}
