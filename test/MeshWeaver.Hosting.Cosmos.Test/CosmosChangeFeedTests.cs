using System.Threading.Tasks;
using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// Tests for Cosmos DB Change Feed integration.
///
/// <para>
/// These need a live endpoint and get it from the shared <see cref="CosmosFixture"/>. When none is
/// available they <b>skip</b> via <see cref="CosmosFixture.SkipUnlessAvailable"/> — they do NOT
/// silently return. The previous shape (<c>if (!_emulatorAvailable) return;</c>) reported PASS for
/// a test that asserted nothing, which is indistinguishable in CI from real coverage.
/// </para>
/// </summary>
[Trait("Category", "Cosmos")]
[Collection("Cosmos")]
public class CosmosChangeFeedTests(CosmosFixture fixture)
{
    [Fact(Timeout = 30000)]
    public async Task CosmosChangeFeedProcessor_StartsAndStops_Successfully()
    {
        fixture.SkipUnlessAvailable();

        // Arrange
        var changeNotifier = new System.Reactive.Subjects.Subject<DataChangeNotification>();

        var processor = new CosmosChangeFeedProcessor(
            fixture.Nodes,
            fixture.Leases,
            changeNotifier);

        // Act & Assert - Start
        await processor.StartAsync(TestContext.Current.CancellationToken);

        // Act & Assert - Stop
        await processor.StopAsync();

        await processor.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task CosmosStorageAdapter_WithChangeFeedProcessor_CanBeAttached()
    {
        fixture.SkipUnlessAvailable();

        // Arrange
        var changeNotifier = new System.Reactive.Subjects.Subject<DataChangeNotification>();

        var storageAdapter = new CosmosStorageAdapter(fixture.Nodes, fixture.Partitions);
        var processor = new CosmosChangeFeedProcessor(
            fixture.Nodes,
            fixture.Leases,
            changeNotifier);

        // Act
        storageAdapter.AttachChangeFeedProcessor(processor);

        await storageAdapter.StartChangeFeedProcessorAsync(TestContext.Current.CancellationToken);
        await storageAdapter.StopChangeFeedProcessorAsync();

        // Assert - Should not throw
        await storageAdapter.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task CreateLeaseContainerAsync_CreatesContainer_WhenNotExists()
    {
        fixture.SkipUnlessAvailable();

        // Arrange
        var testLeaseContainerName = $"test-leases-{Guid.NewGuid():N}";

        // Act
        var leaseContainer = await CosmosChangeFeedProcessor.CreateLeaseContainerAsync(
            fixture.Database,
            testLeaseContainerName,
            TestContext.Current.CancellationToken);

        // Assert
        leaseContainer.Should().NotBeNull();
        leaseContainer.Id.Should().Be(testLeaseContainerName);

        // Cleanup
        await leaseContainer.DeleteContainerAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    // Removed obsolete DataChangeNotifier_* unit tests — the framework's
    // standalone DataChangeNotifier class was deleted. Subjects (the live
    // change-feed primitive each storage adapter holds internally) are
    // System.Reactive types and don't need re-tested here.

    [Fact]
    public void DataChangeNotification_StaticFactoryMethods_CreateCorrectNotifications()
    {
        // No endpoint needed — pure factory-method assertions.

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
