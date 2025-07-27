using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Test data model for workspace testing
/// </summary>
public record WorkspaceTestData(
    string Id,
    string Name,
    DateTime CreatedAt,
    bool IsActive = true
)
{
    /// <summary>
    /// Sample test data used for workspace testing scenarios
    /// </summary>
    public static WorkspaceTestData[] TestData =
    [
        new("1", "First Item", DateTime.UtcNow.AddDays(-1)),
        new("2", "Second Item", DateTime.UtcNow.AddHours(-2)),
        new("3", "Third Item", DateTime.UtcNow.AddMinutes(-30), false)
    ];
}

/// <summary>
/// Tests for workspace data operations and persistence
/// </summary>
public class WorkspaceDataTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configures the host with WorkspaceTestData for testing workspace operations
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<WorkspaceTestData>(type =>
                        type.WithKey(instance => instance.Id)
                            .WithInitialData(_ => Task.FromResult(WorkspaceTestData.TestData.AsEnumerable()))
                    )
                )
            );
    }

    /// <summary>
    /// Configures the client to connect to host workspace data sources
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource =>
                    dataSource.WithType<WorkspaceTestData>())
            );

    /// <summary>
    /// Tests that workspace initializes correctly with the predefined test data
    /// </summary>
    [Fact]
    public async Task Workspace_ShouldInitializeWithTestData()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();

        // act
        var data = await workspace
            .GetObservable<WorkspaceTestData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        // assert
        data.Should().NotBeNull();
        data.Should().HaveCount(3);
        data.Should().BeEquivalentTo(WorkspaceTestData.TestData);
    }

    /// <summary>
    /// Tests that workspace can retrieve a specific item by its key
    /// </summary>
    [Fact]
    public async Task Workspace_GetObservableWithKey_ShouldReturnSpecificItem()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();
        var expectedItem = WorkspaceTestData.TestData.First(x => x.Id == "2");

        // act
        var item = await workspace
            .GetObservable<WorkspaceTestData>("2")
            .Timeout(10.Seconds())
            .FirstAsync();

        // assert
        item.Should().BeEquivalentTo(expectedItem);
    }

    /// <summary>
    /// Tests that workspace returns null when querying for a non-existent key
    /// </summary>
    [Fact]
    public async Task Workspace_GetObservableWithNonExistentKey_ShouldReturnNull()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();

        // act
        var item = await workspace
            .GetObservable<WorkspaceTestData>("999")
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        // assert
        item.Should().BeNull();
    }

    /// <summary>
    /// Tests that workspace can filter items using predicate expressions
    /// </summary>
    [Fact]
    public async Task Workspace_FilterByPredicate_ShouldReturnMatchingItems()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();

        // act
        var activeItems = await workspace
            .GetObservable<WorkspaceTestData>()
            .Select(collection => collection.Where(x => x.IsActive).ToArray())
            .Timeout(10.Seconds())
            .FirstAsync();

        // assert
        activeItems.Should().HaveCount(2);
        activeItems.Should().AllSatisfy(item => item.IsActive.Should().BeTrue());
    }    /// <summary>
         /// Tests that updating an item triggers observable changes in the workspace
         /// </summary>
    [Fact(Timeout = 30000)] // 30 second timeout to prevent hanging
    public async Task Workspace_UpdateItem_ShouldTriggerObservableChanges()
    {
        // arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var updatedItem = new WorkspaceTestData("1", "Updated First Item", DateTime.UtcNow);

        // Create observable to monitor changes
        var changeCount = 0;
        var subscription = workspace
            .GetObservable<WorkspaceTestData>()
            .Subscribe(_ => changeCount++);

        // Wait for initial data
        await workspace
            .GetObservable<WorkspaceTestData>()
            .Timeout(10.Seconds())
            .FirstAsync();

        var initialChangeCount = changeCount;

        // act
        await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { updatedItem }),
            o => o.WithTarget(new ClientAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );

        // Wait a bit for the change to propagate
        await Task.Delay(100, CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(5.Seconds()).Token
        ).Token);

        // assert
        changeCount.Should().BeGreaterThan(initialChangeCount);

        var currentData = await workspace
            .GetObservable<WorkspaceTestData>()
            .Timeout(10.Seconds())
            .FirstAsync();

        currentData.Should().Contain(x => x.Id == "1" && x.Name == "Updated First Item");

        subscription.Dispose();
    }    /// <summary>
         /// Tests that deleting an item removes it from the workspace collection
         /// </summary>
    [Fact(Timeout = 30000)] // 30 second timeout to prevent hanging
    public async Task Workspace_DeleteItem_ShouldRemoveFromCollection()
    {
        // arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var initialData = await workspace
            .GetObservable<WorkspaceTestData>()
            .Timeout(10.Seconds())
            .FirstAsync();

        var itemToDelete = initialData.First(x => x.Id == "3");

        // act
        await client.AwaitResponse(
            DataChangeRequest.Delete(new object[] { itemToDelete }, "TestUser"),
            o => o.WithTarget(new ClientAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );

        // assert
        var updatedData = await workspace
            .GetObservable<WorkspaceTestData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(x => x.Count == 2);

        updatedData.Should().HaveCount(2);
        updatedData.Should().NotContain(x => x.Id == "3");
    }

    /// <summary>
    /// Tests that data change requests with activities properly log changes
    /// </summary>
    [Fact]
    public async Task DataChangeRequest_WithActivity_ShouldLogChanges()
    {        // arrange
        var client = GetClient();
        var updateItem = new WorkspaceTestData("1", "Logged Update", DateTime.UtcNow);

        // act
        var response = await client.AwaitResponse(
            DataChangeRequest.Update(new object[] { updateItem }),
            o => o.WithTarget(new ClientAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );

        // assert
        var dataChangeResponse = response.Message.Should().BeOfType<DataChangeResponse>().Which;
        dataChangeResponse.Log.Should().NotBeNull();
        dataChangeResponse.Log.Category.Should().Be(ActivityCategory.DataUpdate);
        dataChangeResponse.Status.Should().Be(DataChangeStatus.Committed);
    }
    /// <summary>
    /// Tests that collection references provide proper stream access to collections
    /// </summary>
    [Fact]
    public async Task CollectionReference_ShouldProvideStreamAccess()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();
        var collectionRef = new CollectionReference(nameof(WorkspaceTestData));

        // act
        var stream = workspace.GetStream(collectionRef);
        var collection = await stream
            .Select(c => c.Value!.Instances.Values.Cast<WorkspaceTestData>().ToArray())
            .Timeout(10.Seconds())
            .FirstAsync();

        // assert
        collection.Should().HaveCount(3);
        collection.Should().BeEquivalentTo(WorkspaceTestData.TestData);
    }

    /// <summary>
    /// Tests that entity references provide access to specific items in the workspace
    /// </summary>
    [Fact]
    public async Task EntityReference_ShouldProvideSpecificItemAccess()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();
        var entityRef = new EntityReference(nameof(WorkspaceTestData), "2");

        // act
        var stream = workspace.GetStream(entityRef);
        var item = await stream
            .Select(c => c.Value as WorkspaceTestData)
            .Timeout(10.Seconds())
            .FirstAsync();

        // assert
        item.Should().NotBeNull();
        item.Id.Should().Be("2");
        item.Name.Should().Be("Second Item");
    }    /// <summary>
         /// Tests that multiple clients can synchronize data changes across the workspace
         /// </summary>
    [Fact(Timeout = 30000)] // 30 second timeout to prevent hanging
    public async Task Workspace_MultipleClients_ShouldSynchronizeData()
    {
        // arrange
        var client1 = GetClient();
        var client2 = GetClient();
        var updateItem = new WorkspaceTestData("1", "Multi-Client Update", DateTime.UtcNow);

        // act - update from client1
        await client1.AwaitResponse(
            DataChangeRequest.Update(new object[] { updateItem }),
            o => o.WithTarget(new ClientAddress()),
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken,
                new CancellationTokenSource(10.Seconds()).Token
            ).Token
        );

        // assert - client2 should see the change
        var client2Data = await client2
            .GetWorkspace()
            .GetObservable<WorkspaceTestData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(x => x.Any(item => item.Name == "Multi-Client Update") == true);

        client2Data.Should().Contain(x => x.Id == "1" && x.Name == "Multi-Client Update");
    }
}
