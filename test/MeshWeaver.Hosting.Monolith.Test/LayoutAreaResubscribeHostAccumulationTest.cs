using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Memory-leak guard for the layout-area subscribe protocol (issue #606 — "untyped
/// live-compiled content render hot-loop leaks memory until the pod is killed").
///
/// <para><b>The defect.</b> A <c>SubscribeRequest</c> carries the SUBSCRIBER's
/// <c>StreamId</c>, and <c>JsonSynchronizationStream.Resubscribe</c> deliberately REUSES it
/// ("refresh MY stream"). The owner nevertheless treated every arrival as a brand-new
/// subscription: <c>Workspace.SubscribeToClient</c> → <c>CreateSynchronizationStream</c> →
/// <c>ReduceStream</c>, which for a <c>LayoutAreaReference</c> constructs a WHOLE NEW
/// <c>LayoutAreaHost</c> (the <c>AddWorkspaceReferenceStream</c> factory in
/// <c>LayoutExtensions</c>). The previous host was never disposed — only an
/// <c>UnsubscribeRequest</c> disposes a server-side stream, and a resubscribe never sends
/// one — so it kept rendering and kept pushing frames to the same subscriber forever.</para>
///
/// <para><b>Why it ran away on the ordinary serving path.</b> The mirror's staleness gate
/// ("resubscribe only when demonstrably behind") is INERT for <c>EntityStore</c> reductions:
/// <c>EntityStore</c> has no <c>long Version</c>, so <c>receivedVersion</c> never advances
/// while <c>announcedVersion</c> is fabricated as <c>received + 1</c> for every version-less
/// change-feed event. The gate is therefore permanently open and EVERY change on the owner
/// path resubscribes — so every write to a node that somebody is looking at permanently added
/// one more live render pipeline, each holding its own EntityStore, its menu/node-stream
/// subscriptions (which pin <c>MeshNodeStreamCache</c> entries so their upstream sync streams
/// can never be idle-released) and its whole control tree. Untyped content made it continuous
/// rather than occasional; the accumulation itself is content-agnostic, which is what this
/// test pins.</para>
///
/// <para><b>The measurement.</b> Every live server-side <c>LayoutAreaHost</c> pushes its own
/// <c>Full</c> frame on each node change, so the number of <c>Full</c> frames the client
/// receives for ONE write IS the number of live render pipelines on the owner — a managed,
/// deterministic count that needs no heap walk (the ClrMD probes cannot run on macOS, #674).
/// Before the fix that count grew by one per write (measured 1, 4, 5, 6, 7, 8, 9, 10 …);
/// after it, it is flat.</para>
/// </summary>
public class LayoutAreaResubscribeHostAccumulationTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>Number of node writes driven through the subscribed area.</summary>
    private const int Writes = 10;

    /// <summary>Settle window after each write — the render + any resubscribe refresh land inside it.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1500);

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact(Timeout = 180_000)]
    public async Task RepeatedWrites_DoNotStackLayoutAreaHosts_OnTheOwner()
    {
        var id = $"resub-accum-{Guid.NewGuid():N}"[..24];
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Resubscribe Accumulation Guard",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# guard" },
        }).Should().Emit();

        var client = GetClient(c => c.AddData(data => data));
        var address = new Address(path);
        await client.Observe(new PingRequest(), o => o.WithTarget(address)).Should().Emit();

        var workspace = client.GetWorkspace();
        var areaStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea));

        var first = await areaStream.Should().Within(30.Seconds()).Emit();
        first.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "the Overview area must render before the accumulation can be measured");

        var fulls = 0;
        using var areaSub = areaStream.Subscribe(
            ci => { if (ci.ChangeType == ChangeType.Full) Interlocked.Increment(ref fulls); },
            _ => { });

        // Let the initial render settle so the first measured write starts from a quiet stream.
        await Task.Delay(Settle);

        var fullsPerWrite = new int[Writes];
        for (var i = 0; i < Writes; i++)
        {
            Interlocked.Exchange(ref fulls, 0);
            var revision = i;
            await workspace.GetMeshNodeStream(path)
                .Update(cur => cur with { Description = $"rev{revision}" })
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
            await Task.Delay(Settle);
            fullsPerWrite[i] = Volatile.Read(ref fulls);
        }

        Output.WriteLine($"Full frames per write: [{string.Join(", ", fullsPerWrite)}]");

        // Compare STEADY STATE to STEADY STATE: write 0 can legitimately differ (the very first
        // change-feed pulse arms machinery that is already armed for every later write), so the
        // baseline is write 1. The invariant is that the count does not GROW — a growing count
        // means one more live LayoutAreaHost per write on the owner, none of them ever released.
        var baseline = fullsPerWrite[1];
        var last = fullsPerWrite[^1];
        var peak = fullsPerWrite.Skip(1).Max();

        last.Should().BeLessThanOrEqualTo(baseline + 2,
            $"a resubscribe must REFRESH the server-side stream for its StreamId, never stack a second "
            + $"LayoutAreaHost — one Full frame per live render pipeline means the count per write must "
            + $"stay flat, not grow with the number of writes. Measured per-write Full frames: "
            + $"[{string.Join(", ", fullsPerWrite)}]");

        peak.Should().BeLessThanOrEqualTo(baseline + 2,
            $"no single write may fan out to materially more render pipelines than the steady-state "
            + $"baseline. Measured per-write Full frames: [{string.Join(", ", fullsPerWrite)}]");
    }
}
