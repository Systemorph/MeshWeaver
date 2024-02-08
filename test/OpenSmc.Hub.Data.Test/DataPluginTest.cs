using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Hub.Data.Test;

public class DataPluginTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly Dictionary<string, MyData> storage = new();
    readonly MyData[] initialData =
    [
        new ("1", "A"),
        new ("2", "B")
    ];

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddPlugin(hub => new DataPlugin(hub, conf => conf
                .WithWorkspace(workspace => workspace.Key<MyData>(x => x.Id))
                .WithPersistence(persistence => persistence.WithType<MyData>(InitializeMyData, SaveMyData, DeleteMyData))));
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
        var expected = new DataChanged(updateItems);
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

        // asserts
        var expected = new DataChanged(deleteItems);
        deleteResponse.Message.Should().BeEquivalentTo(expected);
        var expectedItems = new MyData[]
        {
            new MyData("2", "B")
        };
        storage.Values.Should().BeEquivalentTo(expectedItems);
        
        var response = await client.AwaitResponse(new GetManyRequest<MyData>(), o => o.WithTarget(new HostAddress()));
        response.Message.Should().BeEquivalentTo(new GetManyResponse<MyData>(expectedItems.Length, expectedItems));
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

    private Task DeleteMyData(IEnumerable<MyData> items)
    {
        foreach (var id in items.Select(x => x.Id))
        {
            storage.Remove(id);
        }
        return Task.CompletedTask;
    }

    public record MyData(string Id, string Text);
}