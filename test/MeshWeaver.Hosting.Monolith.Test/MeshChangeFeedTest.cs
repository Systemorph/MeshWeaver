using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for the MeshChangeFeed: events are published on create/delete,
/// filtered subscriptions work, and path resolver cache is invalidated correctly.
/// </summary>
public class MeshChangeFeedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph();

    private IMeshChangeFeed ChangeFeed => Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
    // The base class also exposes PathResolver; this accessor intentionally re-declares it
    // to resolve via the local Mesh hub rather than the base-class SP.
    private new IPathResolver PathResolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
    private CancellationToken Ct => new CancellationTokenSource(10_000).Token;

    private async Task<MeshNode> CreateTestNodeAsync(string id, string? ns = null)
    {
        var node = new MeshNode(id, ns) { Name = $"Test {id}", NodeType = "Markdown" };
        var response = await Mesh.Observe(new CreateNodeRequest(node), o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(Ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!;
    }

    private async Task DeleteTestNodeAsync(string path)
    {
        var response = await Mesh.Observe(new DeleteNodeRequest(path), o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(Ct);
        response.Message.Error.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task CreateNode_PublishesCreatedEvent()
    {
        var events = new List<MeshChangeEvent>();
        using var sub = ChangeFeed.Subscribe(e => events.Add(e));

        await CreateTestNodeAsync("feed-create-1");

        events.Should().Contain(e => e.Kind == MeshChangeKind.Created && e.Id == "feed-create-1");
    }

    [Fact]
    public async Task DeleteNode_PublishesDeletedEvent()
    {
        var created = await CreateTestNodeAsync("feed-del-1");

        var events = new List<MeshChangeEvent>();
        using var sub = ChangeFeed.Subscribe(e => events.Add(e));

        await DeleteTestNodeAsync(created.Path);

        events.Should().Contain(e => e.Kind == MeshChangeKind.Deleted && e.Path.Contains("feed-del-1"));
    }

    [Fact]
    public async Task FilteredSubscription_OnlyReceivesMatchingEvents()
    {
        var createEvents = new List<MeshChangeEvent>();
        var deleteEvents = new List<MeshChangeEvent>();
        using var createSub = ChangeFeed.Subscribe(e => createEvents.Add(e), MeshChangeKind.Created);
        using var deleteSub = ChangeFeed.Subscribe(e => deleteEvents.Add(e), MeshChangeKind.Deleted);

        var created = await CreateTestNodeAsync("feed-filter-1");
        await DeleteTestNodeAsync(created.Path);

        createEvents.Should().OnlyContain(e => e.Kind == MeshChangeKind.Created);
        deleteEvents.Should().OnlyContain(e => e.Kind == MeshChangeKind.Deleted);
    }

    [Fact]
    public async Task CreateNode_PathResolverFindsIt()
    {
        // Resolve before create Ã¢â‚¬â€ should not find it
        var before = await PathResolver.ResolvePath("feed-resolve-1").FirstAsync().ToTask();

        await CreateTestNodeAsync("feed-resolve-1");

        // After create Ã¢â‚¬â€ cache was invalidated/pre-warmed by change event
        var after = await PathResolver.ResolvePath("feed-resolve-1").FirstAsync().ToTask();
        after.Should().NotBeNull();
        after!.Prefix.Should().Contain("feed-resolve-1");
        after.Remainder.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task DeleteNode_PathResolverNoLongerFindsIt()
    {
        var created = await CreateTestNodeAsync("feed-gone-1");

        // Verify resolver finds it
        var exists = await PathResolver.ResolvePath(created.Path).FirstAsync().ToTask();
        exists.Should().NotBeNull();

        await DeleteTestNodeAsync(created.Path);

        // After delete Ã¢â‚¬â€ cache evicted, resolver should not find it at that exact path
        var gone = await PathResolver.ResolvePath(created.Path).FirstAsync().ToTask();
        (gone == null || gone.Prefix != created.Path).Should().BeTrue(
            "deleted node should not resolve to its exact path");
    }

    [Fact]
    public async Task NestedCreate_EvictsParentPartialMatch()
    {
        // Create parent
        var parent = await CreateTestNodeAsync("nest-parent-1");

        // Resolve nested path Ã¢â‚¬â€ caches partial match (parent with remainder)
        var partial = await PathResolver.ResolvePath($"{parent.Path}/nest-child-1").FirstAsync().ToTask();

        // Create child
        await CreateTestNodeAsync("nest-child-1", parent.Path);

        // Now nested path should resolve to child (stale cache evicted by Created event)
        var afterChild = await PathResolver.ResolvePath($"{parent.Path}/nest-child-1").FirstAsync().ToTask();
        afterChild.Should().NotBeNull();
        afterChild!.Prefix.Should().Be($"{parent.Path}/nest-child-1");
        afterChild.Remainder.Should().BeNullOrEmpty();
    }
}

