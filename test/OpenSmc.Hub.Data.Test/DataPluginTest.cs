using System.Collections.Immutable;
using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Hub.Data.Test;

public class DataPluginTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record MyData(string Id, string Text);

    private ImmutableDictionary<object, object> storage = ImmutableDictionary<object, object>.Empty;
    readonly MyData[] initialData =
    [
        new ("1", "A"),
        new ("2", "B")
    ];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data => data
                .FromConfigurableDataSource("ad hoc", dataSource => dataSource
                    .WithType<MyData>(type => type
                        .WithKey(instance => instance.Id)
                        .WithInitialData(InitializeMyData)
                        .WithUpdate(SaveMyData)
                    )
                )
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddData(data => data.FromHub(new HostAddress(), dataSource => dataSource
                .WithType<MyData>()));
    }

    [Fact]
    public async Task InitializeTest()
    {
        var workspace = GetWorkspace(GetHost());
        var response = await workspace.GetObservable<MyData>().FirstOrDefaultAsync();
        response.Should().BeEquivalentTo(initialData);
    }

    private IWorkspace GetWorkspace(IMessageHub hub) => hub.ServiceProvider.GetRequiredService<IWorkspace>();

    [Fact]
    public async Task Update()
    {
        // arrange
        var client = GetClient();
        var updateItems = new object[]
        {
            new MyData("1", "AAA"),
            new MyData("3", "CCC"),
        };


        // act
        var updateResponse = await client.AwaitResponse(new UpdateDataRequest(updateItems), o => o.WithTarget(new HostAddress()));

        // asserts
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();
        var expectedItems = new MyData[]
        {
            new("1", "AAA"),
            new("2", "B"),
            new("3", "CCC")
        };

        var workspace = GetWorkspace(client);
        var data = await workspace.GetObservable<MyData>().FirstOrDefaultAsync(x => x?.Count == 3);

        data.Should().BeEquivalentTo(expectedItems);
        storage.Values.Should().BeEquivalentTo(expectedItems);
        
    }

    [Fact]
    public async Task Delete()
    {
        // arrange
        var client = GetClient();
        var deleteItems = new object[]
        {
            new MyData("1", "Does not meter"),
        };

        // act
        var deleteResponse = await client.AwaitResponse(new DeleteDataRequest(deleteItems), o => o.WithTarget(new HostAddress()));

        await Task.Delay(200);

        // asserts
        deleteResponse.Message.Version.Should().Be(5L);
        var expectedItems = new[]
        {
            new MyData("2", "B")
        };
        var workspace = GetWorkspace(client);
        var data = await workspace.GetObservable<MyData>().FirstOrDefaultAsync(x => x?.Count == 1);

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
        myInstance = myInstance with { Text = TextChange };
        workspace.Update(myInstance);
        workspace.Commit();

        var hostWorkspace = GetWorkspace(GetHost());

        var instance = await hostWorkspace.GetObservable<MyData>("1").FirstAsync(i => i?.Text == TextChange);
        instance.Should().NotBeNull();

        storage.Values.Should().Contain(i => (i as MyData).Text == TextChange);
    }

    private Task<IEnumerable<MyData>> InitializeMyData(CancellationToken cancellationToken)
    {
        storage = initialData.ToImmutableDictionary(x => (object)x.Id, x => (object)x);
        return Task.FromResult<IEnumerable<MyData>>(initialData);
    }
    
    private InstancesInCollection SaveMyData(InstancesInCollection instancesInCollection)
    {
        storage = instancesInCollection.Instances;
        return instancesInCollection;
    }


}