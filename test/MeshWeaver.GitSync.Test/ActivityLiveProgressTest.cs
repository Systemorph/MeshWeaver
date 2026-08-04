using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins the LIVE-progress contract of <see cref="ActivityRunner.RunActivity"/>: a progress line a
/// command writes through <c>ctx.Log</c> must land on the activity node <b>while the command is
/// still running</b> — not only after it completes.
///
/// <para>This is the regression pin for the self-deadlock fixed in
/// <c>ActivityRunner.ScheduleOffHubTurn</c>. <c>RunActivity</c> is invoked ON a hub; under Orleans
/// that hub is a grain with a single-threaded activation scheduler. Subscribing the execution
/// inline ran <c>command(ctx)</c> on the hub's own turn, and every <c>ctx.Log</c> is a round trip
/// (<c>GetMeshNodeStream(activityPath).Update</c>) that needs THAT SAME turn to process its
/// response — so the write could never land while the command held the turn. The observable
/// symptom was an activity frozen at the single message baked into <c>CreateNode</c>, with no
/// terminal status and no error anywhere (memex, 2026-08-02: GitSync activities stuck at message 1
/// while the sync itself completed and advanced <c>_GitSync.lastSyncCommitSha</c>).</para>
///
/// <para>The test makes that dependency explicit and deterministic instead of relying on load: the
/// command does not complete until it observes its OWN progress line on the node. Under Orleans —
/// where a grain turn is single-threaded and the activity hub is grain-hosted — inline execution
/// cannot satisfy that: the turn is held by the command, so the write can never land, and the test
/// times out. (A non-Orleans harness with a free-threaded scheduler would not deadlock here; the
/// regression this pins is specifically the grain-turn one.) With the execution scheduled off the
/// hub turn the line lands while the command is still subscribed, and the activity finishes.</para>
///
/// <para>⚠️ Consequence of that parenthesis, measured rather than assumed: on THIS harness the two
/// live-progress tests below pass with <c>ScheduleOffHubTurn</c> deleted. They document the contract;
/// they do not enforce it. <see cref="ActivityExecutionGoesThroughThePooledSubscribeScheduler"/> is the
/// one that fails — it asserts the scheduler hop itself rather than an outcome the hop happens to
/// enable.</para>
/// </summary>
public class ActivityLiveProgressTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    /// <summary>
    /// Installs the recording scheduler as a DECORATOR over whatever the mesh already registered,
    /// so the activity path behaves exactly as in production and the test can still assert the hop
    /// happened. Scoped to this mesh — it dies with it.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                var existing = services.LastOrDefault(d => d.ServiceType == typeof(IPooledSubscribeScheduler));
                services.AddSingleton<IPooledSubscribeScheduler>(sp =>
                {
                    var inner = existing?.ImplementationFactory?.Invoke(sp) as IPooledSubscribeScheduler
                                ?? (existing?.ImplementationType is { } t
                                    ? ActivatorUtilities.CreateInstance(sp, t) as IPooledSubscribeScheduler
                                    : existing?.ImplementationInstance as IPooledSubscribeScheduler);
                    return new RecordingSubscribeScheduler(inner);
                });
                return services;
            });

    /// <summary>
    /// Counts how many times an observable was routed through the drainable pool. Wraps the real
    /// scheduler rather than replacing it, so the activity still executes exactly as it does in
    /// production — the recorder only observes.
    ///
    /// <para>An INSTANCE registered per mesh, never a static counter: static state would survive
    /// mesh disposal and bleed across tests (Doc/Architecture/NoStaticState).</para>
    /// </summary>
    private sealed class RecordingSubscribeScheduler(IPooledSubscribeScheduler? inner) : IPooledSubscribeScheduler
    {
        private int pooledSubscribes;

        /// <summary>How many observables have been routed through the pool on this mesh.</summary>
        public int PooledSubscribes => Volatile.Read(ref pooledSubscribes);

        /// <inheritdoc />
        public IObservable<T> SubscribeThroughPool<T>(IObservable<T> source)
        {
            Interlocked.Increment(ref pooledSubscribes);
            return inner is null ? source : inner.SubscribeThroughPool(source);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ProgressLine_LandsWhileTheCommandIsStillRunning()
    {
        var space = "GhLive" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Live Progress Space");

        const string progress = "first progress line";

        var activityPath = await Mesh.RunActivity(
                space, ActivityCategory.Import, "Live progress probe",
                ctx =>
                {
                    ctx.Log(progress);
                    // Complete only once this very line is visible on the activity node. If the
                    // execution ran on the calling hub's turn, the Append round trip could not be
                    // served while we sit here — the classic self-deadlock.
                    return ObserveMessage(ctx.ActivityPath, progress)
                        .Timeout(30.Seconds())
                        .Select(_ => Unit.Default);
                })
            .Timeout(60.Seconds()).ToTask();

        var log = await WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, log.Status);
        Assert.Contains(log.Messages, m => m.Message.Contains(progress));
        Assert.NotNull(log.End);
    }

    /// <summary>
    /// A second progress line written AFTER the first was observed — proves the channel stays open
    /// for the whole run rather than draining a single buffered write.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task EveryProgressLine_LandsWhileTheCommandIsStillRunning()
    {
        var space = "GhLive2" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Live Progress Space 2");

        var activityPath = await Mesh.RunActivity(
                space, ActivityCategory.Import, "Sequential progress probe",
                ctx =>
                {
                    ctx.Log("step one");
                    return ObserveMessage(ctx.ActivityPath, "step one")
                        .Timeout(30.Seconds())
                        .SelectMany(_ =>
                        {
                            ctx.Log("step two");
                            return ObserveMessage(ctx.ActivityPath, "step two").Timeout(30.Seconds());
                        })
                        .Select(_ => Unit.Default);
                })
            .Timeout(60.Seconds()).ToTask();

        var log = await WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, log.Status);
        Assert.Contains(log.Messages, m => m.Message.Contains("step one"));
        Assert.Contains(log.Messages, m => m.Message.Contains("step two"));
    }

    /// <summary>
    /// 🚨 The one test that actually FAILS when <c>ScheduleOffHubTurn</c> is removed.
    ///
    /// <para>The two tests above assert the OUTCOME (a progress line lands mid-run), and that
    /// outcome is reachable inline on a free-threaded harness — verified 2026-08-04 by deleting the
    /// scheduler hop: both still passed, in under a second. A regression pin that cannot fail is
    /// worse than no pin, because the next person to touch this reads green and ships the deadlock.</para>
    ///
    /// <para><b>Why this pins the SEAM and not a thread id.</b> Two earlier probes were tried and
    /// both are unsound on this harness:</para>
    /// <list type="number">
    ///   <item><description><c>createdThread != commandThread</c> — the runtime makes no such
    ///   promise. The hop ENQUEUES the work (<c>SubscribeThroughPool</c> /
    ///   <c>SubscribeOn(TaskPoolScheduler.Default)</c>) and the pool is free to run it on the thread
    ///   that just went idle — the very thread that delivered the create. The hop happens correctly
    ///   and the ids still match. Measured: 3 failures in 25 local runs (12%), and it reddened main
    ///   within 20 minutes of merging.</description></item>
    ///   <item><description>Blocking the delivering thread and waiting for the command to signal —
    ///   the command does not run CONCURRENTLY with the create callback at all. The callback
    ///   returns first, and only then is the execution scheduled. Measured: fails ~60% of runs even
    ///   though the command demonstrably ran on another thread.</description></item>
    /// </list>
    ///
    /// <para>On a free-threaded monolith harness there is no observable behavioural difference
    /// between hopped and inline — the deadlock this guards is an ORLEANS property (the grain's
    /// activation turn). So pin the mechanism where it is deterministic: the execution must go
    /// through the injected <see cref="IPooledSubscribeScheduler"/>. Delete
    /// <c>ScheduleOffHubTurn</c> and the recorder below is never called, so this fails immediately
    /// and for the right reason — with no timing dependence whatsoever.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ActivityExecutionGoesThroughThePooledSubscribeScheduler()
    {
        var recorder = Mesh.ServiceProvider.GetService<IPooledSubscribeScheduler>() as RecordingSubscribeScheduler;
        Assert.NotNull(recorder); // the harness must have installed the recorder — see ConfigureMesh
        var before = recorder!.PooledSubscribes;

        var space = "GhTurn" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Off-turn execution space");

        var activityPath = await Mesh.RunActivity(
                space, ActivityCategory.Import, "Off-turn probe",
                ctx => Observable.Return(Unit.Default))
            .Timeout(60.Seconds()).ToTask();

        Assert.True(recorder.PooledSubscribes > before,
            "the activity execution did not go through IPooledSubscribeScheduler — ScheduleOffHubTurn "
            + "was bypassed, so the command runs on the thread that delivered the create. Under "
            + "Orleans that thread is the grain's activation turn, and every ctx.Log write would "
            + "deadlock against the command holding it.");

        var log = await WaitForActivity(activityPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, log.Status);
    }

    private IObservable<ActivityLog> ObserveMessage(string activityPath, string contains) =>
        Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            // ContentAs, not a cast: on a cross-hub stream the content arrives as a degraded
            // JsonElement, and a plain `as` yields null forever — the wait would silently never
            // match and the test would time out with no clue why. Same reason ActivityRunner.Append
            // and every Blazor reader of this node use ContentAs.
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l is not null && l.Messages.Any(m => m.Message.Contains(contains)))
            .Select(l => l!)
            .Take(1);

    private async Task<ActivityLog> WaitForActivity(string activityPath, Func<ActivityLog, bool> predicate) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            // ContentAs, not a cast: on a cross-hub stream the content arrives as a degraded
            // JsonElement, and a plain `as` yields null forever — the wait would silently never
            // match and the test would time out with no clue why. Same reason ActivityRunner.Append
            // and every Blazor reader of this node use ContentAs.
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l is not null && predicate(l))
            .Select(l => l!)
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask();
}
