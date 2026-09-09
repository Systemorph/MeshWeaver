using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

public class UpdateQueuePublicationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    [Fact]
    public async Task OverlappingColdFactories_PublishOneSubscribedQueue_AndCompleteBothWrites()
    {
        const string id = "overlapping-queue-publication";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
            { Name = "initial", Description = "initial description", NodeType = "Markdown" })
            .Should().Emit();
        var cache = Cache;
        MeshNodeStreamCache.UpdateQueueEntry? nested = null;

        // Both lookups miss before either candidate is published. The nested lookup wins
        // publication; the outer caller must receive that same subscribed queue.
        var outer = cache.GetOrCreateUpdateQueue(path,
            () => nested = cache.GetOrCreateUpdateQueue(path));
        Assert.NotNull(nested);
        Assert.Same(nested, outer);
        Assert.True(nested.HasObservers);
        Assert.True(outer.HasObservers);

        using var first = new ReplaySubject<MeshNode>(1);
        using var second = new ReplaySubject<MeshNode>(1);
        Assert.True(nested.TryEnqueue(Request(path, 1, first,
            node => node with { Name = "first edit" })));
        Assert.True(outer.TryEnqueue(Request(path, 2, second,
            node => node with { Description = "second edit" })));

        var firstNode = await first.LastAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        var secondNode = await second.LastAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Assert.Equal("first edit", firstNode.Name);
        Assert.Equal("first edit", secondNode.Name);
        Assert.Equal("second edit", secondNode.Description);
    }

    [Fact]
    public async Task IdleRelease_PreservesActiveAndQueuedWrites_ThenRetiresTheSettledQueue()
    {
        const string id = "queued-write-lifetime";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
            { Name = "initial", Description = "initial description", NodeType = "Markdown" })
            .Should().Emit();
        var cache = Cache;
        var queue = cache.GetOrCreateUpdateQueue(path);
        using var first = new ReplaySubject<MeshNode>(1);
        using var second = new ReplaySubject<MeshNode>(1);
        var secondRequest = Request(path, 2, second,
            node => node with { Description = "queued edit" });
        var reentered = 0;
        Assert.True(queue.TryEnqueue(Request(path, 1, first, node =>
        {
            // The first mutation cannot hand off until this callback returns, so the
            // second request is provably queued at the attempted idle retirement.
            if (Interlocked.Exchange(ref reentered, 1) == 0)
            {
                Assert.True(queue.TryEnqueue(secondRequest));
                Assert.False(cache.TryReleaseUpdateQueue(path, TimeSpan.Zero));
                Assert.Same(queue, cache.GetOrCreateUpdateQueue(path));
                Assert.True(queue.HasObservers);
            }
            return node with { Name = "active edit" };
        })));

        await first.LastAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        var final = await second.LastAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref reentered));
        Assert.Equal("active edit", final.Name);
        Assert.Equal("queued edit", final.Description);

        // Optimistic results may precede the owner's ACK and the queue-slot handoff.
        // Wait for that actual ownership to settle, rather than assuming result completion
        // is permission to dispose the pipeline.
        await Observable.Interval(TimeSpan.FromMilliseconds(10)).StartWith(0L)
            .Where(_ => cache.TryReleaseUpdateQueue(path, TimeSpan.Zero))
            .FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Assert.False(queue.HasObservers);
        var replacement = cache.GetOrCreateUpdateQueue(path);
        Assert.NotSame(queue, replacement);
        Assert.True(replacement.HasObservers);

        using var third = new ReplaySubject<MeshNode>(1);
        var thirdRequest = Request(path, 3, third,
            node => node with { Name = "replacement edit" });
        Assert.False(queue.TryEnqueue(thirdRequest));
        Assert.True(replacement.TryEnqueue(thirdRequest));
        var replacementNode = await third.LastAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Assert.Equal("replacement edit", replacementNode.Name);
        Assert.Equal("queued edit", replacementNode.Description);
    }

    [Fact]
    public async Task QueueTermination_FaultsBothTheActiveAndBufferedCaller()
    {
        const string id = "queue-termination";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
            { Name = "initial", NodeType = "Markdown" }).Should().Emit();
        var queue = Cache.GetOrCreateUpdateQueue(path);
        using var first = new ReplaySubject<MeshNode>(1);
        using var second = new ReplaySubject<MeshNode>(1);
        var error = new InvalidOperationException("The queue owner terminated.");
        var secondStarted = 0;
        var secondRequest = Request(path, 2, second, node =>
        {
            Interlocked.Exchange(ref secondStarted, 1);
            return node with { Description = "must remain queued" };
        });
        var reentered = 0;
        Assert.True(queue.TryEnqueue(Request(path, 1, first, node =>
        {
            if (Interlocked.Exchange(ref reentered, 1) == 0)
            {
                Assert.True(queue.TryEnqueue(secondRequest));
                queue.Stop(error);
            }
            return node with { Name = "active mutation" };
        })));

        var firstTerminal = await first.Materialize().Where(notification => notification.Exception is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        var secondTerminal = await second.Materialize().Where(notification => notification.Exception is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Assert.Same(error, firstTerminal.Exception);
        Assert.Same(error, secondTerminal.Exception);
        Assert.Equal(0, Volatile.Read(ref secondStarted));
        Assert.False(queue.HasObservers);
    }

    private MeshNodeStreamCache.UpdateRequest Request(
        string path, long sequence, ReplaySubject<MeshNode> result, Func<MeshNode, MeshNode> update)
        => new(update, result, path, sequence, DateTimeOffset.UtcNow,
            Caller: Mesh.ServiceProvider.GetRequiredService<AccessService>().Context);
}
