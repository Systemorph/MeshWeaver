using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Pivot;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Application;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Northwind.Model;
using Xunit;

namespace MeshWeaver.Northwind.Test;

public class NorthwindTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TestCategoryNameSuffix = " (Updated)";

    protected override MessageHubConfiguration ConfigureRouter(
        MessageHubConfiguration configuration)
    {
        return base.ConfigureRouter(configuration)
            .WithRoutes(forward =>
                forward
                    .RouteAddressToHostedHub(NorthwindAddresses.ReferenceDataType, c =>
                        c.AddNorthwindReferenceData()
                    )
                    .RouteAddressToHostedHub(NorthwindAddresses.EmployeeType, c => c.AddNorthwindEmployees())
                    .RouteAddressToHostedHub(NorthwindAddresses.OrderType, c => c.AddNorthwindOrders())
                    .RouteAddressToHostedHub(NorthwindAddresses.SupplierType, c => c.AddNorthwindSuppliers())
                    .RouteAddressToHostedHub(NorthwindAddresses.ProductType, c => c.AddNorthwindProducts())
                    .RouteAddressToHostedHub(NorthwindAddresses.CustomerType, c => c.AddNorthwindCustomers())
            );
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddNorthwindViewModels()
            .AddData(data =>
                data.AddHubSource(
                        NorthwindAddresses.ReferenceData(),
                        dataSource => dataSource.AddNorthwindReferenceData()
                    )
                    .AddHubSource(NorthwindAddresses.Customer(), c => c.WithType<Customer>())
                    .AddHubSource(NorthwindAddresses.Product(), c => c.WithType<Product>())
                    .AddHubSource(NorthwindAddresses.Employee(), c => c.WithType<Employee>())
                    .AddHubSource(NorthwindAddresses.Order(), c => c.WithType<Order>().WithType<OrderDetails>())
                    .AddHubSource(NorthwindAddresses.Supplier(), c => c.WithType<Supplier>())
            )
            .AddNorthwindDataCube();
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureClient(configuration)
            .WithTypes(typeof(ChartControl), typeof(PivotGridControl))
            .AddData(data =>
                data.AddHubSource(CreateHostAddress(), dataSource => dataSource
                    .AddNorthwindDomain()
                    .WithType<NorthwindDataCube>())
            ).AddLayoutClient(x => x);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DataInitialization()
    {
        var client = GetClient();

        var categories = await client
            .GetWorkspace()
            .GetObservable<Category>()
            .Timeout(Timeout)
            .FirstAsync();
        categories.Should().HaveCountGreaterThan(0);
        var territories = await client
            .GetWorkspace()
            .GetObservable<Territory>()
            .Timeout(Timeout)
            .FirstAsync();
        territories.Should().HaveCountGreaterThan(0);
        var employees = await client
            .GetWorkspace()
            .GetObservable<Employee>()
            .Timeout(Timeout)
            .FirstAsync();
        employees.Should().HaveCountGreaterThan(0);
        var products = await client
            .GetWorkspace()
            .GetObservable<Product>()
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();
        products.Should().HaveCountGreaterThan(0);
        var orders = await client
            .GetWorkspace()
            .GetObservable<Order>()
            .Timeout(Timeout)
            .FirstAsync();
        orders.Should().HaveCountGreaterThan(0);
        var suppliers = await client
            .GetWorkspace()
            .GetObservable<Supplier>()
            .Timeout(Timeout)
            .FirstAsync();
        suppliers.Should().HaveCountGreaterThan(0);
        var customers = await client
            .GetWorkspace()
            .GetObservable<Customer>()
            .Timeout(Timeout)
            .FirstAsync();
        customers.Should().HaveCountGreaterThan(0);
        var orderDetails = await client
            .GetWorkspace()
            .GetObservable<OrderDetails>()
            .Timeout(Timeout)
            .FirstAsync();
        orderDetails.Should().HaveCountGreaterThan(0);
    }


    [Fact]
    public async Task SupplierSummaryReport()
    {
        var workspace = GetHost().GetWorkspace();

        const string ViewName = nameof(SupplierSummaryArea.SupplierSummary);
        var controlName = $"{ViewName}";
        var stream = workspace.GetStream(new LayoutAreaReference(ViewName));

        var control = await stream.GetControlStream(controlName)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        var gridArea = stack.Areas.Last().Area;
        control = await stream.GetControlStream(gridArea.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var pivotGrid = control.Should().BeOfType<PivotGridControl>().Subject;
        pivotGrid.Configuration.Should().NotBeNull();
        pivotGrid.Configuration.RowDimensions.Should().HaveCountGreaterThan(0);
    }


    [Fact]
    public async Task TopProductsChart()
    {
        var workspace = GetHost().GetWorkspace();

        const string ViewName = nameof(SupplierSummaryArea.SupplierSummary);
        var controlName = $"{ViewName}";
        var stream = workspace.GetStream(new LayoutAreaReference(ViewName));

        var control = await stream
            .GetControlStream(controlName)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var stack = control.Should().BeOfType<StackControl>().Subject;
        var gridArea = stack.Areas.Last().Area;
        control = await stream.GetControlStream(gridArea.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var pivotGrid = control.Should().BeOfType<PivotGridControl>().Subject;
        pivotGrid.Configuration.Should().NotBeNull();
        pivotGrid.Configuration.RowDimensions.Should().HaveCountGreaterThan(0);
    }

    /// <summary>
    /// Tests that updating a Category propagates to the NorthwindDataCube virtual data source.
    /// This verifies that the virtual data source correctly reacts to changes in source data.
    /// </summary>
    [Fact]
    public async Task CategoryUpdate_ShouldPropagateToDataCube()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Step 1: Get initial NorthwindDataCube data
        var initialCubeData = await workspace
            .GetObservable<NorthwindDataCube>()
            .Timeout(Timeout)
            .FirstAsync(x => x.Any());

        initialCubeData.Should().HaveCountGreaterThan(0, "Should have data cube entries");

        // Step 2: Get a category to update
        var categories = await workspace
            .GetObservable<Category>()
            .Timeout(Timeout)
            .FirstAsync();

        var categoryToUpdate = categories.First();
        var originalCategoryName = categoryToUpdate.CategoryName;

        Output.WriteLine($"Original category: Id={categoryToUpdate.CategoryId}, Name='{originalCategoryName}'");

        // Verify initial data cube has entries with this category
        var initialCubeEntriesWithCategory = initialCubeData
            .Where(c => c.CategoryName == originalCategoryName)
            .ToList();
        initialCubeEntriesWithCategory.Should().HaveCountGreaterThan(0,
            $"Should have data cube entries with category '{originalCategoryName}'");
        Output.WriteLine($"Found {initialCubeEntriesWithCategory.Count} data cube entries with category '{originalCategoryName}'");

        // Step 3: Update the category name
        var newCategoryName = originalCategoryName + TestCategoryNameSuffix;
        var updatedCategory = categoryToUpdate with { CategoryName = newCategoryName };

        var changeRequest = new DataChangeRequest().WithUpdates(updatedCategory);

        Output.WriteLine($"Sending update: CategoryName '{originalCategoryName}' -> '{newCategoryName}'");
        client.Post(changeRequest, o => o.WithTarget(NorthwindAddresses.ReferenceData()));

        // Step 4: Wait for the NorthwindDataCube to update with the new category name
        Output.WriteLine("Waiting for NorthwindDataCube to reflect the category name change...");

        var updatedCubeData = await workspace
            .GetObservable<NorthwindDataCube>()
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync(cubeEntries =>
                cubeEntries.Any(c => c.CategoryName == newCategoryName));

        // Step 5: Verify the data cube updated correctly
        var updatedCubeEntriesWithNewCategory = updatedCubeData
            .Where(c => c.CategoryName == newCategoryName)
            .ToList();

        updatedCubeEntriesWithNewCategory.Should().HaveCountGreaterThan(0,
            $"Should have data cube entries with updated category name '{newCategoryName}'");

        // Verify the count matches (same number of entries should now have the new name)
        updatedCubeEntriesWithNewCategory.Count.Should().Be(initialCubeEntriesWithCategory.Count,
            "Number of entries with updated category should match original count");

        Output.WriteLine($"SUCCESS: {updatedCubeEntriesWithNewCategory.Count} data cube entries now have category name '{newCategoryName}'");

        // Verify no entries remain with the old category name
        var entriesWithOldName = updatedCubeData
            .Where(c => c.CategoryName == originalCategoryName)
            .ToList();
        entriesWithOldName.Should().BeEmpty(
            $"No data cube entries should still have old category name '{originalCategoryName}'");

        Output.WriteLine("SUCCESS: Virtual data source correctly propagated category update to NorthwindDataCube");
    }

    [Fact]
    public async Task GetLayoutAreas_ShouldReturnNorthwindAreas()
    {
        var client = GetClient();

        var response = await client.AwaitResponse(
            new GetLayoutAreasRequest(),
            o => o.WithTarget(CreateHostAddress()),
            new CancellationTokenSource(Timeout).Token
        );

        var areas = response.Message.Areas.ToList();
        areas.Should().NotBeEmpty("Northwind should have layout areas defined");
        Output.WriteLine($"Found {areas.Count} layout areas");
        foreach (var area in areas)
        {
            Output.WriteLine($"  - {area.Area}: {area.Description}");
        }
    }

    [Fact]
    public async Task GetLayoutAreaStream_ShouldReturnLayoutAreasCatalog()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();

        Output.WriteLine("Getting stream for LayoutAreas...");
        var stream = workspace.GetStream(new LayoutAreaReference("LayoutAreas"))!;

        Output.WriteLine("Waiting for first emission...");
        var result = await stream
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        result.Should().NotBeNull();
        Output.WriteLine($"Got result: {result.Value}");
    }

}
