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
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

public class DomainLayoutServiceTest(ITestOutputHelper output) : HubTestBase(output)
{

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress(ClientType, (_, d) => d.Package())
            )
            .AddData(data =>
                data.AddSource(
                    ds =>
                        ds.WithType<DataRecord>(t =>
                            t.WithInitialData(DataRecord.InitialData)
                        )
                )
            )
            .AddLayout(l => l.AddDomainLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .WithRoutes(r =>
                r.RouteAddress(HostType, (_, d) => d.Package())
            )
            .AddLayoutClient();
    }

    [HubFact]
    public async Task TestEntityView()
    {
        var reference = DomainLayoutAreas.GetDetailsReference(nameof(DataRecord), "Hello");
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var stack = content
            .Should()
            .BeOfType<StackControl>()
            .Which;

        // The last area is a reactive view that resolves to BuildPropertyForm() → StackControl
        var controlFromStream = await stream
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null)!;
        var formStack = controlFromStream.Should().BeOfType<StackControl>().Which;

        // Navigate into the form to find a control with DataContext:
        // formStack → first area (LayoutGridControl) → first property area (StackControl) → last area (reactive control with DataContext)
        var gridAreaId = formStack.Areas.First().Area.ToString()!;
        var gridControl = await stream
            .GetControlStream(gridAreaId)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var grid = gridControl.Should().BeOfType<LayoutGridControl>().Which;

        var propAreaId = grid.Areas.First().Area.ToString()!;
        var propControl = await stream
            .GetControlStream(propAreaId)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var propStack = propControl.Should().BeOfType<StackControl>().Which;

        var reactiveAreaId = propStack.Areas.Last().Area.ToString()!;
        var reactiveControl = await stream
            .GetControlStream(reactiveAreaId)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var dataContext = reactiveControl!.DataContext;
        dataContext.Should().NotBeNullOrWhiteSpace();


        var namePointer = new JsonPointerReference($"displayName");
        var nameStream = stream.DataBind<string>(namePointer, dataContext);
        var value = await nameStream
            .Where(x => x != null)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null)!;
        value.Should().NotBeNull();
        value.Should().Be("Hello");

        var objectStream = stream.DataBind<JsonElement>(new(dataContext));
        var obj = await objectStream.Timeout(10.Seconds()).FirstAsync()!;
        const string Universe = nameof(Universe);

        var jsonModel = obj.AsNode()?.ToJsonString();
        var model = new ModelParameter<JsonElement>(string.IsNullOrEmpty(jsonModel) ? default : JsonDocument.Parse(jsonModel).RootElement, (m,r)=>m.GetValueFromModel(r));
        model.Update(new JsonPatch(PatchOperation.Replace(JsonPointer.Parse("/displayName"), JsonNode.Parse($"\"{Universe}\""))));

        var log = await stream.SubmitModel(model);
        log.Status.Should().Be(ActivityStatus.Succeeded);

        value = await stream
            .DataBind<string>(namePointer, dataContext!, (x, _) => (string)x!)
            .Where(x => x != "Hello")
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null)!;
        value.Should().Be(Universe);
        stream.Dispose();
        await Task.Delay(10);
        stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );
        // After re-opening, the form is again a StackControl
        controlFromStream = await stream
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        controlFromStream.Should().BeOfType<StackControl>();
        value = await stream
            .GetDataBoundObservable<string>(namePointer, dataContext!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null)!;

        value.Should().Be(Universe);
    }


    [HubFact]
    public async Task TestCatalog()
    {
        var host = GetHost();
        var reference = DomainLayoutAreas.GetCatalogReference(nameof(DataRecord));
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            reference
        );
        var content = await stream.GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);
        var stack = content
            .Should()
            .BeOfType<StackControl>()
            .Which;

        var control = await stream
            .GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null)!;
        var dataGrid = control.Should().BeOfType<DataGridControl>().Which;
        var pointer = dataGrid.Data.Should().BeAssignableTo<JsonPointerReference>().Which;
        var dataStream = await stream
            .GetDataStream<IEnumerable<object>>(pointer)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null)!;
        dataStream.Should().BeAssignableTo<IEnumerable<object>>().Which.Should().HaveCount(2);

    }
}
