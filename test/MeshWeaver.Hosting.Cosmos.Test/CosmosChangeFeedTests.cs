using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// Tests for Cosmos DB Change Feed integration.
/// These tests require the Cosmos DB Emulator to be running locally.
/// Tests are skipped when the emulator is not available.
/// </summary>
[Trait("Category", "Cosmos")]
public class CosmosChangeFeedTests : IAsyncLifetime
{
    private const string ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    private const string DatabaseName = "MeshWeaverChangeFeedTest";
    private const string NodesContainer = "nodes";
    private const string PartitionsContainer = "partitions";
    private const string LeasesContainer = "leases";

    private CosmosClient? _cosmosClient;
    private Database? _database;
    private bool _emulatorAvailable;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _cosmosClient = new CosmosClient(ConnectionString, new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(5)
            });

            // Try to connect to the emulator
            await _cosmosClient.ReadAccountAsync();

            // Create database and containers for testing
            var databaseResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(DatabaseName);
            _database = databaseResponse.Database;

            await _database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(NodesContainer, "/namespace"));

            await _database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(PartitionsContainer, "/partitionKey"));

            await _database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(LeasesContainer, "/id"));

            _emulatorAvailable = true;
        }
        catch
        {
            _emulatorAvailable = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_database != null)
        {
            try
            {
                await _database.DeleteAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        _cosmosClient?.Dispose();
    }

    [Fact(Timeout = 30000)]
    public async Task CosmosChangeFeedProcessor_StartsAndStops_Successfully()
    {
        if (!_emulatorAvailable)
        {
            // Skip test if emulator not available
            return;
        }

        // Arrange
        var changeNotifier = new DataChangeNotifier();
        var nodesContainer = _database!.GetContainer(NodesContainer);
        var leasesContainer = _database.GetContainer(LeasesContainer);

        var processor = new CosmosChangeFeedProcessor(
            nodesContainer,
            leasesContainer,
            changeNotifier);

        // Act & Assert - Start
        await processor.StartAsync(TestContext.Current.CancellationToken);

        // Should not throw
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act & Assert - Stop
        await processor.StopAsync();

        await processor.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task CosmosStorageAdapter_WithChangeFeedProcessor_CanBeAttached()
    {
        if (!_emulatorAvailable)
        {
            return;
        }

        // Arrange
        var changeNotifier = new DataChangeNotifier();
        var nodesContainer = _database!.GetContainer(NodesContainer);
        var partitionsContainer = _database.GetContainer(PartitionsContainer);
        var leasesContainer = _database.GetContainer(LeasesContainer);

        var storageAdapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
        var processor = new CosmosChangeFeedProcessor(
            nodesContainer,
            leasesContainer,
            changeNotifier);

        // Act
        storageAdapter.AttachChangeFeedProcessor(processor);

        await storageAdapter.StartChangeFeedProcessorAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await storageAdapter.StopChangeFeedProcessorAsync();

        // Assert - Should not throw
        await storageAdapter.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task CreateLeaseContainerAsync_CreatesContainer_WhenNotExists()
    {
        if (!_emulatorAvailable)
        {
            return;
        }

        // Arrange
        var testLeaseContainerName = $"test-leases-{Guid.NewGuid():N}";

        // Act
        var leaseContainer = await CosmosChangeFeedProcessor.CreateLeaseContainerAsync(
            _database!,
            testLeaseContainerName,
            TestContext.Current.CancellationToken);

        // Assert
        leaseContainer.Should().NotBeNull();
        leaseContainer.Id.Should().Be(testLeaseContainerName);

        // Cleanup
        await leaseContainer.DeleteContainerAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void DataChangeNotifier_SubscribersReceiveNotifications()
    {
        // This test doesn't require the emulator
        // Arrange
        var notifier = new DataChangeNotifier();
        var receivedNotifications = new List<DataChangeNotification>();

        var subscription = notifier.Subscribe(n => receivedNotifications.Add(n));

        // Act
        notifier.NotifyChange(DataChangeNotification.Created("test/path", new { Name = "Test" }));
        notifier.NotifyChange(DataChangeNotification.Updated("test/path", new { Name = "Updated" }));
        notifier.NotifyChange(DataChangeNotification.Deleted("test/path"));

        // Assert
        receivedNotifications.Should().HaveCount(3);
        receivedNotifications[0].Kind.Should().Be(DataChangeKind.Created);
        receivedNotifications[1].Kind.Should().Be(DataChangeKind.Updated);
        receivedNotifications[2].Kind.Should().Be(DataChangeKind.Deleted);

        subscription.Dispose();
    }

    [Fact]
    public void DataChangeNotifier_DisposedNotifier_DoesNotSendNotifications()
    {
        // Arrange
        var notifier = new DataChangeNotifier();
        var receivedNotifications = new List<DataChangeNotification>();

        notifier.Subscribe(n => receivedNotifications.Add(n));

        // Act
        notifier.NotifyChange(DataChangeNotification.Created("test/path", null));
        notifier.Dispose();
        notifier.NotifyChange(DataChangeNotification.Updated("test/path", null)); // Should be ignored

        // Assert
        receivedNotifications.Should().HaveCount(1);
    }

    [Fact]
    public void DataChangeNotification_StaticFactoryMethods_CreateCorrectNotifications()
    {
        // Arrange
        var entity = new { Id = "1", Name = "Test" };

        // Act
        var created = DataChangeNotification.Created("Test/Path", entity);
        var updated = DataChangeNotification.Updated("Test/Path", entity);
        var deleted = DataChangeNotification.Deleted("Test/Path", entity);

        // Assert
        created.Path.Should().Be("Test/Path"); // NormalizePath only trims slashes
        created.Kind.Should().Be(DataChangeKind.Created);
        created.Entity.Should().Be(entity);
        created.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));

        updated.Kind.Should().Be(DataChangeKind.Updated);
        deleted.Kind.Should().Be(DataChangeKind.Deleted);
    }
}
