using System.Reactive.Linq;
using FluentAssertions;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout;
using OpenSmc.Messaging;
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
            .AddNorthwindViews()
            .AddData(data =>
                data.FromHub(
                        new ReferenceDataAddress(),
                        dataSource => dataSource.AddNorthwindReferenceData()
                    )
                    .FromHub(new CustomerAddress(), c => c.WithType<Customer>())
                    .FromHub(new ProductAddress(), c => c.WithType<Product>())
                    .FromHub(new EmployeeAddress(), c => c.WithType<Employee>())
                    .FromHub(new OrderAddress(), c => c.WithType<Order>())
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
    }

    [Fact]
    public async Task DashboardView()
    {
        var workspace = GetClient().GetWorkspace();
        await workspace.Initialized;

        var viewName = nameof(NorthwindViews.Dashboard);
        var stream = workspace.GetStream(new HostAddress(), new LayoutAreaReference(viewName));
        var dashboard = await stream.GetControl(viewName).FirstAsync();

        dashboard.Should().BeOfType<LayoutStackControl>();
    }
}
