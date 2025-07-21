using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using System;
using System.Threading;
using FluentAssertions.Extensions;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Tests for synchronization stream operations and data change management
/// </summary>
public class SynchronizationStreamTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Instance = nameof(Instance);

    /// <summary>
    /// Configures the host with MyData and object types for synchronization testing
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.AddSource(
                    dataSource =>
                        dataSource.WithType<MyData>(type =>
                            type.WithKey(instance => instance.Id)
                        ).WithType<object>(type => type.WithKey(i => i))
                )
            );
    }

    /// <summary>
    /// Tests parallel updates to the synchronization stream with concurrent modifications
    /// </summary>
    [Fact]
    public async Task ParallelUpdate()
    {
        List<MyData> tracker = new();
        var workspace = GetHost().GetWorkspace();
        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName));
        stream.Should().NotBeNull();
        stream.Reduce(new EntityReference(collectionName, Instance))!
            .Select(i => i.Value!)
            .OfType<MyData>()
            .Subscribe(tracker.Add);

        var count = 0;
        Enumerable.Range(0, 10).AsParallel().Select(_ =>
        {
            stream.Update(state =>
            {
                var instance = new MyData(Instance, (++count).ToString());
                var existingInstance = state?.Collections.GetValueOrDefault(collectionName)?.Instances
                    .GetValueOrDefault(Instance);

                return stream.ApplyChanges(
                    new EntityStoreAndUpdates(
                        WorkspaceOperations.Update((state ?? new()), collectionName, i => i.Update(Instance, instance)),
                        [new EntityUpdate(collectionName, Instance, instance) { OldValue = existingInstance }],
                        stream.StreamId)
                );
            }, _ => Task.CompletedTask);
            return true;
        }).ToArray();
        await Task.Delay(10, CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(5.Seconds()).Token
        ).Token);
        await DisposeAsync();

        tracker.Should().HaveCount(10)
            .And.Subject.Select(t => t.Text).Should().Equal(Enumerable.Range(0, 10).Select(exp => (exp + 1).ToString()));
    }
}
