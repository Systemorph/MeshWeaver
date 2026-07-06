using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro for the sync-storm memory-leak / wedge: a hub whose init gate never opens (a
/// client sync/cache hub whose <c>[Initialize]</c> never fires) used to defer EVERY inbound message +
/// arm a 30s timer each, so a writer flooding it accumulated the deferred buffer without bound until
/// the action block starved and <c>/healthz</c> timed out (memory 4.3Gi on memex.localhost, cleared
/// only by a restart). The stuck-gate safety net (<c>MessageService.MaxDeferredMessages</c>) caps the
/// deferred backlog and drops the overflow, so memory stays bounded no matter how hard the stuck hub
/// is flooded. This test floods a never-opening-gate hub well past the cap and asserts the deferred
/// backlog does NOT grow past it.
/// </summary>
public class DeferredBacklogBoundedTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A plain non-system, non-[CanBeIgnored] message → defers behind a closed gate.</summary>
    private record Filler(int N);

    /// <summary>Mirror of the internal <c>MessageService.MaxDeferredMessages</c> — kept in sync deliberately.</summary>
    private const int Cap = 512;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(Filler))
            // A user gate that never opens: every non-bypass message defers behind it forever —
            // exactly a client sync/cache hub whose [Initialize] never fires.
            .WithInitializationGate("test-never-opens", _ => false);

    [Fact(Timeout = 30000)]
    public async Task DeferredBacklog_StaysBounded_WhenGateNeverOpens()
    {
        var host = GetHost();

        // Flood well past the cap. WITHOUT the safety net every one of these queues in the deferred
        // buffer (+ a 30s timer each) — the unbounded accumulation that OOM-wedged the portal.
        const int posts = Cap + 300;
        for (var i = 0; i < posts; i++)
            host.Post(new Filler(i), o => o.WithTarget(host.Address));

        // Poll the PUBLIC disposal diagnostics (reports "deferred=<N>" per hub) until the deferred
        // backlog is stable across two reads — the single-threaded action block defers messages one at
        // a time, so the count climbs to the cap and then holds as overflow is dropped.
        int Deferred()
        {
            var diag = host.GetDisposalDiagnostics();
            var max = 0;
            foreach (Match m in Regex.Matches(diag, @"deferred=(\d+)"))
                max = Math.Max(max, int.Parse(m.Groups[1].Value));
            return max;
        }

        int prev = -1, cur = Deferred();
        for (var i = 0; i < 120 && cur != prev; i++)
        {
            await Task.Delay(100);
            prev = cur;
            cur = Deferred();
        }

        Assert.True(cur > 0, $"messages should have deferred behind the never-opening gate (sanity; saw {cur})");
        Assert.True(cur <= Cap,
            $"deferred backlog must stay bounded at the cap {Cap}; saw {cur} — unbounded accumulation is the wedge");
    }
}
