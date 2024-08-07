using System.Collections.Immutable;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Activities;
using MeshWeaver.Hub.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Data.Test;

public record MyData(string Id, string Text)
{
    public static MyData[] InitialData = [new("1", "A"), new("2", "B")];

}
public class DataPluginTest(ITestOutputHelper output) : HubTestBase(output)
{

    private ImmutableDictionary<object, object> storage = ImmutableDictionary<object, object>.Empty;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    "ad hoc",
                    dataSource =>
                        dataSource.WithType<MyData>(type =>
                            type.WithKey(instance => instance.Id)
                                .WithInitialData(InitializeMyData)
                                .WithUpdate(SaveMyData)
                        )
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(
        MessageHubConfiguration configuration
    )
    {
        return base.ConfigureClient(configuration)
            .AddData(data =>
                data.FromHub(new HostAddress(), dataSource => dataSource.WithType<MyData>())
            );
    }

    [Fact]
    public async Task InitializeTest()
    {
        var workspace = GetWorkspace(GetHost());
        await workspace.Initialized;
        var response = await workspace.GetObservable<MyData>().FirstOrDefaultAsync();
        response.Should().BeEquivalentTo(MyData.InitialData);
    }

    private IWorkspace GetWorkspace(IMessageHub hub) =>
        hub.ServiceProvider.GetRequiredService<IWorkspace>();

    [Fact]
    public async Task Update()
    {
        // arrange
        var client = GetClient();
        var updateItems = new object[] { new MyData("1", "AAA"), new MyData("3", "CCC"), };

        var timeout = TimeSpan.FromSeconds(9999);
        var clientWorkspace = GetWorkspace(client);
        await clientWorkspace.Initialized;
        var data = (
            await clientWorkspace.GetObservable<MyData>().FirstOrDefaultAsync().Timeout(timeout)
        )
            .OrderBy(a => a.Id)
            .ToArray();

        data.Should().HaveCount(2);

        // act
        var updateResponse = await client.AwaitResponse(
            new UpdateDataRequest(updateItems),
            o => o.WithTarget(new ClientAddress())
        );

        // asserts
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();
        var expectedItems = new MyData[] { new("1", "AAA"), new("2", "B"), new("3", "CCC") };

        data = (
            await clientWorkspace
                .GetObservable<MyData>()
                .Timeout(timeout)
                .FirstOrDefaultAsync(x => x?.Count == 3)
        )
            .OrderBy(a => a.Id)
            .ToArray();

        data.ToArray().Should().BeEquivalentTo(expectedItems);
        data = (
            await GetHost()
                .GetWorkspace()
                .GetObservable<MyData>()
                .Timeout(timeout)
                .FirstOrDefaultAsync(x => x?.Count == 3)
        )
            .OrderBy(a => a.Id)
            .ToArray();

        data.ToArray().Should().BeEquivalentTo(expectedItems);
        await Task.Delay(200);
        storage.Values.Cast<MyData>().OrderBy(x => x.Id).Should().BeEquivalentTo(expectedItems);
    }

    [Fact]
    public async Task Delete()
    {
        // arrange
        var client = GetClient();

        var data = await GetHost().GetWorkspace().GetObservable<MyData>().FirstOrDefaultAsync();
        data.Should().BeEquivalentTo(MyData.InitialData);

        var toBeDeleted = data.Take(1).ToArray();
        var expectedItems = data.Skip(1).ToArray();
        // act
        var deleteResponse = await client.AwaitResponse(
            new DeleteDataRequest(toBeDeleted),
            o => o.WithTarget(new ClientAddress())
        );

        await Task.Delay(200);

        // asserts
        data = await GetClient().GetWorkspace().GetObservable<MyData>().FirstOrDefaultAsync();
        data.Should().BeEquivalentTo(expectedItems);
        data = await GetHost().GetWorkspace().GetObservable<MyData>().FirstOrDefaultAsync();
        data.Should().BeEquivalentTo(expectedItems);

        storage.Values.Should().BeEquivalentTo(expectedItems);
    }

    public static string TextChange = nameof(TextChange);

    public record LocalImportRequest : IRequest<ActivityLog>;

    [Fact]
    public async Task CheckUsagesFromWorkspaceVariable()
    {
        var client = GetClient();
        var workspace = GetWorkspace(client);
        await workspace.Initialized;
        var myInstance = workspace.GetData<MyData>("1");
        myInstance.Text.Should().NotBe(TextChange);

        // act
        myInstance = myInstance with
        {
            Text = TextChange
        };
        workspace.Update(myInstance);

        var hostWorkspace = GetWorkspace(GetHost());

        var instance = await hostWorkspace
            .GetObservable<MyData>("1")
            .FirstAsync(i => i?.Text == TextChange)
            //.Timeout(TimeSpan.FromSeconds(5))
            ;
        instance.Should().NotBeNull();
        await Task.Delay(100);
        storage.Values.Should().Contain(i => (i as MyData).Text == TextChange);
    }

    private Task<IEnumerable<MyData>> InitializeMyData(CancellationToken cancellationToken)
    {
        storage = MyData.InitialData.ToImmutableDictionary(x => (object)x.Id, x => (object)x);
        return Task.FromResult<IEnumerable<MyData>>(MyData.InitialData);
    }

    private InstanceCollection SaveMyData(InstanceCollection instanceCollection)
    {
        storage = instanceCollection.Instances;
        return instanceCollection;
    }
}
