using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
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


        var controlFromStream = await stream.GetControlAsync(stack.Areas.Last().Area.ToString());
        var control = controlFromStream.Should().BeOfType<EditFormControl>().Which;
        var dataContext = control.DataContext;
        dataContext.Should().NotBeNullOrWhiteSpace();

        var changeStream = stream
            .Reduce(new JsonPointerReference(dataContext));

        var change = await changeStream.Where(x => x.Value is not null)
            .Timeout(3.Seconds())
            .Select(x => x.Value)
            .FirstAsync();


        change.Should().NotBeNull();
        var data = change!.Value;
        var innerPointer = JsonPointer.Parse("/displayName");
        var prop = innerPointer.Evaluate(data);
        prop.ToString().Should().Be("Hello");


        var dataPointer = JsonPointer.Parse($"{dataContext}/displayName");
        var patch = new JsonPatch(PatchOperation.Replace(innerPointer, "Universe"));
        var updated = patch.Apply(data);
        var patchForUiStream = new JsonPatch(PatchOperation.Replace(dataPointer, "Universe"));
        stream.Update(c => new ChangeItem<JsonElement>(updated, stream.StreamId, ChangeType.Patch, stream.Hub.Version, c.ToEntityUpdates(updated, patchForUiStream, stream.Hub.JsonSerializerOptions)));

        stream.Dispose();

        stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );
        change = await stream
            .Reduce(new JsonPointerReference(dataContext))
            .Where(x => x.Value is not null)
            .Timeout(3.Seconds())
            .Select(x => x.Value)
            .FirstAsync();

        change.Should().NotBeNull();
        prop = innerPointer.Evaluate(change!.Value);
        prop.ToString().Should().Be("Universe");
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
