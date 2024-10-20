using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Extensions;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Fixture;
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
        var innerPointer = JsonPointer.Parse("/displayName");
        var (stream, data) = await GetDataInstance(workspace, reference);
        var prop = innerPointer.Evaluate(data);
        prop.ToString().Should().Be("Hello");


        var patch = new JsonPatch(PatchOperation.Replace(innerPointer, "Universe"));
        var response = await client.AwaitResponse(new DataChangeRequest{Updates = [patch.Apply(data)]}, x => x.WithTarget(new HostAddress()));
        response.Message.Status.Should().Be(DataChangeStatus.Committed);
        //stream.Update(c => new ChangeItem<JsonElement>(stream.Owner, stream.Reference, patch.Apply(c), client.Address, new(() => patch), client.Version));

        var dataStream = await 
            workspace.GetRemoteStream(host.Address, new CollectionReference(typeof(DataRecord).FullName)).FirstAsync();

        var loadedInstance = (JsonObject)dataStream.Value.Instances.GetValueOrDefault("Hello");
        loadedInstance["displayName"]!.ToString().Should().Be("Universe");


        await stream.DisposeAsync();
        (_,data) = await GetDataInstance(workspace, reference);
        prop = innerPointer.Evaluate(data);
        prop.ToString().Should().Be("Universe");
    }

    private static async Task<(ISynchronizationStream<JsonElement> Stream, JsonElement Element)> GetDataInstance(IWorkspace workspace, LayoutAreaReference reference)
    {
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        var stack = content
            .Should()
            .BeOfType<LayoutStackControl>()
            .Which;

        var control = await stream.GetControlAsync(stack.Areas.Last().Area.ToString());
        var dataContext = control.Should().BeOfType<EditFormControl>().Which.DataContext;
        dataContext.Should().NotBeNullOrWhiteSpace();

        var pointer = JsonPointer.Parse(dataContext);
        var data = await stream.Reduce(new JsonPointerReference(dataContext), stream.Subscriber)
            .Timeout(3.Seconds())
            .FirstAsync();
        data.Should().NotBeNull();
        return (stream,data.Value!.Value);
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
        var content = await stream.GetControlStream(reference.Area)
            .Timeout(3.Seconds())
            .FirstAsync(x => x != null);
        var stack = content
            .Should()
            .BeOfType<LayoutStackControl>()
            .Which;

        var control = await stream.GetControlAsync(stack.Areas.Last().Area.ToString());
        var dataGrid = control.Should().BeOfType<DataGridControl>().Which;
        dataGrid.Data.Should().BeAssignableTo<IEnumerable<object>>().Which.Should().HaveCount(2);

    }
}
