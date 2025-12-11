using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

#region Domain Types

/// <summary>
/// Customer entity for testing data path combinations.
/// </summary>
public record Customer(
    [property: Key] string Id,
    string Name,
    string Email
);

/// <summary>
/// Order entity for testing data path combinations.
/// </summary>
public record Order(
    [property: Key] string Id,
    string CustomerId,
    decimal Amount,
    string Status
);

/// <summary>
/// Combined view that joins Order with Customer data.
/// Used to test virtual data paths.
/// </summary>
public record OrderSummary(
    [property: Key] string OrderId,
    string CustomerId,
    string CustomerName,
    string CustomerEmail,
    decimal Amount,
    string Status
);

#endregion

/// <summary>
/// Tests for custom data paths with global registry and subscription updates.
/// Demonstrates:
/// 1. Registering custom path prefix handlers via MeshNodeAttribute
/// 2. Combining data from multiple types into a virtual view
/// 3. Subscribing to streams and receiving updates when underlying data changes
/// </summary>
public class DataPathTest(ITestOutputHelper output) : HubTestBase(output)
{
    #region Test Configuration

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        // Note: Domain types (Customer, Order, OrderSummary) are registered by the data sources
        // with their simple names. Do NOT call WithTypes() for these types before AddData()
        // as that would register them with fully qualified names.
        return base.ConfigureHost(configuration)
            .AddData(data => data
                // Source data: Customers
                .AddSource(ds => ds.WithType<Customer>(ts => ts
                    .WithInitialData(() =>
                    [
                        new Customer("C1", "Alice Smith", "alice@example.com"),
                        new Customer("C2", "Bob Jones", "bob@example.com"),
                        new Customer("C3", "Carol White", "carol@example.com")
                    ])))
                // Source data: Orders
                .AddSource(ds => ds.WithType<Order>(ts => ts
                    .WithInitialData(() =>
                    [
                        new Order("O1", "C1", 100.00m, "Pending"),
                        new Order("O2", "C1", 250.50m, "Shipped"),
                        new Order("O3", "C2", 75.25m, "Delivered")
                    ])))
                // Virtual path: OrderSummary combines Orders with Customers
                // Accessible via DataPathReference("OrderSummary") or DataPathReference("OrderSummary/O1")
                .WithVirtualPath("OrderSummary", (workspace, entityId) =>
                {
                    var ordersStream = workspace.GetStream(typeof(Order));
                    var customersStream = workspace.GetStream(typeof(Customer));

                    return Observable.CombineLatest(
                        ordersStream,
                        customersStream,
                        (orders, customers) =>
                        {
                            var customerLookup = customers.Value!.GetData<Customer>()
                                .ToDictionary(c => c.Id);

                            var summaries = orders.Value!.GetData<Order>()
                                .Select(o =>
                                {
                                    var customer = customerLookup.GetValueOrDefault(o.CustomerId);
                                    return new OrderSummary(
                                        o.Id,
                                        o.CustomerId,
                                        customer?.Name ?? "Unknown",
                                        customer?.Email ?? "",
                                        o.Amount,
                                        o.Status
                                    );
                                })
                                .ToList();

                            // If entityId specified, return single entity; otherwise return collection
                            if (entityId != null)
                            {
                                return (object?)summaries.FirstOrDefault(s => s.OrderId == entityId);
                            }

                            // Return as InstanceCollection for collection requests
                            return new InstanceCollection(
                                summaries.ToDictionary(s => (object)s.OrderId, s => (object)s));
                        }
                    );
                })
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            // Add data extension so client knows about the workspace reference types
            .AddData()
            // Register domain types with explicit names matching the host's TypeSource names
            .WithTypes(new Dictionary<string, Type>
            {
                { "Customer", typeof(Customer) },
                { "Order", typeof(Order) },
                { "OrderSummary", typeof(OrderSummary) }
            });
    }

    #endregion

    #region DataPathReference Tests

    /// <summary>
    /// Tests that direct workspace access works and collections are registered with simple names.
    /// </summary>
    [Fact]
    public async Task DirectWorkspaceAccess_ReturnsData()
    {
        // Arrange
        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Verify: Collection names should be simple names (not fully qualified)
        var dataContext = workspace.DataContext;
        var collectionNames = dataContext.DataSourcesByCollection.Keys.ToList();
        collectionNames.Should().Contain("Order",
            $"Expected 'Order' in DataSourcesByCollection but found: {string.Join(", ", collectionNames)}");
        collectionNames.Should().Contain("Customer",
            $"Expected 'Customer' in DataSourcesByCollection but found: {string.Join(", ", collectionNames)}");

        // Act - access stream directly via workspace
        var stream = workspace.GetStream(typeof(Order));
        var data = await stream.Take(1).Timeout(TimeSpan.FromSeconds(5));

        // Assert
        var orders = data.Value!.GetData<Order>().ToArray();
        orders.Should().HaveCount(3);
    }

    /// <summary>
    /// Tests that DataPathReference resolves to a collection when path is just the collection name.
    /// </summary>
    [Fact]
    public async Task DataPathReference_Collection_ReturnsAllEntities()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Act - request collection via DataPathReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("Order")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var collection = response.Message.Data.Should().BeOfType<InstanceCollection>().Subject;
        collection.Instances.Should().HaveCount(3);
    }

    /// <summary>
    /// Tests that DataPathReference resolves to an entity when path includes entity ID.
    /// </summary>
    [Fact]
    public async Task DataPathReference_Entity_ReturnsSingleEntity()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Act - request entity via DataPathReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("Order/O1")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var order = response.Message.Data.Should().BeOfType<Order>().Subject;
        order.Id.Should().Be("O1");
        order.CustomerId.Should().Be("C1");
        order.Amount.Should().Be(100.00m);
    }

    /// <summary>
    /// Tests that DataPathReference works with virtual data sources.
    /// </summary>
    [Fact]
    public async Task DataPathReference_VirtualType_ReturnsCombinedData()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Act - request virtual collection via DataPathReference
        var response = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("OrderSummary")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var collection = response.Message.Data.Should().BeOfType<InstanceCollection>().Subject;
        collection.Instances.Should().HaveCount(3);

        // Verify the combined data
        var summaries = collection.Instances.Values.Cast<OrderSummary>().ToList();
        summaries.Should().Contain(s => s.OrderId == "O1" && s.CustomerName == "Alice Smith");
        summaries.Should().Contain(s => s.OrderId == "O2" && s.CustomerName == "Alice Smith");
        summaries.Should().Contain(s => s.OrderId == "O3" && s.CustomerName == "Bob Jones");
    }

    #endregion

    #region Subscription and Update Tests

    /// <summary>
    /// Tests that subscribing to a stream via DataPathReference receives updates
    /// when underlying data changes.
    /// </summary>
    [Fact]
    public async Task Subscription_ReceivesUpdates_WhenUnderlyingDataChanges()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Subscribe to Order collection via workspace
        // First, we need to get to the target hub and subscribe
        var subscribeResponse = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("Order")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        subscribeResponse.Message.Error.Should().BeNull();
        var initialCollection = subscribeResponse.Message.Data.Should().BeOfType<InstanceCollection>().Subject;
        initialCollection.Instances.Should().HaveCount(3);

        // Act - Update an order
        var updatedOrder = new Order("O1", "C1", 999.99m, "Updated");
        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update([updatedOrder]),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        updateResponse.Message.Should().BeOfType<DataChangeResponse>();

        // Assert - Verify the update took effect
        var verifyResponse = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("Order/O1")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        verifyResponse.Message.Error.Should().BeNull();
        var order = verifyResponse.Message.Data.Should().BeOfType<Order>().Subject;
        order.Amount.Should().Be(999.99m);
        order.Status.Should().Be("Updated");
    }

    /// <summary>
    /// Tests that virtual data source updates when underlying source data changes.
    /// </summary>
    [Fact]
    public async Task VirtualDataSource_UpdatesWhenSourceChanges()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Get initial virtual data
        var initialResponse = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("OrderSummary")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        initialResponse.Message.Error.Should().BeNull();
        var initialSummaries = (initialResponse.Message.Data as InstanceCollection)!
            .Instances.Values.Cast<OrderSummary>().ToList();

        var initialO1Summary = initialSummaries.First(s => s.OrderId == "O1");
        initialO1Summary.Amount.Should().Be(100.00m);

        // Act - Update the underlying Order
        var updatedOrder = new Order("O1", "C1", 500.00m, "Modified");
        await client.AwaitResponse(
            DataChangeRequest.Update([updatedOrder]),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Allow some time for the virtual data source to update
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert - Verify virtual data source reflects the change
        var updatedResponse = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("OrderSummary")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        updatedResponse.Message.Error.Should().BeNull();
        var updatedSummaries = (updatedResponse.Message.Data as InstanceCollection)!
            .Instances.Values.Cast<OrderSummary>().ToList();

        var updatedO1Summary = updatedSummaries.First(s => s.OrderId == "O1");
        updatedO1Summary.Amount.Should().Be(500.00m);
        updatedO1Summary.Status.Should().Be("Modified");
    }

    /// <summary>
    /// Tests that adding a new entity to the source updates the virtual data source.
    /// </summary>
    [Fact]
    public async Task VirtualDataSource_ReflectsNewEntities()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Get initial count
        var initialResponse = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("OrderSummary")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        var initialCount = (initialResponse.Message.Data as InstanceCollection)!.Instances.Count;
        initialCount.Should().Be(3);

        // Act - Add a new order
        var newOrder = new Order("O4", "C3", 300.00m, "New");
        await client.AwaitResponse(
            DataChangeRequest.Update([newOrder]),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Allow time for propagation
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert - Verify virtual data source has the new entity
        var updatedResponse = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("OrderSummary")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        var updatedSummaries = (updatedResponse.Message.Data as InstanceCollection)!
            .Instances.Values.Cast<OrderSummary>().ToList();

        updatedSummaries.Should().HaveCount(4);
        updatedSummaries.Should().Contain(s =>
            s.OrderId == "O4" &&
            s.CustomerName == "Carol White" &&
            s.Amount == 300.00m);
    }

    /// <summary>
    /// Tests that updating a customer updates the virtual OrderSummary data.
    /// </summary>
    [Fact]
    public async Task VirtualDataSource_UpdatesWhenRelatedDataChanges()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Get initial data
        var initialResponse = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("OrderSummary")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        var initialSummaries = (initialResponse.Message.Data as InstanceCollection)!
            .Instances.Values.Cast<OrderSummary>().ToList();

        // Initial customer name for C1
        initialSummaries.Where(s => s.CustomerId == "C1")
            .Should().AllSatisfy(s => s.CustomerName.Should().Be("Alice Smith"));

        // Act - Update the customer name
        var updatedCustomer = new Customer("C1", "Alice Johnson", "alice.johnson@example.com");
        await client.AwaitResponse(
            DataChangeRequest.Update([updatedCustomer]),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Allow time for propagation
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert - Verify virtual data source reflects customer update
        var updatedResponse = await client.AwaitResponse(
            new GetDataRequest(new DataPathReference("OrderSummary")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        var updatedSummaries = (updatedResponse.Message.Data as InstanceCollection)!
            .Instances.Values.Cast<OrderSummary>().ToList();

        // Orders for C1 should now show the updated customer name
        updatedSummaries.Where(s => s.CustomerId == "C1")
            .Should().AllSatisfy(s => s.CustomerName.Should().Be("Alice Johnson"));
    }

    #endregion

    #region Global Registry Tests

    /// <summary>
    /// Tests that UnifiedReference with data: prefix resolves correctly.
    /// </summary>
    [Fact]
    public async Task UnifiedReference_DataPrefix_ResolvesCorrectly()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Act - Use UnifiedReference with data: prefix
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("data/host/1/Order")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var collection = response.Message.Data.Should().BeOfType<InstanceCollection>().Subject;
        collection.Instances.Should().HaveCount(3);
    }

    /// <summary>
    /// Tests that UnifiedReference with data: prefix for entity resolves correctly.
    /// </summary>
    [Fact]
    public async Task UnifiedReference_DataPrefix_Entity_ResolvesCorrectly()
    {
        // Arrange
        GetHost();
        var client = GetClient();

        // Act - Use UnifiedReference with data: prefix for specific entity
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("data/host/1/Customer/C2")),
            o => o.WithTarget(CreateHostAddress()),
            TestContext.Current.CancellationToken);

        // Assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var customer = response.Message.Data.Should().BeOfType<Customer>().Subject;
        customer.Id.Should().Be("C2");
        customer.Name.Should().Be("Bob Jones");
    }

    #endregion
}
