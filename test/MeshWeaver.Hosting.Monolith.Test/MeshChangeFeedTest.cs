using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph();

    private IMeshChangeFeed ChangeFeed => Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
    // The base class also exposes PathResolver; this accessor intentionally re-declares it
    // to resolve via the local Mesh hub rather than the base-class SP.
    private new IPathResolver PathResolver => Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
    private CancellationToken Ct => new CancellationTokenSource(10_000).Token;

    private MeshNode CreateTestNode(string id, string? ns = null)
    {
        // Top-level fixtures (empty namespace) are partition roots; the PartitionWriteGuard
        // rejects a normal user creating a non-partition-owning type (Markdown) there. These
        // nodes only need to EXIST for the change-feed / path-resolver assertions, so seed
        // them under the System identity (the legitimate partition provisioner) which bypasses
        // the guard. SeedTopLevel routes through IMeshService.CreateNode — the same create
        // pipeline that publishes the MeshChangeFeed event and warms the path resolver.
        var node = new MeshNode(id, ns) { Name = $"Test {id}", NodeType = "Markdown" };
        return SeedTopLevel(node);
    }

    private async Task DeleteTestNode(string path)
    {
        var response = await Mesh.Observe(new DeleteNodeRequest(path), o => o.WithTarget(Mesh.Address)).Should().Emit();
        response.Message.Error.Should().BeNullOrEmpty();
    }

    [Fact]
    public void CreateNode_PublishesCreatedEvent()
    {
        var events = new List<MeshChangeEvent>();
        using var sub = ChangeFeed.Subscribe(e => events.Add(e));

        CreateTestNode("feed-create-1");

        events.Should().Contain(e => e.Kind == MeshChangeKind.Created && e.Id == "feed-create-1");
    }

    [Fact]
    public void DeleteNode_PublishesDeletedEvent()
    {
        var created = CreateTestNode("feed-del-1");

        var events = new List<MeshChangeEvent>();
        using var sub = ChangeFeed.Subscribe(e => events.Add(e));

        DeleteTestNode(created.Path);

        events.Should().Contain(e => e.Kind == MeshChangeKind.Deleted && e.Path.Contains("feed-del-1"));
    }

    [Fact]
    public void FilteredSubscription_OnlyReceivesMatchingEvents()
    {
        var createEvents = new List<MeshChangeEvent>();
        var deleteEvents = new List<MeshChangeEvent>();
        using var createSub = ChangeFeed.Subscribe(e => createEvents.Add(e), MeshChangeKind.Created);
        using var deleteSub = ChangeFeed.Subscribe(e => deleteEvents.Add(e), MeshChangeKind.Deleted);

        var created = CreateTestNode("feed-filter-1");
        DeleteTestNode(created.Path);

        createEvents.Should().OnlyContain(e => e.Kind == MeshChangeKind.Created);
        deleteEvents.Should().OnlyContain(e => e.Kind == MeshChangeKind.Deleted);
    }

    [Fact]
    public async Task CreateNode_PathResolverFindsIt()
    {
        // Resolve before create Ã¢â‚¬â€ should not find it
        var before = await PathResolver.ResolvePath("feed-resolve-1").Should().Emit();

        CreateTestNode("feed-resolve-1");

        // After create Ã¢â‚¬â€ cache was invalidated/pre-warmed by change event
        var after = await PathResolver.ResolvePath("feed-resolve-1").Should().Emit();
        after.Should().NotBeNull();
        after!.Prefix.Should().Contain("feed-resolve-1");
        after.Remainder.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task DeleteNode_PathResolverNoLongerFindsIt()
    {
        var created = CreateTestNode("feed-gone-1");

        // Verify resolver finds it
        var exists = await PathResolver.ResolvePath(created.Path).Should().Emit();
        exists.Should().NotBeNull();

        DeleteTestNode(created.Path);

        // After delete Ã¢â‚¬â€ cache evicted, resolver should not find it at that exact path
        var gone = await PathResolver.ResolvePath(created.Path).Should().Emit();
        (gone == null || gone.Prefix != created.Path).Should().BeTrue(
            "deleted node should not resolve to its exact path");
    }

    [Fact]
    public async Task NestedCreate_EvictsParentPartialMatch()
    {
        // Create parent
        var parent = CreateTestNode("nest-parent-1");

        // Resolve nested path Ã¢â‚¬â€ caches partial match (parent with remainder)
        var partial = await PathResolver.ResolvePath($"{parent.Path}/nest-child-1").Should().Emit();

        // Create child
        CreateTestNode("nest-child-1", parent.Path);

        // Now nested path should resolve to child (stale cache evicted by Created event)
        var afterChild = await PathResolver.ResolvePath($"{parent.Path}/nest-child-1").Should().Emit();
        afterChild.Should().NotBeNull();
        afterChild!.Prefix.Should().Be($"{parent.Path}/nest-child-1");
        afterChild.Remainder.Should().BeNullOrEmpty();
    }
}

