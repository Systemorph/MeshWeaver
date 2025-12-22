using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Initializer for registering layout areas from configuration.
/// Compiles ViewSource to view delegates and registers area definitions.
/// </summary>
public class LayoutAreaInitializer : IConfigurationInitializer
{
    public int Priority => 200; // Run after data model initialization

    public async Task InitializeAsync(IMessageHub hub, object[] configObjects, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<LayoutAreaInitializer>>();
        var layoutAreas = configObjects.OfType<LayoutAreaConfig>().ToList();

        if (layoutAreas.Count == 0)
        {
            logger?.LogDebug("No layout areas to register");
            return;
        }

        logger?.LogInformation("Registering {Count} layout areas...", layoutAreas.Count);

        // Get view compilation service and dynamic view registry
        var viewCompiler = hub.ServiceProvider.GetService<IViewCompilationService>();
        var viewRegistry = hub.ServiceProvider.GetService<IDynamicViewRegistry>();

        // Compile and register views with ViewSource
        var areasWithSource = layoutAreas.Where(c => !string.IsNullOrWhiteSpace(c.ViewSource)).ToList();
        if (areasWithSource.Count > 0 && viewCompiler != null && viewRegistry != null)
        {
            logger?.LogInformation("Compiling {Count} dynamic views...", areasWithSource.Count);

            foreach (var config in areasWithSource)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var compiledView = await viewCompiler.CompileViewAsync(config, ct);
                    viewRegistry.RegisterView(config.Area, compiledView, new LayoutAreaDefinition(config.Area, config.Id)
                    {
                        Title = config.Title,
                        Group = config.Group,
                        Order = config.Order
                    });

                    logger?.LogDebug("Compiled and registered dynamic view: {Area}", config.Area);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "Failed to compile view for {Area}", config.Area);
                }
            }
        }

        // Register area definitions for areas without ViewSource (metadata only)
        var areasWithoutSource = layoutAreas.Where(c => string.IsNullOrWhiteSpace(c.ViewSource));
        foreach (var config in areasWithoutSource)
        {
            try
            {
                viewRegistry?.RegisterAreaDefinition(config.Area, new LayoutAreaDefinition(config.Area, config.Id)
                {
                    Title = config.Title,
                    Group = config.Group,
                    Order = config.Order
                });

                logger?.LogDebug("Registered layout area definition: {Area} (Title: {Title}, Group: {Group})",
                    config.Area, config.Title, config.Group);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Failed to register layout area {Area}", config.Area);
            }
        }

        logger?.LogInformation("Layout area initialization complete");
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
