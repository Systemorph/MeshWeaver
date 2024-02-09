using DocumentFormat.OpenXml.Drawing;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using FluentAssertions;
using OpenSmc.Import.Contract;
using Xunit;
using Xunit.Abstractions;
using OpenSmc.ServiceProvider;
using AngleSharp.Text;

namespace OpenSmc.Hub.Data.Test;

public class DataPluginTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record MyData(string Id, string Text);

    private readonly Dictionary<string, MyData> storage = new();
    readonly MyData[] initialData =
    [
        new ("1", "A"),
        new ("2", "B")
    ];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(c => c
                .WithType<MyData>(data => data
                    .WithKey(x => x.Id)
                    .WithInitialization(InitializeMyData)
                    .WithSave(SaveMyData)
                    .WithDelete(DeleteMyData)
                )
            )
            .AddPlugin<ImportPlugin>();
    }

    [Fact]
    public async Task InitializeTest()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        var expected = new GetManyResponse<MyData>(initialData.Length, initialData);
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
        var expected = new DataChanged(1);
        updateResponse.Message.Should().BeEquivalentTo(expected);
        var expectedItems = new MyData[]
        {
            new("1", "AAA"),
            new("2", "B"),
            new("3", "CCC")
        };
        storage.Values.Should().BeEquivalentTo(expectedItems);
        
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        var finalExpected = new GetManyResponse<MyData>(expectedItems.Length, expectedItems);
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
        var expected = new DataChanged(1);
        deleteResponse.Message.Should().BeEquivalentTo(expected);
        var expectedItems = new MyData[]
        {
            new MyData("2", "B")
        };
        storage.Values.Should().BeEquivalentTo(expectedItems);
        
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        response.Message.Should().BeEquivalentTo(new GetManyResponse<MyData>(expectedItems.Length, expectedItems));
    }


    public static string TextChange = nameof(TextChange);
    public class ImportPlugin(IMessageHub hub) : MessageHubPlugin(hub), IMessageHandler<ImportRequest>
    {
        [Inject] IWorkspace workspace;
        IMessageDelivery IMessageHandler<ImportRequest>.HandleMessage(IMessageDelivery<ImportRequest> request)
        {
            // TODO V10: Mise-en-place must be been done ==> data plugin configuration
            var someData = workspace.Query<MyData>().ToArray();
            var myInstance = someData.First();
            myInstance = myInstance with { Text = TextChange };
            workspace.Update(myInstance);
            workspace.Commit();
            Hub.Post(new DataChanged(Hub.Version), o => o.ResponseFor(request));
            return request.Processed();
        }
    }

    [Fact]
    public async Task CheckUsagesFromWorkspaceVariable()
    {
        var client = GetClient();
        var importResponse = await client.AwaitResponse(new ImportRequest(), o => o.WithTarget(new HostAddress()));
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        response.Message.Items.Should().Contain(i => i.Text == TextChange);
        await Task.Delay(100);
        storage.Values.Should().Contain(i => i.Text == TextChange);
    }

    private Task<IReadOnlyCollection<MyData>> InitializeMyData()
    {
        foreach (var data in initialData)
            storage[data.Id] = data;
        return Task.FromResult<IReadOnlyCollection<MyData>>(initialData);
    }
    
    private Task SaveMyData(IEnumerable<MyData> items)
    {
        foreach (var data in items)
        {
            storage[data.Id] = data;
        }
        return Task.CompletedTask;
    }

    private Task DeleteMyData(IEnumerable<object> instances)
    {
        foreach (var instance in instances.OfType<MyData>())
        {
            storage.Remove(instance.Id);
        }
        return Task.CompletedTask;
    }

}