using System.Collections.Immutable;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using FluentAssertions;
using OpenSmc.Activities;
using Xunit;
using Xunit.Abstractions;
using OpenSmc.ServiceProvider;

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
                .FromConfigurableDataSource("ad hoc",dataSource => dataSource
                    .WithType<MyData>(type => type
                        .WithKey(instance => instance.Id)
                        .WithInitialData(InitializeMyData)
                        .WithUpdate(SaveMyData)
                    )
                )
            )
            .AddPlugin<ImportPlugin>();
    }

    [Fact]
    public async Task InitializeTest()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        var expected = new GetResponse<MyData>(initialData.Length, initialData);
        response.Message.Should().BeEquivalentTo(expected);
    }

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

        await Task.Delay(300);

        // asserts
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();
        var expectedItems = new MyData[]
        {
            new("1", "AAA"),
            new("2", "B"),
            new("3", "CCC")
        };
        storage.Values.Should().BeEquivalentTo(expectedItems);
        
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        var finalExpected = new GetResponse<MyData>(expectedItems.Length, expectedItems);
        response.Message.Should().BeEquivalentTo(finalExpected);
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
        deleteResponse.Message.Version.Should().Be(1);
        var expectedItems = new[]
        {
            new MyData("2", "B")
        };
        storage.Values.Should().BeEquivalentTo(expectedItems);
        
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        response.Message.Should().BeEquivalentTo(new GetResponse<MyData>(expectedItems.Length, expectedItems));
    }


    public static string TextChange = nameof(TextChange);

    public record LocalImportRequest : IRequest<ActivityLog>;
    public class ImportPlugin(IMessageHub hub) : MessageHubPlugin(hub), IMessageHandler<LocalImportRequest>
    {
        [Inject] IWorkspace workspace;
        IMessageDelivery IMessageHandler<LocalImportRequest>.HandleMessage(IMessageDelivery<LocalImportRequest> request)
        {
            // TODO V10: Mise-en-place must be been done ==> data plugin context
            var someData = workspace.State.GetData<MyData>();
            var myInstance = someData.First();
            myInstance = myInstance with { Text = TextChange };
            workspace.Update(myInstance);
            workspace.Commit();
            Hub.Post(new ActivityLog(DateTime.UtcNow, new UserInfo("User", "User")), o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    [Fact]
    public async Task CheckUsagesFromWorkspaceVariable()
    {
        var client = GetClient();
        await client.AwaitResponse(new LocalImportRequest(), o => o.WithTarget(new HostAddress()));
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        response.Message.Items.Should().Contain(i => i.Text == TextChange);
        await Task.Delay(100);
        storage.Values.Should().Contain(i => (i as MyData).Text == TextChange);
    }

    private Task<IEnumerable<MyData>> InitializeMyData(CancellationToken cancellationToken)
    {
        storage = initialData.ToImmutableDictionary(x => (object)x.Id, x => (object)x);
        return Task.FromResult<IEnumerable<MyData>>(initialData);
    }
    
    private void SaveMyData(InstancesInCollection instancesInCollection)
    {
        storage = instancesInCollection.Instances;
    }


}