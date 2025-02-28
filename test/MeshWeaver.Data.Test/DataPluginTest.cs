using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Activities;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Data.Test;

public record MyData(string Id, [property:Required]string Text)
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
                data.AddSource(
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
    ) =>
        base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource => dataSource.WithType<MyData>())
            );

    [Fact]
    public async Task InitializeTest()
    {
        var workspace = GetHost().GetWorkspace();
        var response = await workspace
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();
        response.Should().BeEquivalentTo(MyData.InitialData);
    }


    [Fact]
    public async Task Update()
    {
        // arrange
        var client = GetClient();
        var updateItems = new object[] { new MyData("1", "AAA"), new MyData("3", "CCC"), };

        var clientWorkspace = client.GetWorkspace();
        var data = (
            await clientWorkspace
                .GetObservable<MyData>()
                .Timeout(10.Seconds())
                .FirstOrDefaultAsync()
                
        )
            .OrderBy(a => a.Id)
            .ToArray();

        data.Should().HaveCount(2);

        // act
        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(updateItems),
            o => o.WithTarget(new ClientAddress())//,
            //new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token
        );

        // asserts
        updateResponse.Message.Should().BeOfType<DataChangeResponse>();
        var expectedItems = new MyData[] { new("1", "AAA"), new("2", "B"), new("3", "CCC") };

        data = (
            await clientWorkspace
                .GetObservable<MyData>()
                .Timeout(10.Seconds())
                .FirstOrDefaultAsync(x => x?.Count == 3)
        )
            .OrderBy(a => a.Id)
            .ToArray();

        data.ToArray().Should().BeEquivalentTo(expectedItems);
        data = (
            await GetHost()
                .GetWorkspace()
                .GetObservable<MyData>()
                .Timeout(10.Seconds())
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

        var data = await GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();
        data.Should().BeEquivalentTo(MyData.InitialData);

        var toBeDeleted = data.Take(1).ToArray();
        var expectedItems = data.Skip(1).ToArray();
        // act
        var deleteResponse = await client.AwaitResponse(
            DataChangeRequest.Delete(toBeDeleted, null),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token
        );


        // asserts
        data = await GetClient()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(i => i.Count == 1);
        data.Should().BeEquivalentTo(expectedItems);
        data = await GetHost()
            .GetWorkspace()
            .GetObservable<MyData>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync(i => i.Count == 1);
        data.Should().BeEquivalentTo(expectedItems);

        storage.Values.Should().BeEquivalentTo(expectedItems);
    }

    public static string TextChange = nameof(TextChange);

    public record LocalImportRequest : IRequest<ActivityLog>;

    [Fact]
    public async Task CheckUsagesFromWorkspaceVariable()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var myInstance = await workspace
            .GetObservable<MyData>("1")
            .Timeout(10.Seconds())
            .FirstAsync();
        myInstance.Text.Should().NotBe(TextChange);

        // act
        myInstance = myInstance with
        {
            Text = TextChange
        };
        workspace.Update(myInstance, new(ActivityCategory.DataUpdate, client), null);

        var hostWorkspace = GetHost().GetWorkspace();

        var instance = await hostWorkspace
            .GetObservable<MyData>("1")
            .Timeout(10.Seconds())
            .FirstAsync(i => i?.Text == TextChange)
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
    [Fact]
    public async Task ValidationFailure()
    {
        // arrange
        var client = GetClient();
        var updateItems = new object[] { new MyData("5", null) };

        // act
        var updateResponse = await client.AwaitResponse(
            DataChangeRequest.Update(updateItems, null),
            o => o.WithTarget(new ClientAddress()),
            new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token
        );

        // asserts
        var response = updateResponse.Message.Should().BeOfType<DataChangeResponse>().Which;
        response.Status.Should().Be(DataChangeStatus.Failed);
        var log = response.Log;
        log.Status.Should().Be(ActivityStatus.Failed);
        var members = log.Messages.Should().ContainSingle().Which.Scopes.FirstOrDefault(s => s.Key == "members");
        members.Value.Should().BeOfType<string[]>().Which.Single().Should().Be("Text");
    }

    [Fact]
    public async Task ReduceCollectionReference()
    {
        var host = GetHost();
        var collection = await host.GetWorkspace().GetStream(new CollectionReference(typeof(MyData).FullName), null)
            .Select(c => c.Value.Instances.Values)
            .FirstAsync();

        collection.Should().BeEquivalentTo(MyData.InitialData);
    }

}
