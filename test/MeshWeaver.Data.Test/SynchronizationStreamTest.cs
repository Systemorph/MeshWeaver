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
using System.Collections.Immutable;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Base interface for testing Select projection
/// </summary>
public interface IBaseData
{
    string Id { get; }
}

/// <summary>
/// Derived type implementing IBaseData for Select projection tests
/// </summary>
public record DerivedData(string Id, string ExtraInfo) : IBaseData;

/// <summary>
/// Tests for synchronization stream operations and data change management
/// </summary>
public class SynchronizationStreamTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Instance = nameof(Instance);

    /// <summary>
    /// Configures the host with MyData, DerivedData and object types for synchronization testing
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
                        ).WithType<DerivedData>(type =>
                            type.WithKey(d => d.Id)
                        ).WithType<object>(type => type.WithKey(i => i))
                )
            );
    }

    /// <summary>
    /// Tests parallel updates to the synchronization stream with concurrent modifications
    /// </summary>
    [Fact(Skip = "Unstable")]
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
        await Task.Delay(100, CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            new CancellationTokenSource(5.Seconds()).Token
        ).Token);
        await DisposeAsync();

        tracker.Should().HaveCount(10)
            .And.Subject.Select(t => t.Text).Should().Equal(Enumerable.Range(0, 10).Select(exp => (exp + 1).ToString()));
    }

    /// <summary>
    /// Tests that Select projects ISynchronizationStream&lt;DerivedData&gt; to ISynchronizationStream&lt;IBaseData&gt;
    /// </summary>
    [Fact]
    public async Task SelectProjectsDerivedToBaseType()
    {
        // Arrange: configure workspace with DerivedData
        var workspace = GetHost().GetWorkspace();
        var collectionName = workspace.DataContext.GetTypeSource(typeof(DerivedData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName));
        stream.Should().NotBeNull();

        // Reduce to get a stream of DerivedData entities
        var derivedStream = stream.Reduce(new EntityReference(collectionName, "1"))!
            .Select(i => i.Value)
            .Where(v => v is DerivedData)
            .Select(v => (DerivedData)v!)
            .Replay(1);
        derivedStream.Connect();

        // Act: use Select to project DerivedData to IBaseData
        var baseDataStream = stream.Reduce(new EntityReference(collectionName, "1"))!
            .Select(d => (IBaseData)d!);

        List<IBaseData> receivedBaseData = new();
        baseDataStream.Subscribe(change =>
        {
            if (change.Value is not null)
                receivedBaseData.Add(change.Value);
        });

        // Add a DerivedData instance
        var instance = new DerivedData("1", "Extra information");
        stream.Update(state =>
        {
            var store = WorkspaceOperations.Update(state ?? new(), collectionName, c => c.Update("1", instance));
            return stream.ApplyChanges(new EntityStoreAndUpdates(
                store,
                [new EntityUpdate(collectionName, "1", instance)],
                stream.StreamId));
        });

        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert: the Select stream should have received the projected IBaseData
        receivedBaseData.Should().HaveCount(1);
        receivedBaseData[0].Should().BeOfType<DerivedData>();
        receivedBaseData[0].Id.Should().Be("1");
    }

    /// <summary>
    /// Tests that Select can transform stream values using a lambda expression
    /// </summary>
    [Fact]
    public async Task SelectTransformsValues()
    {
        // Arrange
        var workspace = GetHost().GetWorkspace();
        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName));
        stream.Should().NotBeNull();

        // Act: use Select to extract just the Text property as a string
        var textStream = stream.Reduce(new EntityReference(collectionName, "test"))!
            .Select(obj => obj is MyData data ? data.Text : null!);

        List<string> receivedTexts = new();
        textStream.Subscribe(change =>
        {
            if (change.Value is not null)
                receivedTexts.Add(change.Value);
        });

        // Add MyData instances with different Text values
        stream.Update(state =>
        {
            var instance = new MyData("test", "Hello World");
            var store = WorkspaceOperations.Update(state ?? new(), collectionName, c => c.Update("test", instance));
            return stream.ApplyChanges(new EntityStoreAndUpdates(
                store,
                [new EntityUpdate(collectionName, "test", instance)],
                stream.StreamId));
        });

        await Task.Delay(100, TestContext.Current.CancellationToken);

        stream.Update(state =>
        {
            var instance = new MyData("test", "Updated Text");
            var store = WorkspaceOperations.Update(state ?? new(), collectionName, c => c.Update("test", instance));
            return stream.ApplyChanges(new EntityStoreAndUpdates(
                store,
                [new EntityUpdate(collectionName, "test", instance)],
                stream.StreamId));
        });

        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert: should have received both text values
        receivedTexts.Should().HaveCount(2);
        receivedTexts.Should().ContainInOrder("Hello World", "Updated Text");
    }

}
