using System.Reactive.Linq;
using FluentAssertions;
using OpenSmc.Data;
using OpenSmc.Data.Persistence;
using OpenSmc.Hub.Fixture;
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
            );
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromHub(
                        new ReferenceDataAddress(),
                        dataSource => dataSource.AddNorthwindDomain()
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

    [Fact]
    public async Task DataInitialization()
    {
        var client = GetClient();
        var categories = await client.GetWorkspace().GetObservable<Category>().FirstAsync();
        categories.Should().HaveCountGreaterThan(0);
        var territories = await client.GetWorkspace().GetObservable<Territory>().FirstAsync();
        territories.Should().HaveCountGreaterThan(0);
        var employees = await client.GetWorkspace().GetObservable<Employee>().FirstAsync();
        employees.Should().HaveCountGreaterThan(0);
        var products = await client.GetWorkspace().GetObservable<Product>().FirstAsync();
        products.Should().HaveCountGreaterThan(0);
        var orders = await client.GetWorkspace().GetObservable<Order>().FirstAsync();
        orders.Should().HaveCountGreaterThan(0);
        var suppliers = await client.GetWorkspace().GetObservable<Supplier>().FirstAsync();
        suppliers.Should().HaveCountGreaterThan(0);
    }
}
