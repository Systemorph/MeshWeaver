using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Json.More;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Domain;
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
                data.AddSource(
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
        var reference = DomainViews.GetDetailsReference(typeof(DataRecord).FullName, "Hello");
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var stack = content
            .Should()
            .BeOfType<StackControl>()
            .Which;


        var controlFromStream = await stream
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var control = controlFromStream.Should().BeOfType<EditFormControl>().Which;
        var dataContext = control.DataContext;
        dataContext.Should().NotBeNullOrWhiteSpace();


        var namePointer = new JsonPointerReference($"displayName");
        var nameStream = stream.DataBind<string>(namePointer, dataContext);
        var value = await nameStream
            .Where(x => x != null)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        value.Should().NotBeNull();
        value.Should().Be("Hello");

        var objectStream = stream.DataBind<JsonElement>(new(dataContext));
        var obj = await objectStream.Timeout(10.Seconds()).FirstAsync();
        const string Universe = nameof(Universe);

        var jsonModel = obj.AsNode()?.ToJsonString();
        var model = new ModelParameter<JsonElement>(string.IsNullOrEmpty(jsonModel) ? default : JsonDocument.Parse(jsonModel).RootElement, (m,r)=>m.GetValueFromModel(r));
        model.Update(new JsonPatch(PatchOperation.Replace(JsonPointer.Parse("/displayName"), JsonNode.Parse($"\"{Universe}\""))));

        var log = await stream.SubmitModel(model);
        log.Status.Should().Be(ActivityStatus.Succeeded);

        value = await stream
            .DataBind<string>(namePointer, dataContext, (x, _) => (string)x)
            .Where(x => x != "Hello")
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        value.Should().Be(Universe);
        stream.Dispose();
        await Task.Delay(10);
        stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        value = await stream
            .GetDataBoundObservable<string>(namePointer, dataContext)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        value.Should().Be(Universe);
    }


    [HubFact]
    public async Task TestCatalog()
    {
        var host = GetHost();
        var reference = DomainViews.GetCatalogReference(typeof(DataRecord).FullName);
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var stack = content
            .Should()
            .BeOfType<StackControl>()
            .Which;

        var control = await stream
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);
        var dataGrid = control.Should().BeOfType<DataGridControl>().Which;
        dataGrid.Data.Should().BeAssignableTo<IEnumerable<object>>().Which.Should().HaveCount(2);

    }
}
