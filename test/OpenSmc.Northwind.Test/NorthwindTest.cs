using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using OpenSmc.Data;
using OpenSmc.GridModel;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout;
using OpenSmc.Messaging;
using OpenSmc.Northwind.Domain;
using OpenSmc.Northwind.Model;
using OpenSmc.Northwind.ViewModel;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Northwind.Test;

public class NorthwindTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureRouter(
        MessageHubConfiguration configuration
    )
    {
        return base.ConfigureRouter(configuration)
            .WithRoutes(forward =>
                forward
                    .RouteAddressToHostedHub<ReferenceDataAddress>(c =>
                        c.AddNorthwindReferenceData()
                    )
                    .RouteAddressToHostedHub<EmployeeAddress>(c => c.AddNorthwindEmployees())
                    .RouteAddressToHostedHub<OrderAddress>(c => c.AddNorthwindOrders())
                    .RouteAddressToHostedHub<SupplierAddress>(c => c.AddNorthwindSuppliers())
                    .RouteAddressToHostedHub<ProductAddress>(c => c.AddNorthwindProducts())
                    .RouteAddressToHostedHub<CustomerAddress>(c => c.AddNorthwindCustomers())
            );
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddNorthwindViewModels()
            .AddData(data =>
                data.FromHub(
                        new ReferenceDataAddress(),
                        dataSource => dataSource.AddNorthwindReferenceData()
                    )
                    .FromHub(new CustomerAddress(), c => c.WithType<Customer>())
                    .FromHub(new ProductAddress(), c => c.WithType<Product>())
                    .FromHub(new EmployeeAddress(), c => c.WithType<Employee>())
                    .FromHub(new OrderAddress(), c => c.WithType<Order>().WithType<OrderDetails>())
                    .FromHub(new SupplierAddress(), c => c.WithType<Supplier>())
            );
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.FromHub(new HostAddress(), dataSource => dataSource.AddNorthwindDomain())
            );

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

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
    public async Task DashboardView()
    {
        var workspace = GetClient().GetWorkspace();
        await workspace.Initialized;

        var viewName = nameof(NorthwindLayoutAreas.Dashboard);
        var stream = workspace.GetStream<JsonElement, LayoutAreaReference>(new HostAddress(), new LayoutAreaReference(viewName));
        var dashboard = (await stream.GetControl(viewName)).Should().BeOfType<LayoutStackControl>().Subject;
        var areas = dashboard.Areas;
        var controls = new List<KeyValuePair<string, object>>();

        while (areas.Count > 0)
        {
            var children = await areas.ToAsyncEnumerable().SelectAwait(async s => new KeyValuePair<string,object>(s, await stream.GetControl(s))).ToArrayAsync();
            controls.AddRange(children);
            areas = children.SelectMany(c => (c.Value as LayoutStackControl)?.Areas ?? Enumerable.Empty<string>()).ToArray();
        }

    }

    [Fact]
    public async Task ProductSummaryReport()
    {
        var workspace = GetHost().GetWorkspace();
        await workspace.Initialized;

        var viewName = nameof(NorthwindLayoutAreas.SupplierSummaryGrid);
        var stream = workspace.GetStream<EntityStore, LayoutAreaReference>(new HostAddress(), new LayoutAreaReference(viewName));
        var grid = (await stream.GetControl(viewName)).Should().BeOfType<GridControl>().Subject;
    }
}
