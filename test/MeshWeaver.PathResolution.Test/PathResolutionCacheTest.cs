using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

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
            .Await();

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

    /// <summary>
    /// 🚨 <b>The announcement KIND decides whether a cached MISS is ever evicted</b> — the mechanism
    /// behind the #817/#824/#2087 announce-loss class, made executable.
    ///
    /// <para>A path probed while its node is absent resolves to <c>prefix = ancestor</c> with a
    /// non-empty <c>Remainder</c>. That is a perfectly cacheable POSITIVE value, so it is cached —
    /// and from then on only the change feed can dislodge it. <c>Created</c>/<c>Deleted</c> REMOVE
    /// matching entries; <c>Updated</c> deliberately only STALE-MARKS them and keeps serving the
    /// route shape, because removing on Updated was the #1172 routing/compile feedback loop (every
    /// activity-log write invalidated the entry the next routed message needed).</para>
    ///
    /// <para>So a genuine CREATE announced as an UPDATE leaves the miss in place for the life of the
    /// process: the row is in storage, routing keeps answering <i>"No node found at '…'"</i>, no hub
    /// is ever woken. This test states both halves as facts so no future change can invert them
    /// silently — and so the reason <c>NodeTypeBatchBake</c>'s insert must publish <c>Created</c>
    /// (its <c>WriteIfVersion(node, 0)</c> is "write only if absent") is not just prose.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ACachedMiss_IsEvictedByCreated_ButNotByUpdated()
    {
        var space = await SeedSpace();
        var absent = $"{space}/never-created";
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();

        // Cache the MISS: the ancestor resolves, the last segment does not.
        var miss = await PollResolution(absent,
            r => r is not null && string.Equals(r.Prefix, space, StringComparison.Ordinal));
        miss!.Remainder.Should().Be("never-created", "this is the cached-miss shape the class doc names");

        // An UPDATED event for that path must NOT change the route shape — it stale-marks only.
        // Asserted as the STABLE state after the event has been given every chance to land: an
        // Updated that arrived and did the right thing is indistinguishable from one still in
        // flight, so the point is that the shape never changes, not that it changes slowly.
        var placeholder = MeshNode.FromPath(absent) with { Name = "Phantom", NodeType = "Markdown" };
        feed.Publish(MeshChangeEvent.Updated(placeholder));
        var afterUpdated = await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L).Take(5)
            .SelectMany(_ => PathResolver.ResolvePath(absent))
            .ToArray().Await();
        afterUpdated.Should().OnlyContain(
            r => r != null && r.Prefix == space && r.Remainder == "never-created",
            "Updated only STALE-MARKS the entry and keeps serving the route shape (#1172) — which "
            + "is exactly why announcing a CREATE as an Updated strands the path forever");

        // A CREATED event for the same path REMOVES the entry, so the next resolution re-queries.
        // The node genuinely exists by then, so the re-query resolves it fully.
        await SeedChild(absent);
        feed.Publish(MeshChangeEvent.Created(placeholder));
        var afterCreated = await PollResolution(absent,
            r => r is not null && string.Equals(r.Prefix, absent, StringComparison.Ordinal));
        afterCreated!.Remainder.Should().BeNull(
            "Created REMOVES the cached miss, so the path resolves in full without a restart — "
            + "this is the half the announce-loss class keeps losing");
    }
}
