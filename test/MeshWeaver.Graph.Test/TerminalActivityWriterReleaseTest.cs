using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Issue #3117 — three terminal <c>_Activity</c> writers bypassed
/// <c>ActivityLogAppender.Append</c>, so none of them retired the activity's per-node hub.</b>
///
/// <para><b>The invariant.</b> <c>Append</c> fires <c>IMeshNodeStreamCache.ReleaseIfUnwatched</c> on
/// the terminal write (#1435/#1324). That is what retires an activity's mirror when the activity
/// finishes, instead of leaving it to the ten-minute idle sweep — and the mirror is not a passive
/// cache entry, it posts a <c>HeartBeatEvent</c> every 45 s for the express purpose of keeping its
/// grain alive.</para>
///
/// <para><b>The gap.</b> Three writers reach a terminal status without going through
/// <c>Append</c> at all: <c>ActivityLogLogger.PublishSnapshotLocked</c> (by far the highest volume —
/// every kernel run, script, markdown execution and test run), <c>CodeNodeType.FailActivity</c>, and
/// <c>BuildProtocolDriver.FinishActivity</c>. Bounded rather than a leak, because the idle sweep
/// still reaches them — but a ten-minute retention multiplied by every kernel and test run is a real
/// steady-state footprint.</para>
///
/// <para>🚨 <b>Why the fix is a SEAM and not three copies of one line.</b> #3110's root cause was
/// exactly that: <c>EvictFaultedEntry</c> documents itself as "the single teardown … so they cannot
/// drift", and a fourth site had a hand-rolled copy that disposed only half the state. So the
/// release became <c>ActivityLogAppender.ReleaseMirrorWhenFinal</c> — one implementation, four
/// callers.</para>
///
/// <para><b>What this observes, and why it is not a watcher.</b> The subject is the real
/// <c>MeshNodeStreamCache</c> on a real monolith mesh; the signal is its own
/// <c>ReadStreamEvictions</c> feed, which the cache documents as being there for "diagnostics and
/// deterministic tests", filtered to the <c>"final"</c> reason that ONLY
/// <c>ReleaseIfUnwatched</c> emits. That matters: <c>ReleaseIfUnwatched</c> declines while anyone is
/// subscribed to the path, so a test that waited for the release by watching the activity node would
/// suppress the very thing it is asserting. This subscribes to the eviction feed instead, which is a
/// different stream, and never to the path.</para>
///
/// <para><b>Fails on unfixed code:</b> both writer facts time out with no <c>"final"</c> eviction,
/// because nothing released the mirror.</para>
/// </summary>
public class TerminalActivityWriterReleaseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "TestData";

    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    /// <summary>
    /// A real, Running activity node — the state every one of these writers finds when it lands.
    /// </summary>
    private async Task<string> ARunningActivity(string prefix)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = prefix + Guid.NewGuid().ToString("N")[..8];
        await meshService.CreateNode(new MeshNode(id, $"{Partition}/_Activity")
        {
            Name = "terminal-writer release probe",
            NodeType = ActivityNodeType.NodeType,
            MainNode = Partition,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.DataUpdate)
            {
                Id = id,
                HubPath = Partition,
                Status = ActivityStatus.Running,
            },
        }).FirstAsync().Timeout(60.Seconds());
        return $"{Partition}/_Activity/{id}";
    }

    /// <summary>
    /// The release, armed BEFORE the write that should cause it. <c>Replay</c> + <c>Connect</c> is
    /// load-bearing: the eviction can land while the write's own observable is still completing, and
    /// a subscription taken afterwards would miss it — a race the test would usually win, which is
    /// the worst kind of green.
    /// </summary>
    private (IConnectableObservable<ReadStreamEviction> Released, IDisposable Connection) ArmRelease(string path)
    {
        var released = Cache.ReadStreamEvictions
            .Where(e => e.Path == path && e.Reason == "final")
            .Take(1)
            .Replay();
        return (released, released.Connect());
    }

    /// <summary>
    /// 🚨 THE PIN for <c>BuildProtocolDriver.FinishActivity</c> — every build-protocol completion.
    /// </summary>
    [Fact]
    public async Task ABuildProtocolCompletion_RetiresTheActivitysMirror()
    {
        var path = await ARunningActivity("build");
        var (released, connection) = ArmRelease(path);
        using var _ = connection;

        // 🚨 LastAsync, never FirstAsync. The release rides the write's COMPLETION (see the seam's
        // remarks — releasing on the emission strands the still-in-flight write), and FirstAsync
        // UNSUBSCRIBES as soon as the node is emitted, so the completion arm would never run and
        // this test would report the regression it exists to catch. Production reaches completion:
        // CloseChunk's result is consumed through `.Concat().ToList()`, which waits for it.
        await BuildProtocolDriver.FinishActivity(Mesh, path, ActivityStatus.Succeeded)
            .LastAsync().Timeout(60.Seconds());

        var eviction = await released.FirstAsync().Timeout(60.Seconds());

        eviction.Reason.Should().Be("final",
            "the terminal status is the EVENT that proves the mirror is dead, so the release is "
            + "event-driven rather than a shorter timer — pre-fix this writer bypassed "
            + "ActivityLogAppender.Append entirely and its hub waited out the 10-minute idle sweep");
    }

    /// <summary>
    /// 🚨 THE PIN for <c>CodeNodeType.FailActivity</c> — every failed code-node activity.
    ///
    /// <para>This also covers the second defect found at that call site: the lambda tested
    /// <c>curr.Content is ActivityLog</c>, and a local mirror's Content can be a degraded
    /// <c>JsonElement</c>. The type test is then null, the lambda no-ops, no patch is sent, and the
    /// run this method exists to FAIL stays Running for ever — so there would be no terminal write
    /// to release on either.</para>
    /// </summary>
    [Fact]
    public async Task AFailedCodeRun_RetiresTheActivitysMirror()
    {
        var path = await ARunningActivity("code");
        var (released, connection) = ArmRelease(path);
        using var _ = connection;

        CodeNodeType.FailActivity(Mesh, path, "no worker connected — release probe");

        var eviction = await released.FirstAsync().Timeout(60.Seconds());

        eviction.Reason.Should().Be("final",
            "a failed code run is a terminal activity write like any other, and it bypassed the "
            + "Append seam that fires the release");
    }

    /// <summary>
    /// 🚨 The rule must stay NARROW: the seam releases on a TERMINAL status and on nothing else. A
    /// release fired for a Running activity would tear the mirror out from under a run that is still
    /// writing to it — the ten-minute sweep exists precisely because "is it done?" is otherwise a
    /// heuristic, and the terminal status is what replaces the heuristic with proof.
    /// </summary>
    [Fact]
    public async Task ARunningStatus_ReleasesNothing()
    {
        var path = await ARunningActivity("running");
        var (released, connection) = ArmRelease(path);
        using var _ = connection;

        ActivityLogAppender.ReleaseMirrorWhenFinal(Mesh, path, ActivityStatus.Running);

        var sawRelease = await released
            .Select(_ => true)
            .Timeout(2.Seconds(), Observable.Return(false))
            .FirstAsync();

        sawRelease.Should().BeFalse(
            "a Running activity is still writing to its mirror — releasing it would be the very "
            + "race the terminal-status proof exists to avoid");
    }
}
