using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

public class DomainLayoutServiceTest(ITestOutputHelper output) : HubTestBase(output)
{

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress<ClientAddress>((_, d) => d.Package(r.Hub.JsonSerializerOptions))
            )
            .AddData(data =>
                data.FromConfigurableDataSource(
                    "Local",
                    ds =>
                        ds.WithType<DataRecord>(t =>
                            t.WithInitialData(DataRecord.InitialData)
                        )
                )
            )
            .AddDomainViews();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithRoutes(r =>
                r.RouteAddress<HostAddress>((_, d) => d.Package(r.Hub.JsonSerializerOptions))
            )
            .AddLayoutClient();
    }

    [HubFact]
    public async Task TestEntityView()
    {
        var host = GetHost();
        var reference = host.GetDetailsReference(typeof(DataRecord).FullName, "Hello");
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var content = await stream.GetLayoutAreaStream(reference.Area)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        var stack = content
            .Should()
            .BeOfType<LayoutStackControl>()
            .Which;


        var controlFromStream = await stream.GetLayoutAreaAsync(stack.Areas.Last().Area.ToString());
        var control = controlFromStream.Should().BeOfType<EditFormControl>().Which;
        var dataContext = control.DataContext;
        dataContext.Should().NotBeNullOrWhiteSpace();


        var namePointer = new JsonPointerReference($"{dataContext}/displayName");
        var nameStream = stream.DataBind<string>(namePointer);
        var value = await nameStream.Where(x => x != null).FirstAsync();
        value.Should().NotBeNull();
        value.Should().Be("Hello");

        var objectPointer = new JsonPointerReference(dataContext);
        var objectStream = stream.DataBind<JsonElement>(objectPointer);
        var obj = await objectStream.FirstAsync();
        const string Universe = nameof(Universe);
        obj = obj.SetPointer("/displayName", JsonNode.Parse($"\"{Universe}\""));

        var response = await stream.Hub.AwaitResponse(new DataChangeRequest { Updates = [obj] }, o => o.WithTarget(stream.Owner));
        response.Message.Status.Should().Be(DataChangeStatus.Committed);
        value = await stream
            .DataBind(namePointer, x => (string)x)
            .Where(x => x != "Hello")
            .Timeout(3.Seconds())
            .FirstAsync();
        value.Should().Be(Universe);

        stream.Dispose();
        stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        value = await stream
            .GetDataBoundObservable<string>(namePointer)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);

        value.Should().Be(Universe);
    }


    [HubFact]
    public async Task TestCatalog()
    {
        var host = GetHost();
        var reference = host.GetCatalogReference(typeof(DataRecord).FullName);
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var content = await stream.GetLayoutAreaStream(reference.Area)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        var stack = content
            .Should()
            .BeOfType<LayoutStackControl>()
            .Which;

        var control = await stream.GetLayoutAreaAsync(stack.Areas.Last().Area.ToString());
        var dataGrid = control.Should().BeOfType<DataGridControl>().Which;
        dataGrid.Data.Should().BeAssignableTo<IEnumerable<object>>().Which.Should().HaveCount(2);

    }
}
