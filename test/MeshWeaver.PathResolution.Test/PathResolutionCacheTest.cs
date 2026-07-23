using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.PathResolution.Test;

/// <summary>
/// Tests for the positive-only resolution cache in <c>PathResolutionService</c>.
/// <list type="bullet">
///   <item>A resolved (non-null) path is memoized: the SECOND subscription emits
///     synchronously — this is the contract the Blazor navigation layer relies on
///     to skip progress UI on slide switches.</item>
///   <item>A NULL resolution is NEVER cached — the historic stale-NULL race
///     (query snapshot racing change-feed propagation right after CreateNode)
///     must not pin a permanent 404.</item>
///   <item>Delete/Update events on the <see cref="IMeshChangeFeed"/> invalidate
///     affected entries so resolutions never serve stale routing.</item>
/// </list>
/// </summary>
public class PathResolutionCacheTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Seeds a unique top-level Space (Space owns partitions, so the test Admin
    /// identity can create it directly — same pattern as SlideLayoutAreaTest).
    /// </summary>
    private async Task<string> SeedSpace()
    {
        var space = $"Cache{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Cache Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();
        return space;
    }

    private Task<MeshNode> SeedChild(string path, string name = "Child") =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = "Markdown",
        }).Should().Emit();

    /// <summary>
    /// Bounded poll for a resolution state. Change-feed propagation and re-query are
    /// asynchronous, so tests wait on the actual condition (never a fixed sleep) —
    /// each tick runs a full resolution and the first matching one wins.
    /// </summary>
    private Task<AddressResolution?> PollResolution(
        string path, Func<AddressResolution?, bool> predicate) =>
        Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .SelectMany(_ => PathResolver.ResolvePath(path))
            .Where(predicate)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask();

    /// <summary>
    /// The cache contract the Blazor layer builds on: once a path has resolved,
    /// a second subscription must emit SYNCHRONOUSLY on Subscribe (Replay(1)
    /// promise cache) — no second Postgres/query round-trip per routed message
    /// or navigation.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SecondResolution_EmitsSynchronously()
    {
        var space = await SeedSpace();

        var first = await PathResolver.ResolvePath(space).Should().Emit();
        first.Should().NotBeNull();
        first!.Prefix.Should().Be(space);

        AddressResolution? captured = null;
        var emitted = false;
        using var subscription = PathResolver.ResolvePath(space)
            .Subscribe(r =>
            {
                captured = r;
                emitted = true;
            });

        // Assert BEFORE any await: the value must have arrived inside Subscribe.
        emitted.Should().BeTrue(
            "the second resolution of an already-resolved path must emit synchronously on Subscribe (warm positive cache)");
        captured.Should().NotBeNull();
        captured!.Prefix.Should().Be(space);
        captured.Remainder.Should().BeNull();
    }

    /// <summary>
    /// The historic-race guard: a null (not-found) resolution must NEVER be served
    /// from cache once the node exists. This is exactly the stale-NULL-after-
    /// CreateNode race that got the previous PathResolution cache removed — the
    /// positive-only design must keep this green both before and after caching.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task NullResolution_IsNotCached()
    {
        var space = $"Cache{Guid.NewGuid():N}"[..16];
        var path = $"{space}/child";

        // Resolve BEFORE anything exists → null (2 segments, so no partition-root synthesis).
        var missing = await PathResolver.ResolvePath(path).Should().Emit();
        missing.Should().BeNull("nothing exists at {0} yet", path);

        // Create the nodes and resolve again — the earlier null must not stick.
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Cache Test Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        }).Should().Emit();
        await SeedChild(path);

        var resolved = await PollResolution(path,
            r => r is not null && string.Equals(r.Prefix, path, StringComparison.Ordinal));
        resolved.Should().NotBeNull(
            "a null resolution must never be cached — after CreateNode the path must resolve");
        resolved!.Remainder.Should().BeNull();
    }

    /// <summary>
    /// Deleting a node publishes a change-feed event that must evict the cached
    /// resolution: subsequent resolutions of the deleted path fall back to the
    /// deepest surviving ancestor.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task DeletedNode_InvalidatesCache()
    {
        var space = await SeedSpace();
        var child = $"{space}/child";
        await SeedChild(child);

        // Resolve (and thereby cache) the full child path.
        var resolved = await PollResolution(child,
            r => r is not null && string.Equals(r.Prefix, child, StringComparison.Ordinal));
        resolved.Should().NotBeNull();

        await NodeFactory.DeleteNode(child).Should().Emit();

        // The delete's change-feed event must invalidate the entry: the next
        // resolutions re-query and fall back to the parent Space.
        var after = await PollResolution(child,
            r => r is null || !string.Equals(r.Prefix, child, StringComparison.Ordinal));
        after.Should().NotBeNull("the parent Space still exists and is the deepest prefix");
        after!.Prefix.Should().Be(space);
        after.Remainder.Should().Be("child");
    }

    /// <summary>
    /// Updating a node's payload must refresh the cached resolution's
    /// <see cref="AddressResolution.Node"/> — routing carries the matched node, so
    /// a stale cached node would serve outdated metadata forever.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UpdatedNode_RefreshesNodePayload()
    {
        var space = await SeedSpace();
        var child = $"{space}/child";
        await SeedChild(child, name: "Before");

        var resolved = await PollResolution(child,
            r => r is not null && string.Equals(r.Prefix, child, StringComparison.Ordinal));
        resolved!.Node.Should().NotBeNull();
        resolved.Node!.Name.Should().Be("Before");

        await NodeFactory.UpdateNode(resolved.Node with { Name = "After" }).Should().Emit();

        var fresh = await PollResolution(child,
            r => string.Equals(r?.Node?.Name, "After", StringComparison.Ordinal));
        fresh.Should().NotBeNull(
            "the update's change-feed event must evict the cached resolution so a fresh query sees the new Name");
    }
}
