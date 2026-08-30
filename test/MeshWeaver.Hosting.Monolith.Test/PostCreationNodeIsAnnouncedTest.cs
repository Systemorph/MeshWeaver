using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>#2087 / the #817–#824 announce-loss class — a node a post-creation handler writes must be
/// ANNOUNCED, or it is invisible to the mesh that wrote it.</b>
///
/// <para><b>The invariant</b> (#824): the <see cref="IMeshChangeFeed"/> is what invalidates the
/// caches that decide whether a node is REACHABLE — <c>PathResolutionService</c>'s resolution cache,
/// <c>MeshNodeStreamCache</c>, the Orleans path-cache invalidator. A write that skips it leaves a
/// node that EXISTS in storage and does not exist to the running mesh: a path probed while it was
/// still absent resolves to its ancestor with a remainder — a perfectly cacheable value — and
/// nothing ever evicts it, so routing answers <c>No node found at '…'</c> for the life of the
/// process. #824 closed that for the installer's bulk path and warned: <i>"if this shape recurs,
/// look for a NEW mutation path that writes storage without going through those helpers."</i></para>
///
/// <para><b>The recurrence.</b> <c>MeshExtensions.RunPostCreationHandlersObs</c> persists the
/// additional nodes an <see cref="INodePostCreationHandler"/> returns with a bare
/// <c>IStorageAdapter.Write</c> — no announcement of any kind. Those are brand-new nodes (a Space's
/// creator-Admin grant, an <c>Admin/Partition</c> definition, onboarding seeds) written on the
/// create path of every node whose type has a handler, i.e. on essentially every imported root. The
/// <c>DataChangeRequest.Update</c> it posts instead is not a substitute: it targets the new node's
/// OWN address — the thing that has to become reachable in the first place — and feeds synced
/// queries, not the resolution caches.</para>
/// </summary>
public class PostCreationNodeIsAnnouncedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ProbeType = "AnnounceProbe";
    private const string ExtraPath = $"{TestPartition}/announce-probe-extra";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(ProbeType) { Name = "Announce Probe" })
            .ConfigureServices(services => services
                .AddSingleton<INodePostCreationHandler, ExtraNodeHandler>());

    /// <summary>
    /// The invariant, asserted directly on the feed: the additional node the handler returns carries
    /// a <see cref="MeshChangeKind.Created"/> event, exactly as any other create does.
    ///
    /// <para><b>Non-vacuity.</b> The parent's own <c>Created</c> is required first, so a feed that
    /// published nothing at all (a mis-wired subscription) cannot pass this by being empty.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ANodeWrittenByAPostCreationHandler_IsAnnouncedOnTheMeshChangeFeed()
    {
        var seen = new ConcurrentQueue<MeshChangeEvent>();
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        using var _ = feed.Subscribe(seen.Enqueue);

        var parentPath = $"{TestPartition}/announce-probe-parent";
        await NodeFactory.CreateNode(MeshNode.FromPath(parentPath) with
        {
            Name = "Announce probe parent",
            NodeType = ProbeType,
        }).Should().Emit();

        // The handler's write is chained off the create, so the event has landed by the time the
        // additional node is readable. Wait on the CONDITION, never a delay.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => seen.ToArray())
            .Where(events => events.Any(e => e.Path == ExtraPath))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await();

        var events = seen.ToArray();
        foreach (var e in events)
            Output.WriteLine($"{e.Kind} {e.Path}");

        events.Should().Contain(e => e.Path == parentPath && e.Kind == MeshChangeKind.Created,
            "the parent's own create announces normally — if this is missing the feed is mis-wired "
            + "and the assertion below would prove nothing");

        events.Should().Contain(e => e.Path == ExtraPath && e.Kind == MeshChangeKind.Created,
            "a node written by a post-creation handler is a CREATE like any other, and the "
            + "mesh-change feed is what makes it reachable — without it the row is in storage and "
            + "does not exist to the running mesh (#2087, the #817/#824 class)");
    }

    /// <summary>
    /// The outage in miniature, deterministic and in-process: probe the path while it is genuinely
    /// absent (which caches the miss), then create the parent whose handler writes it, then require
    /// the node reachable with nothing restarted. This is the same shape #824 pinned for the bulk
    /// installer path, on the mutation path that had not adopted the helpers.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task APathProbedBeforeItExisted_IsReachableRightAfterTheHandlerWritesIt()
    {
        // PROBE the absent node — the poisoning step. It must not find anything (that is the
        // point), and whatever the resolver caches here is what the create has to invalidate.
        var beforeCreate = await Mesh.GetWorkspace().GetMeshNodeStream(ExtraPath)
            .Take(1).Timeout(TimeSpan.FromSeconds(5))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .FirstAsync().Await();
        beforeCreate.Should().BeNull("the additional node is genuinely absent at this point");

        var parentPath = $"{TestPartition}/announce-probe-reach";
        await NodeFactory.CreateNode(MeshNode.FromPath(parentPath) with
        {
            Name = "Announce probe reach",
            NodeType = ProbeType,
        }).Should().Emit();

        var afterCreate = await Mesh.GetWorkspace().GetMeshNodeStream(ExtraPath)
            .Where(n => n is not null).Select(n => n!)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await();

        afterCreate.Path.Should().Be(ExtraPath,
            "a node written by a post-creation handler that was probed while absent must not stay "
            + "unreachable — that is the fresh-import outage this pins (#2087)");
    }

    /// <summary>
    /// Writes exactly one additional node at a fixed path, so the test can probe that path before
    /// the create. <c>Handle</c> itself does nothing — the defect is entirely in how
    /// <c>GetAdditionalNodes</c>' result is persisted.
    /// </summary>
    private sealed class ExtraNodeHandler : INodePostCreationHandler
    {
        public string NodeType => ProbeType;

        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
            => Observable.Return(Unit.Default);

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
            => [MeshNode.FromPath(ExtraPath) with { Name = "Announce probe extra", NodeType = ProbeType }];
    }
}
