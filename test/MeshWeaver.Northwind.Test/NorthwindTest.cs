using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Xml.Serialization;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Charting;
using MeshWeaver.Data;
using MeshWeaver.Domain.Layout.Documentation.Model;
using MeshWeaver.Fixture;
using MeshWeaver.GridModel;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Northwind.Model;
using MeshWeaver.Northwind.ViewModel;
using MeshWeaver.Reporting.Models;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Northwind.Test;

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
            .WithTypes(typeof(ChartControl), typeof(GridControl))
            .AddData(data =>
                data.FromHub(new HostAddress(), dataSource => dataSource.AddNorthwindDomain())
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
    public async Task DashboardView()
    {
        var workspace = GetClient().GetWorkspace();

        var viewName = nameof(NorthwindDashboardArea.Dashboard);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(viewName)
        );
        var dashboard = (await stream
                .GetLayoutAreaControl(viewName)
                .Timeout(3.Seconds())
                .FirstAsync()
            )
            .Should()
            .BeOfType<LayoutGridControl>()
            .Subject;
        var areas = dashboard.Areas;
        var controls = new List<KeyValuePair<string, object>>();

        while (areas.Count > 0)
        {
            var children = await areas
                .ToAsyncEnumerable()
                .SelectAwait(async s => new KeyValuePair<string, object>(
                    s.Area.ToString(),
                    await stream.GetLayoutAreaControl(s.Area.ToString()).Timeout(3.Seconds())
                    .FirstAsync()
                ))
                .ToArrayAsync();
            controls.AddRange(children);
            areas = children
                .SelectMany(c =>
                    (c.Value as LayoutStackControl)?.Areas ?? Enumerable.Empty<NamedAreaControl>()
                )
                .ToImmutableList();
        }
    }

    [Fact]
    public async Task SupplierSummaryReport()
    {
        var workspace = GetHost().GetWorkspace();

        const string ViewName = nameof(SupplierSummaryArea.SupplierSummary);
        var controlName = $"{ViewName}"; 
        var stream = workspace.GetStream(new LayoutAreaReference(ViewName));

        var control = await stream.GetLayoutAreaControl(controlName)
            .Timeout(3.Seconds())
            .FirstAsync();

        var grid = control.Should().BeOfType<GridControl>().Subject;
        grid.Data.Should()
            .BeOfType<GridOptions>()
            .Which.RowData.Should()
            .BeOfType<List<object>>()
            .Which.Should()
            .HaveCountGreaterThan(2)
            .And.Subject.First()
            .Should()
            .BeOfType<GridRow>()
            .Which.RowGroup.DisplayName.Should()
            .MatchRegex(@"[^0-9]+"); // should contain at least one non-numeric character, i.e. dimsnsion is matched.
    }

    [Fact]
    public void DocumentationTest()
    {
        var assembly = typeof(Customer).Assembly;
        var resourceName = $"{assembly.GetName().Name}.xml";
        using (var stream = assembly.GetManifestResourceStream(resourceName))
        {
            if (stream != null)
            {
                // Read the resource stream and process it as needed
                // For example, you can use StreamReader to read the content
                using (var reader = new StreamReader(stream))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(Doc));
                    Doc doc = serializer.Deserialize(reader) as Doc;
                }
            }
            else
            {
                // Resource not found
            }
        }
    }

    [Fact]
    public async Task TopProductsChart()
    {
        var workspace = GetHost().GetWorkspace();

        const string ViewName = nameof(SupplierSummaryArea.SupplierSummary);
        var controlName = $"{ViewName}"; 
        var stream = workspace.GetStream(new LayoutAreaReference(ViewName));

        var control = await stream
            .GetLayoutAreaControl(controlName)
            .Timeout(3.Seconds())
            .FirstAsync();
        var grid = control.Should().BeOfType<GridControl>().Subject;
        grid.Data.Should()
            .BeOfType<GridOptions>()
            .Which.RowData.Should()
            .BeOfType<List<object>>()
            .Which.Should()
            .HaveCountGreaterThan(2)
            .And.Subject.First()
            .Should()
            .BeOfType<GridRow>()
            .Which.RowGroup.DisplayName.Should()
            .MatchRegex(@"[^0-9]+"); // should contain at least one non-numeric character, i.e. dimsnsion is matched.
    }
}
