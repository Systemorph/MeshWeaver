using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>THE #3954 RACE, END TO END ON A REAL MESH.</b> The negative-probe claim protocol is pinned
/// close-up by <c>NegativeCacheInvalidationRaceTest</c> (MeshWeaver.Hosting.Test), which substitutes
/// a gated owner so one probe can be held open across the invalidation. These arms are the other
/// half of the evidence: the same race driven through the <b>ordinary</b> pipeline — a real
/// <c>CreateNode</c>, the real <see cref="IMeshChangeFeed"/> broadcast, several probes in flight at
/// once — asserting the thing a user actually observes.
///
/// <para><b>The incident.</b> At 05:40Z on memex (the third symptom of #3894) a <c>create</c>
/// succeeded and <c>search</c> returned the new row, while <c>get</c> kept answering
/// <c>Not found</c> until somebody issued a <c>recycle</c>. The cached miss lives in
/// <c>MeshNodeStreamCache._negative</c>; the create's <c>Created</c> broadcast already reset it, but
/// a <c>NotFound</c> from a probe that had subscribed <em>earlier</em> landed <em>afterwards</em> and
/// re-armed a window of up to five minutes. The breaker fast-fails <b>reads and writes alike</b>, so
/// the stale negative also suppressed the writes that would have cleared it, and the only thing that
/// closed it again was a further change event for the path — which is exactly what <c>recycle</c>
/// publishes.</para>
///
/// <para><b>Why this is deterministic.</b> The race is an ORDERING, so the test imposes one. A probe
/// stakes its claim through the production seam (<c>BeginNegativeProbe</c>) before the create, and
/// delivers its verdict through the production admission path afterwards — the two instants the live
/// race orders by accident. Everything between them is real: a real <c>CreateNode</c> publishing the
/// real post-commit <c>Created</c> event into the real change feed.</para>
///
/// <para>🚨 <b>Controls run in BOTH directions</b>, because a fence that simply stopped recording
/// negatives would delete the storm protection this cache exists to provide.
/// <see cref="AGenuineMiss_OnAPathThatNeverExisted_OpensTheWindow"/> is the organic positive control
/// on the live read path, and
/// <see cref="ALateMiss_WithNoInvalidationInBetween_IsStillAdmitted"/> is the positive control on the
/// seam itself — without it the negative arm would pass just as happily against an admission path
/// that refused everything, i.e. it would assert an absence nothing could falsify.</para>
/// </summary>
public class StaleNegativeAfterARealCreateTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private static string NewPath(string prefix) => $"{TestPartition}/{prefix}-{Guid.NewGuid():N}";

    private static MeshNode Node(string path, string name) => MeshNode.FromPath(path) with
    {
        Name = name,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    /// <summary>
    /// Reads <paramref name="path"/> through the cache and returns the owner's REAL missing-node
    /// failure, so the arms below carry the exception class production mints rather than a
    /// hand-written stand-in. The classifier agreement is asserted, so a classifier change cannot
    /// silently hollow these tests out.
    /// </summary>
    private async Task<Exception> RealMissingNodeFailure(string path)
    {
        var failure = await Cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(TestTimeouts.Convergence).Match(
                n => n.Kind == NotificationKind.OnError,
                "reading a path that does not exist must surface the owner's NotFound as OnError");
        MeshNodeStreamCache.IsMissingNodeFailure(failure.Exception!).Should().BeTrue(
            "precondition: the organic failure must be the class the storm breaker records");
        return failure.Exception!;
    }

    /// <summary>
    /// 🚨 POSITIVE CONTROL, organic and first: the live read path must still arm the breaker on a
    /// path that genuinely does not exist. This is the protection the class exists for, and the
    /// thing an over-eager fence would silently remove.
    /// </summary>
    [Fact]
    public async Task AGenuineMiss_OnAPathThatNeverExisted_OpensTheWindow()
    {
        var path = NewPath("really-absent");

        await RealMissingNodeFailure(path);

        Cache.IsStormWindowOpen(path).Should().BeTrue(
            "a point read of a path that really does not exist must open the breaker window — the "
            + "#3954 claim protocol refuses only a verdict an invalidation has already staled, and "
            + "nothing invalidated this path");

        await Cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Materialize()
            .Should().Within(TestTimeouts.Quick).Match(
                n => n.Kind == NotificationKind.OnError,
                "the open window must fast-fail the re-read from cache instead of re-probing the owner");
    }

    /// <summary>
    /// 🚨 THE REPRO. Several probes are in flight at <c>P</c> when a REAL <c>CreateNode(P)</c> lands,
    /// and their misses arrive afterwards. Every one must be refused, the window must stay shut, and
    /// — the user-visible half — <c>get</c> must answer the node and a follow-up write must land,
    /// with <b>no recycle published anywhere in this test</b>.
    /// </summary>
    [Fact]
    public async Task MissesThatLandAfterARealCreate_DoNotReArmTheWindow_AndNoRecycleIsNeeded()
    {
        var cache = Cache;
        var path = NewPath("late-miss");

        // The real owner error those probes are carrying, and the control that the window DOES open
        // when nothing has invalidated the path yet.
        var missing = await RealMissingNodeFailure(path);
        cache.IsStormWindowOpen(path).Should().BeTrue(
            "control: before the create, a miss on this path is genuine and arms the breaker — so a "
            + "closed window at the end can only be the claim protocol's doing");

        // The readers that went out BEFORE the create — the shape of a page whose bindings all read
        // the same path. Each stakes its claim through the production seam; a later probe replaces
        // an earlier one, exactly as concurrent reads do.
        var probes = Enumerable.Range(0, 5).Select(_ => cache.BeginNegativeProbe(path)).ToArray();

        // The create: the ordinary pipeline, whose post-commit Created publish IS the invalidation.
        await NodeFactory.CreateNode(Node(path, "NowExists")).Should().Within(TestTimeouts.Convergence).Emit();

        // …and now every one of those misses lands, after the invalidation has already run.
        foreach (var probe in probes)
            cache.TryRecordNegativeForTest(path, missing, probe, static () => { }).Should().BeFalse(
                "a miss minted by a probe that was already in flight when the create invalidated the "
                + "path is stale on arrival — admitting it re-arms a window that only another change "
                + "event, i.e. a recycle, would close again (#3954)");

        cache.IsStormWindowOpen(path).Should().BeFalse(
            "no stale miss may leave a fast-fail window on a path that now exists");

        // The user-visible consequence: `get` answers the node, WITHOUT a recycle.
        var node = await cache.GetStream(path, Mesh.JsonSerializerOptions)
            .Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the node exists, so `get` must answer it — this is the read that kept saying "
                + "'Not found' on memex at 05:40Z until somebody recycled");
        node!.Path.Should().Be(path);

        // …and the other half the breaker gates. A stale negative fast-fails WRITES too
        // (MeshNodeStreamCache.UpdateRaw), so it suppressed the write that would have cleared it.
        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Name = "WrittenWithoutARecycle" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the breaker fast-fails writes on a windowed path as well, so the same stale verdict "
                + "suppressed the write that would have cleared it");
    }

    /// <summary>
    /// 🚨 POSITIVE CONTROL on the SEAM. Identical to the repro except that nothing invalidates the
    /// path in between — so the miss IS admitted. Without this arm the repro would pass against an
    /// admission path that refused every verdict, and would be asserting nothing.
    /// </summary>
    [Fact]
    public async Task ALateMiss_WithNoInvalidationInBetween_IsStillAdmitted()
    {
        var cache = Cache;
        var path = NewPath("no-invalidation");

        var missing = await RealMissingNodeFailure(path);

        // A fresh probe supersedes the organic read's claim, as a natural re-probe does…
        var probe = cache.BeginNegativeProbe(path);
        // …and NOTHING happens here. No create, no write, no change event of any kind.
        cache.TryRecordNegativeForTest(path, missing, probe, static () => { }).Should().BeTrue(
            "a verdict from a probe whose claim is still current must be admitted exactly as before "
            + "#3954 — the refusal is conditional on an invalidation having run, and this is the "
            + "control that proves the negative arm could have failed");

        cache.IsStormWindowOpen(path).Should().BeTrue(
            "an admitted miss opens the fast-fail window, which is the storm protection this cache "
            + "exists to provide");
    }
}
