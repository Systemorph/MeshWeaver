using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

using System.Reactive.Threading.Tasks;
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
                data.AddHubSource(CreateHostAddress(), dataSource =>
                    dataSource.WithType<WorkspaceTestData>())
            );

    /// <summary>
    /// Tests that workspace initializes correctly with the predefined test data
    /// </summary>
    [Fact]
    public void Workspace_ShouldInitializeWithTestData()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();

        // act
        var data = workspace
            .GetObservable<WorkspaceTestData>()
            .Should().Within(10.Seconds())
            .Emit();

        // assert
        data.Should().NotBeNull();
        data.Should().HaveCount(3);
        data.Should().BeEquivalentTo(WorkspaceTestData.TestData, GetHost().JsonSerializerOptions);
    }

    /// <summary>
    /// Tests that workspace can retrieve a specific item by its key
    /// </summary>
    [Fact]
    public void Workspace_GetObservableWithKey_ShouldReturnSpecificItem()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();
        var expectedItem = WorkspaceTestData.TestData.First(x => x.Id == "2");

        // act
        var item = workspace
            .GetObservable<WorkspaceTestData>("2")
            .Should().Within(10.Seconds())
            .Match(x => x is not null);

        // assert
        item.Should().BeEquivalentTo(expectedItem, GetHost().JsonSerializerOptions);
    }

    /// <summary>
    /// Tests that workspace returns null when querying for a non-existent key
    /// </summary>
    [Fact]
    public void Workspace_GetObservableWithNonExistentKey_ShouldReturnNull()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();

        // act
        var item = workspace
            .GetObservable<WorkspaceTestData>("999")
            .Should().Within(5.Seconds())
            .Emit();

        // assert
        item.Should().BeNull();
    }

    /// <summary>
    /// Tests that workspace can filter items using predicate expressions
    /// </summary>
    [Fact]
    public void Workspace_FilterByPredicate_ShouldReturnMatchingItems()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();

        // act
        var activeItems = workspace
            .GetObservable<WorkspaceTestData>()
            .Select(collection => collection.Where(x => x.IsActive).ToArray())
            .Should().Within(10.Seconds())
            .Match(items => items.Length == 2);

        // assert
        activeItems.Should().HaveCount(2);
        activeItems.Should().AllSatisfy(item => item.IsActive.Should().BeTrue());
    }    /// <summary>
         /// Tests that updating an item triggers observable changes in the workspace
         /// </summary>
    [Fact(Timeout = 30000)] // 30 second timeout to prevent hanging
    public void Workspace_UpdateItem_ShouldTriggerObservableChanges()
    {
        // arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var updatedItem = new WorkspaceTestData("1", "Updated First Item", DateTime.UtcNow);

        // Count emissions reactively. Scan turns the change feed into a running
        // count; ReplaySubject buffers so the assertion below sees every tick even
        // if it subscribes after the emission. Asserting on this same signal (not a
        // captured int read out-of-band) removes the cross-subscription race.
        var changeCounts = new System.Reactive.Subjects.ReplaySubject<int>();
        var subscription = workspace
            .GetObservable<WorkspaceTestData>()
            .Scan(0, (count, _) => count + 1)
            .Subscribe(changeCounts.OnNext);

        // Wait for initial data — the first emission is change #1.
        var initialChangeCount = changeCounts.Should().Within(10.Seconds()).Match(c => c >= 1);

        // act
        client.Observe(DataChangeRequest.Update(new object[] { updatedItem }), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert — the update produces a further change event (count strictly grows).
        changeCounts.Should().Within(10.Seconds()).Match(c => c > initialChangeCount);

        // and the new value is present in the workspace.
        var currentData = workspace
            .GetObservable<WorkspaceTestData>()
            .Should().Within(10.Seconds())
            .Match(x => x.Any(item => item.Id == "1" && item.Name == "Updated First Item"));
        currentData.Should().Contain(x => x.Id == "1" && x.Name == "Updated First Item");

        subscription.Dispose();
    }    /// <summary>
         /// Tests that deleting an item removes it from the workspace collection
         /// </summary>
    [Fact(Timeout = 30000)] // 30 second timeout to prevent hanging
    public void Workspace_DeleteItem_ShouldRemoveFromCollection()
    {
        // arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var initialData = workspace
            .GetObservable<WorkspaceTestData>()
            .Should().Within(10.Seconds())
            .Emit();

        var itemToDelete = initialData.First(x => x.Id == "3");

        // act
        client.Observe(DataChangeRequest.Delete(new object[] { itemToDelete }, "TestUser"), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        // assert
        var updatedData = workspace
            .GetObservable<WorkspaceTestData>()
            .Should().Within(10.Seconds())
            .Match(x => x.Count == 2);

        updatedData.Should().HaveCount(2);
        updatedData.Should().NotContain(x => x.Id == "3");
    }

    /// <summary>
    /// Tests that data change requests with activities properly log changes
    /// </summary>
    [Fact]
    public void DataChangeRequest_WithActivity_ShouldLogChanges()
    {        // arrange
        var client = GetClient();
        var updateItem = new WorkspaceTestData("1", "Logged Update", DateTime.UtcNow);

        // act
        var response = client.Observe(DataChangeRequest.Update(new object[] { updateItem }), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

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
    public void CollectionReference_ShouldProvideStreamAccess()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();
        var collectionRef = new CollectionReference(nameof(WorkspaceTestData));

        // act
        var stream = workspace.GetStream(collectionRef);
        ((object?)stream).Should().NotBeNull();
        var collection = stream!
            .Where(c => c.Value != null)
            .Select(c => c.Value!.Instances.Values.Cast<WorkspaceTestData>().ToArray())
            .Should().Within(10.Seconds())
            .Emit();

        // assert
        collection.Should().HaveCount(3);
        collection.Should().BeEquivalentTo(WorkspaceTestData.TestData, GetHost().JsonSerializerOptions);
    }

    /// <summary>
    /// Tests that entity references provide access to specific items in the workspace
    /// </summary>
    [Fact]
    public void EntityReference_ShouldProvideSpecificItemAccess()
    {
        // arrange
        var workspace = GetHost().GetWorkspace();
        var entityRef = new EntityReference(nameof(WorkspaceTestData), "2");

        // act
        var stream = workspace.GetStream(entityRef);
        ((object?)stream).Should().NotBeNull();
        var item = stream!
            .Where(c => c.Value != null)
            .Select(c => c.Value as WorkspaceTestData)
            .Should().Within(10.Seconds())
            .Emit();

        // assert
        item.Should().NotBeNull();
        item!.Id.Should().Be("2");
        item.Name.Should().Be("Second Item");
    }    /// <summary>
         /// Tests that multiple clients can synchronize data changes across the workspace
         /// </summary>
    [Fact]
    public void Workspace_MultipleClients_ShouldSynchronizeData()
    {
        // arrange
        var client1 = GetClient();
        var client2 = GetClient();
        var updateItem = new WorkspaceTestData("1", "Multi-Client Update", DateTime.UtcNow);

        // act - update from client1
        var response = client1.Observe(DataChangeRequest.Update([updateItem]), o => o.WithTarget(CreateClientAddress()))
            .Should().Within(10.Seconds()).Emit();

        response.Message.Log.Status.Should().Be(ActivityStatus.Succeeded);
        Logger.LogInformation("*** Data Change Finished");
        // assert - client2 should see the change
        var client2Data = client2
            .GetWorkspace()
            .GetObservable<WorkspaceTestData>()
            .Should().Within(10.Seconds())
            .Match(x => x.Any(item => item.Name == "Multi-Client Update") == true);

        client2Data.Should().Contain(x => x.Id == "1" && x.Name == "Multi-Client Update");
    }
}
