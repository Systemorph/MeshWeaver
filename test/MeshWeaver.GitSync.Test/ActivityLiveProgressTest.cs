using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
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
/// they do not enforce it. <see cref="TheCommandDoesNotRunOnTheThreadThatDeliveredTheCreate"/> is the
/// one that fails — it asserts the scheduler hop itself rather than an outcome the hop happens to
/// enable.</para>
/// </summary>
public class ActivityLiveProgressTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
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
    /// <para>So pin the MECHANISM instead of the symptom, which needs no Orleans silo: the command
    /// body must not run on the thread that delivered the create — under Orleans that thread IS the
    /// grain's activation turn, and holding it is what makes every <c>ctx.Log</c> round trip
    /// unserviceable. <c>onActivityCreated</c> fires on exactly that continuation, immediately
    /// before the execution is scheduled, so the two thread ids are the before / after of the hop.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheCommandDoesNotRunOnTheThreadThatDeliveredTheCreate()
    {
        var space = "GhTurn" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Off-turn execution space");

        int createdThread = -1;
        int commandThread = -2;

        var activityPath = await Mesh.RunActivity(
                space, ActivityCategory.Import, "Off-turn probe",
                ctx =>
                {
                    commandThread = Environment.CurrentManagedThreadId;
                    return Observable.Return(Unit.Default);
                },
                onActivityCreated: _ => createdThread = Environment.CurrentManagedThreadId)
            .Timeout(60.Seconds()).ToTask();

        Assert.NotEqual(-1, createdThread);
        Assert.NotEqual(-2, commandThread);
        Assert.True(createdThread != commandThread,
            $"the command ran INLINE on the create's thread ({createdThread}) — under Orleans that is "
            + "the grain turn, so every ctx.Log write would deadlock against the command holding it");

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
