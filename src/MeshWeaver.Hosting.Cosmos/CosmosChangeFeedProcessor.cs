using System.Text.Json;
using Microsoft.Azure.Cosmos;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Processes Cosmos DB Change Feed and publishes changes to IDataChangeNotifier.
/// </summary>
public class CosmosChangeFeedProcessor : IAsyncDisposable
{
    private readonly Container _monitoredContainer;
    private readonly Container _leaseContainer;
    private readonly IDataChangeNotifier _changeNotifier;
    private readonly ILogger<CosmosChangeFeedProcessor>? _logger;
    private readonly string _processorName;
    private ChangeFeedProcessor? _processor;

    public CosmosChangeFeedProcessor(
        Container monitoredContainer,
        Container leaseContainer,
        IDataChangeNotifier changeNotifier,
        string processorName = "MeshWeaverChangeFeedProcessor",
        ILogger<CosmosChangeFeedProcessor>? logger = null)
    {
        _monitoredContainer = monitoredContainer;
        _leaseContainer = leaseContainer;
        _changeNotifier = changeNotifier;
        _processorName = processorName;
        _logger = logger;
    }

    /// <summary>
    /// Creates a lease container for the change feed processor if it doesn't exist.
    /// </summary>
    /// <param name="database">The Cosmos database.</param>
    /// <param name="leaseContainerName">Name for the lease container (default: "{database}-leases").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lease container.</returns>
    public static async Task<Container> CreateLeaseContainerAsync(
        Database database,
        string? leaseContainerName = null,
        CancellationToken cancellationToken = default)
    {
        var containerName = leaseContainerName ?? $"{database.Id}-leases";

        var containerResponse = await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(containerName, "/id"),
            cancellationToken: cancellationToken);

        return containerResponse.Container;
    }

    /// <summary>
    /// Starts the change feed processor.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _processor = _monitoredContainer
            .GetChangeFeedProcessorBuilder<JsonElement>(_processorName, HandleChangesAsync)
            .WithInstanceName(Environment.MachineName)
            .WithLeaseContainer(_leaseContainer)
            .WithStartTime(DateTime.MinValue.ToUniversalTime()) // Process all available changes from the beginning
            .Build();

        _logger?.LogInformation(
            "Starting Cosmos Change Feed processor '{ProcessorName}' for container '{ContainerName}'",
            _processorName, _monitoredContainer.Id);

        await _processor.StartAsync();
    }

    /// <summary>
    /// Stops the change feed processor.
    /// </summary>
    public async Task StopAsync()
    {
        if (_processor != null)
        {
            _logger?.LogInformation(
                "Stopping Cosmos Change Feed processor '{ProcessorName}'",
                _processorName);

            await _processor.StopAsync();
            _processor = null;
        }
    }

    private Task HandleChangesAsync(
        ChangeFeedProcessorContext context,
        IReadOnlyCollection<JsonElement> changes,
        CancellationToken cancellationToken)
    {
        _logger?.LogDebug(
            "Change feed received {Count} changes from lease {LeaseToken}",
            changes.Count, context.LeaseToken);

        foreach (var change in changes)
        {
            try
            {
                ProcessChange(change);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing change feed item");
            }
        }

        return Task.CompletedTask;
    }

    private void ProcessChange(JsonElement change)
    {
        // Extract the path from the document
        // MeshNodes have 'namespace' and 'id' properties that form the path
        string? path = null;

        if (change.TryGetProperty("namespace", out var nsElement) &&
            change.TryGetProperty("id", out var idElement))
        {
            var ns = nsElement.GetString() ?? "";
            var id = idElement.GetString() ?? "";
            path = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
        }
        else if (change.TryGetProperty("path", out var pathElement))
        {
            path = pathElement.GetString();
        }

        if (string.IsNullOrEmpty(path))
        {
            _logger?.LogWarning("Could not determine path for change feed item");
            return;
        }

        // Cosmos Change Feed only reports creates and updates (not deletes in the default mode)
        // To detect deletes, you would need to use the "AllVersionsAndDeletes" mode with dedicated configuration
        _changeNotifier.NotifyChange(DataChangeNotification.Updated(path, null));

        _logger?.LogDebug("Published change notification for path '{Path}'", path);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
