using FluentAssertions;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using Xunit;
using Xunit.Abstractions;
using static OpenSmc.Data.Test.DataPluginTest;

namespace OpenSmc.Data.Test;

public class SynchronizationStreamTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Instance = nameof(Instance);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    "ad hoc",
                    dataSource =>
                        dataSource.WithType<MyData>(type =>
                            type.WithKey(instance => instance.Id)
                        ).WithType<object>(type => type.WithKey(i => i))
                )
            );
    }

    [Fact]
    public async Task ParallelUpdate()
    {
        List<MyData> tracker = new();
        var workspace = GetHost().GetWorkspace();
        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData)).CollectionName;
        var stream = workspace.GetStreamFor(new CollectionsReference(collectionName), new ClientAddress());
        stream.Should().NotBeNull();
        stream.Reduce(new EntityReference(collectionName, Instance), new ClientAddress()).Subscribe(i => tracker.Add((MyData)i.Value));

        var count = 0;
        Enumerable.Range(0, 10).AsParallel().ForEach(_ => stream.Update(state => stream.ToChangeItem((state ?? new()).Update(collectionName, instances  => (instances??new()).Update(Instance, new MyData(Instance,(++count).ToString()))))));
        await DisposeAsync();
        tracker.Should().HaveCount(11);
        for (var i = 0; i < 10; i++)
            tracker[i].Text.Should().Be((i+1).ToString());

    }
}
