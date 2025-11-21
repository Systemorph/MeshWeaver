using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Pivot;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Application;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Northwind.Model;
using Xunit;

namespace MeshWeaver.Northwind.Test;

public class NorthwindTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureRouter(
        MessageHubConfiguration configuration)
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
                data.AddHubSource(
                        new ReferenceDataAddress(),
                        dataSource => dataSource.AddNorthwindReferenceData()
                    )
                    .AddHubSource(new CustomerAddress(), c => c.WithType<Customer>())
                    .AddHubSource(new ProductAddress(), c => c.WithType<Product>())
                    .AddHubSource(new EmployeeAddress(), c => c.WithType<Employee>())
                    .AddHubSource(new OrderAddress(), c => c.WithType<Order>().WithType<OrderDetails>())
                    .AddHubSource(new SupplierAddress(), c => c.WithType<Supplier>())
            )
            .AddNorthwindDataCube();
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    ) =>
        base.ConfigureClient(configuration)
            .WithTypes(typeof(ChartControl), typeof(PivotGridControl))
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource => dataSource
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
        var gridArea =  stack.Areas.Last().Area;
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

}
