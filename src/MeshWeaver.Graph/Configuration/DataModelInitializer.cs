using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Initializer for compiling data model types and initializing the data context.
/// Compiles types from DataModel configs, registers them with the hub's data context,
/// and loads data from persistence for each node type.
/// </summary>
internal class DataModelInitializer(ITypeCompilationService typeCompiler) : IConfigurationInitializer
{
    public int Priority => 100; // Run early - types needed for other initializers

    public async Task InitializeAsync(IMessageHub hub, object[] configObjects, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<DataModelInitializer>>();
        var dataModels = configObjects.OfType<DataModel>().ToList();
        var nodeTypes = configObjects.OfType<NodeTypeConfig>().ToList();

        if (dataModels.Count == 0)
            return;

        logger?.LogInformation("Compiling {Count} data model types...", dataModels.Count);

        // Step 1: Compile all types
        var compiledTypes = await typeCompiler.CompileAllAsync(dataModels, ct);

        // Update DataModels with compiled types
        foreach (var model in dataModels)
        {
            if (compiledTypes.TryGetValue(model.Id, out var type))
                model.CompiledType = type;
        }

        logger?.LogInformation("Compiled {Count} types successfully", compiledTypes.Count);

        // Step 2: Initialize data context with types (except node types - MeshNode is handled separately)
        var persistence = hub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null)
        {
            logger?.LogWarning("No IPersistenceService available - skipping data loading");
            return;
        }

        var workspace = hub.ServiceProvider.GetService<IWorkspace>();
        if (workspace == null)
        {
            logger?.LogWarning("No IWorkspace available - skipping data context initialization");
            return;
        }

        // Step 3: Load data from persistence for each node type
        var hubPath = hub.Address.ToString();
        foreach (var nodeType in nodeTypes)
        {
            var dataModel = dataModels.FirstOrDefault(m => m.Id == nodeType.DataModelId);
            if (dataModel?.CompiledType == null)
            {
                logger?.LogWarning("No compiled type found for node type {NodeType}", nodeType.NodeType);
                continue;
            }

            try
            {
                // Load node content from persistence
                var meshNode = await persistence.GetNodeAsync(hubPath, ct);
                if (meshNode?.Content != null && dataModel.CompiledType.IsInstanceOfType(meshNode.Content))
                {
                    logger?.LogDebug("Loaded content for node {NodePath} with type {Type}",
                        hubPath, dataModel.CompiledType.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Failed to load data for node type {NodeType}", nodeType.NodeType);
            }
        }

        logger?.LogInformation("Data model initialization complete for {HubPath}", hubPath);
    }
}
