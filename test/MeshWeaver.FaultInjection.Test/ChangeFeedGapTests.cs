using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>Plugins#3000 — a lost LISTEN window must not leave a replica silently stale.</b> PostgreSQL's
/// <c>LISTEN/NOTIFY</c> never replays: when the listener's connection drops and is re-opened, every
/// commit another replica made in between is announced to nobody here. Every cache retracted ONLY
/// by the change feed then serves its pre-gap answer for the life of the process — no error, no
/// log line, nothing to grep (core #6045/#6046/#6047 were symptoms of exactly this).
///
/// <para><b>The fault, deterministically.</b> Two silos over one store of record, joined by the
/// <see cref="Testing.FaultInjection.CrossProcessChangeRelay"/> (the LISTEN/NOTIFY model). Silo 1
/// holds a LIVE children query — the dependent cache. The relay is switched to <c>Drop</c> (the
/// connection is down), silo 0 commits a new child straight to the store (another replica's write
/// landing in PostgreSQL), and the drop's arrival proves that commit's notification was LOST. Then
/// the relay comes back.</para>
///
/// <list type="bullet">
///   <item><b>Fixed:</b> the reconnect DECLARES the gap (<see cref="ChangeFeedGap"/>), and silo 1's
///     live query re-reads the store and converges on the new child.</item>
///   <item><b>Negative control:</b> the identical sequence with the pre-fix listener — the
///     notification is lost and nobody is told — and the live query never shows the child. This is
///     what proves the convergence above comes from the gap signal and from nothing else (no
///     heartbeat, no unrelated notification, no timer).</item>
/// </list>
/// </summary>
public abstract class ALiveQueryAcrossALostChangeFeedWindow(FaultInjectionCluster mesh)
{
    /// <summary>Whether the relay declares the gap when it comes back (the fix) or stays silent (pre-fix).</summary>
    protected abstract bool AnnounceGap { get; }

    [Fact(Timeout = 120_000)]
    public async Task ACommitDuringTheGap_ReachesTheLiveQueryOnlyThroughTheGapSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = $"gap-{Guid.NewGuid():N}";
        var writerHub = mesh.Hub(0);
        var writerStore = mesh.Storage(0);
        var readerServices = mesh.Silo(1);
        var readerAccess = readerServices.GetRequiredService<AccessService>();

        // A first child, committed while the relay is healthy, so the live query's Initial is
        // provably non-empty and its pipeline provably live before the fault.
        await writerStore.Write(Child(ns, "before"), writerHub.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit("the first child commits", ct);

        var live = readerAccess.RunAsSystem(() => readerServices.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{ns}")))
            .Scan(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase),
                (paths, change) => change.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset =>
                        paths.Clear().Union(change.Items.Select(n => n.Path)),
                    QueryChangeType.Removed => paths.Except(change.Items.Select(n => n.Path)),
                    _ => paths.Union(change.Items.Select(n => n.Path)),
                })
            .Replay(1);
        using var connection = live.Connect();
        await live.Where(p => p.Contains($"{ns}/before")).Should().Within(TestTimeouts.Convergence)
            .Emit("silo 1's live query lists the child committed before the fault", ct);

        // ── The gap ────────────────────────────────────────────────────────────────────────────
        var drop = mesh.Relay.Drop(announceGap: AnnounceGap);
        try
        {
            await writerStore.Write(Child(ns, "during"), writerHub.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit("silo 0 commits a child while the feed is down", ct);
            await drop.Arrivals.Where(a => a.Contains($"{ns}/during", StringComparison.OrdinalIgnoreCase))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the commit's notification reached the relay and was LOST — otherwise this case "
                      + "injected nothing", ct);
        }
        finally
        {
            drop.Release();
        }

        if (AnnounceGap)
        {
            await live.Where(p => p.Contains($"{ns}/during")).Should().Within(TestTimeouts.Convergence)
                .Emit("the reconnect declared the gap, so silo 1's live query re-read the store and "
                      + "converged on the child committed while its feed was down (Plugins#3000)", ct);
        }
        else
        {
            // The negative control: a robust negative on the exact signal the fixed arm waits for.
            await live.Where(p => p.Contains($"{ns}/during")).Should()
                .NotEmit(TimeSpan.FromSeconds(5),
                    "with the notification lost and no gap declared, nothing re-runs the live query — the "
                    + "pre-fix silent staleness. If this arm ever converges, something other than the gap "
                    + "signal is feeding the query and the fixed arm proves nothing", ct);
        }
    }

    private static MeshNode Child(string ns, string id)
        => new(id, ns) { Name = id, NodeType = "Markdown", State = MeshNodeState.Active, Version = 1 };
}

/// <summary>The fix: the reconnect declares the gap.</summary>
public class ALiveQueryConvergesAfterADeclaredChangeFeedGapTest(ALiveQueryConvergesAfterADeclaredChangeFeedGapTest.Cluster mesh)
    : ALiveQueryAcrossALostChangeFeedWindow(mesh), IClassFixture<ALiveQueryConvergesAfterADeclaredChangeFeedGapTest.Cluster>
{
    protected override bool AnnounceGap => true;

    /// <summary>Two silos.</summary>
    public class Cluster : FaultInjectionCluster;
}

/// <summary>Negative control: the pre-fix listener — notifications lost, nobody told.</summary>
public class ALiveQueryStaysStaleAfterASilentChangeFeedGapTest(ALiveQueryStaysStaleAfterASilentChangeFeedGapTest.Cluster mesh)
    : ALiveQueryAcrossALostChangeFeedWindow(mesh), IClassFixture<ALiveQueryStaysStaleAfterASilentChangeFeedGapTest.Cluster>
{
    protected override bool AnnounceGap => false;

    /// <summary>Two silos.</summary>
    public class Cluster : FaultInjectionCluster;
}
