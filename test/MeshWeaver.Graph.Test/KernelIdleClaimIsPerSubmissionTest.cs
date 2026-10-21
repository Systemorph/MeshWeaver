using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The kernel host's idle-disconnect claim must last until EACH SUBMISSION ends — not until the
/// Activity reads terminal. One Activity carries many submissions (a markdown view's blocks, an
/// ExecutionManager's queue); once an earlier block settled it Succeeded, a later block whose bounded
/// reply wait timed out found that status and released its claim at once, so the next idle tick
/// disposed the host and its executor while that block was still running — and it never settled.
///
/// <para>Real mesh, the real Roslyn kernel, an injected idle clock (<see cref="KernelHubOptions.TimeProvider"/>)
/// stepped by the test, and a one-millisecond request budget on the Activity hubs so every reply wait
/// has timed out by the time a script is running. The long block parks on a gate the test releases
/// (a DI singleton the script resolves), so the idle window is crossed deterministically while it
/// runs. Both tests fail on the Activity-status settlement: the block never returns.</para>
/// </summary>
public class KernelIdleClaimIsPerSubmissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Bound => TestTimeouts.Convergence;
    private static readonly TimeSpan Window = new KernelHubOptions().IdleDisconnectTimeout;

    private readonly ManualClock clock = new();
    private readonly BehaviorSubject<string> gate = new("idle");

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton(new KernelHubOptions { TimeProvider = clock })
                .AddSingleton(gate))
            .ConfigureDefaultNodeHub(c => c.Address.ToString().Contains("/_Activity/", StringComparison.Ordinal)
                ? c.WithRequestTimeout(TimeSpan.FromMilliseconds(1))
                : c);

    private const string Gated = """
        using System.Reactive.Linq;
        using Microsoft.Extensions.DependencyInjection;
        var gate = Mesh.ServiceProvider.GetRequiredService<System.Reactive.Subjects.BehaviorSubject<string>>();
        gate.OnNext("started");
        await gate.Where(s => s == "released").Take(1).Await(Ct);
        2
        """;

    /// <summary>Block 1 settles the Activity Succeeded; block 2 then runs quietly past the idle window.</summary>
    [Fact(Timeout = 180_000)]
    public async Task ALaterBlockOnASettledActivity_KeepsItsHostUntilItEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = await CreateActivity(ct);

        Submit(activity, "1");
        await ReturnValue(activity, "1", "block 1 runs and settles the shared Activity", ct);

        Submit(activity, Gated);
        await CrossTheIdleWindowWhileGated(ct);

        var host = Mesh.GetHostedHub(new Address(activity), HostedHubCreation.Never);
        host.Should().NotBeNull("the Activity's kernel host is live while block 2 runs");
        await ReturnValue(activity, "2",
            "block 2 was still running when the idle window passed; its host must not have been reclaimed", ct);
        await Reclaimed(host!, "block 2 ended, so its claim is released and the idle host is reclaimed", ct);
    }

    /// <summary>Three blocks queued at once: the first settles the Activity while the second still runs.</summary>
    [Fact(Timeout = 180_000)]
    public async Task QueuedBlocksOnOneActivity_KeepTheirHostUntilTheLastEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = await CreateActivity(ct);

        Submit(activity, "1");
        Submit(activity, Gated);
        Submit(activity, "3");
        await CrossTheIdleWindowWhileGated(ct);

        var host = Mesh.GetHostedHub(new Address(activity), HostedHubCreation.Never);
        host.Should().NotBeNull("the Activity's kernel host is live while the blocks run");
        await ReturnValue(activity, "3",
            "every queued block must run to its end; none may be cut off by an earlier block's terminal status", ct);
        await Reclaimed(host!, "every block ended, so every claim is released and the idle host is reclaimed", ct);
    }

    /// <summary>
    /// The other half of the claim: once its submission has settled it must be RELEASED, or the host
    /// is never reclaimed. Steps the idle clock one window per tick until the host is disposing —
    /// a submission may settle a moment after its return value is visible, so one step could land
    /// before the release; a claim that is never released never gets there.
    /// </summary>
    private Task<bool> Reclaimed(IMessageHub host, string because, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(100))
            .Select(_ =>
            {
                clock.Advance(Window);
                return host.IsDisposing;
            })
            .Where(disposing => disposing)
            .FirstAsync()
            .Timeout(Bound)
            .Catch<bool, TimeoutException>(ex => Observable.Throw<bool>(
                new TimeoutException($"the kernel host was never reclaimed: {because}", ex)))
            .Await(ct);

    private async Task CrossTheIdleWindowWhileGated(CancellationToken ct)
    {
        await gate.Where(s => s == "started").FirstAsync().Timeout(Bound).Await(ct);
        // The gated block is executing and its reply wait (1 ms budget) has long timed out.
        clock.Advance(Window);
        clock.Advance(Window);
        gate.OnNext("released");
    }

    private void Submit(string activityPath, string code)
        => RequestHub.Post(
            new SubmitCodeRequest(code) { Id = Guid.NewGuid().ToString("N"), ActivityLogPath = activityPath },
            o => o.WithTarget(new Address(activityPath)));

    private async Task<string> CreateActivity(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var ns = $"{TestPartition}/_Activity";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => mesh.CreateNode(new MeshNode(id, ns)
            {
                Name = "shared kernel activity",
                NodeType = "Activity",
                MainNode = TestPartition,
                State = MeshNodeState.Active,
                Content = new ActivityLog("ScriptExecution") { Id = id, Status = ActivityStatus.Running },
            }))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        return $"{ns}/{id}";
    }

    private Task<ActivityLog?> ReturnValue(string activityPath, string expected, string because, CancellationToken ct)
        => Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(node => node?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(log => log?.ReturnValue?.ToString() == expected)
            .FirstAsync()
            .Timeout(Bound)
            .Catch<ActivityLog?, TimeoutException>(ex => Observable.Throw<ActivityLog?>(
                new TimeoutException($"{activityPath} never returned {expected}: {because}", ex)))
            .Await(ct);

    /// <summary>A clock that moves only when told to, firing each due one-shot timer at its due instant.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gateLock = new();
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private ImmutableList<ManualTimer> timers = ImmutableList<ManualTimer>.Empty;

        public override DateTimeOffset GetUtcNow() { lock (gateLock) return now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (gateLock) timers = timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target;
            lock (gateLock) target = now + by;
            while (true)
            {
                ManualTimer? next;
                lock (gateLock)
                {
                    next = timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                    if (next is null) { now = target; return; }
                    now = next.Due!.Value;
                }
                next.Fire();
            }
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        // Re-armed from hub threads (every delivered message) while the test advances the clock.
        private readonly object dueLock = new();
        private DateTimeOffset? due;

        public DateTimeOffset? Due { get { lock (dueLock) return due; } }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var at = dueTime == Timeout.InfiniteTimeSpan ? (DateTimeOffset?)null : clock.GetUtcNow() + dueTime;
            lock (dueLock) due = at;
            return true;
        }

        public void Fire()
        {
            lock (dueLock) due = null;
            callback(state);
        }

        public void Dispose() { lock (dueLock) due = null; }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
