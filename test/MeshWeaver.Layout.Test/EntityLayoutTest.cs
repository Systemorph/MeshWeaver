using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Extensions;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Hub.Fixture;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

public class EntityLayoutTest(ITestOutputHelper output) : HubTestBase(output)
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

        var control = await stream.GetControlAsync(stack.Areas.Last().Area.ToString());
        var dataContext = control.Should().BeOfType<EditFormControl>().Which.DataContext;
        dataContext.Should().NotBeNullOrWhiteSpace();

        var pointer = JsonPointer.Parse(dataContext);
        var data = pointer.Evaluate(stream.Current.Value);
        data.Should().NotBeNull();
        var innerPointer = JsonPointer.Parse("/displayName");
        var prop = innerPointer.Evaluate(data!.Value);
        prop.ToString().Should().Be("Hello");
        var patch = new JsonPatch(PatchOperation.Replace(pointer.Combine(innerPointer), "Universe"));
        stream.Update(c => new ChangeItem<JsonElement>(stream.Owner, stream.Reference, patch.Apply(c), client.Address, new(() => patch), client.Version));

        var dataStream = await 
            workspace.GetRemoteStream(host.Address, new CollectionReference(typeof(DataRecord).FullName)).FirstAsync();

        var loadedInstance = (JsonObject)dataStream.Value.Instances.GetValueOrDefault("Hello");
        loadedInstance["displayName"].Should().Be("Universe");
    }
}
