using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 IS THE PER-COMPILE <c>_Activity</c> HUB RETENTION BOUNDED, OR IS IT A LEAK?
///
/// <para><see cref="NodeTypeRecompileAlcLeakTest"/> measures what a recompile RETAINS at the moment
/// it finishes — 6 hubs and 3–5 MB, essentially all of it the per-compile
/// <c>{type}/_Activity/compile-&lt;ts&gt;</c> node hub plus the <c>sync/</c> sub-hubs its data source
/// builds. That test runs for seconds, so it cannot distinguish "never reclaimed" from "reclaimed on
/// a clock it does not wait for", and #1324 spent two passes assuming the former.</para>
///
/// <para>It is the latter. An Activity node hub reaches
/// <c>KernelContainer.DisposeOnTimeout</c> — a one-shot idle timer — through
/// <c>ActivityNodeType.CreateMeshNode</c> → <c>.AddKernelSubHubHandlers()</c> →
/// <c>IKernelHubConfigurator.ConfigureSubHub</c>, registered by <c>KernelNodeType.AddKernel()</c>
/// which <c>AddGraph()</c> includes. That chain is live in the monolith, not only on Orleans. The
/// reason it never fired is the defect #1435 closed: the timer is re-armed by EVERY inbound message
/// and a finished activity's still-warm mirror posted a <c>HeartBeatEvent</c> every 45 s
/// (<c>SyncStreamOptions.HeartbeatInterval</c>) expressly to keep the hub alive. With the mirror
/// released on the terminal write, nothing is left to re-arm it.</para>
///
/// <para>This test runs the same recompile loop and then WAITS, polling for the per-compile activity
/// hubs to disappear, so the answer is a number rather than an argument. It runs on a <b>1:20 scale
/// model</b> of production — <see cref="IdleWindow"/> and <see cref="ScaledHeartbeat"/> compressed
/// TOGETHER, for the reason spelled out on <see cref="IdleWindow"/>: shortening the window alone
/// would put the 45 s heartbeat outside it and turn the guard into a false pass. The production
/// defaults are untouched and asserted by
/// <see cref="ProductionIdleDisconnectTimeout_IsTheDocumentedCeiling"/>.</para>
///
/// <para>A scale model can only cover re-armers it scales, so it was checked against the real
/// thing. <b>Unscaled, at production values — 15 min window, real 45 s heartbeat — the same
/// population sat flat at 15 hubs through t+885s, dropped to 5 at t+900s and reached 0 at t+915s:
/// 13 hubs reclaimed in 915 s (15.25 min), one idle window plus the 15 s poll granularity, with the
/// mesh's total hub count going 49 → 32.</b> That run covers every OTHER periodic toucher the mesh
/// has at its real cadence; it is recorded on #1324 and is far too slow to keep in CI, which is what
/// this scaled test is for — the ratchet that keeps the property from regressing between such runs.</para>
///
/// <para>🚨 <b>Shortening the production window is NOT the fix and must never become one.</b> #1324
/// prohibits it explicitly, and #1435's whole lesson is that the clocks were fine — something was
/// resetting them. The override here is a lens, not a cure.</para>
/// </summary>
public class CompileActivityHubRetentionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "ActivityRetentionTest";
    private const string TypeId = "RetentionType";
    private const string TypePath = $"{Partition}/{TypeId}";

    /// <summary>
    /// The idle-disconnect window used by THIS test's mesh — a 1:20 SCALE MODEL of production
    /// (45 s heartbeat : 15 min window), not merely a smaller number.
    ///
    /// <para>🚨 The scaling has to be done in PAIRS or the test is a false pass. The re-armer this
    /// property is really about — the mirror's <c>HeartBeatEvent</c> — fires on
    /// <see cref="SyncStreamOptions.HeartbeatInterval"/>, and shortening only the window would put
    /// the 45 s heartbeat OUTSIDE it: the timer would fire between two heartbeats, the test would go
    /// green, and production (where 45 s sits far inside 15 min) would still never reclaim
    /// anything. Compressed together, a regression of #1435 — a finished activity whose mirror is
    /// left warm — keeps the hub alive here exactly as it does in production, and this test reds.</para>
    ///
    /// <para>🚨 The production values are 45 s and 15 min and stay that way. See the class remarks;
    /// the wall-clock measurement at the real horizon is recorded on #1324.</para>
    /// </summary>
    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(20);

    /// <summary>The heartbeat cadence, scaled with <see cref="IdleWindow"/> at production's 1:20.</summary>
    private static readonly TimeSpan ScaledHeartbeat = TimeSpan.FromSeconds(1);

    /// <summary>How many times to recompile, so several per-compile activities exist to reclaim.</summary>
    private const int Recompiles = 2;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton(new KernelHubOptions { IdleDisconnectTimeout = IdleWindow });
                services.Configure<SyncStreamOptions>(o =>
                {
                    o.HeartbeatInterval = ScaledHeartbeat;
                    o.FirstHeartbeat = ScaledHeartbeat;
                });
                return services;
            });

    /// <summary>
    /// The ceiling this issue closes on. Stated as an assertion rather than a comment so that
    /// shortening the production timer — the one cure #1324 forbids — cannot happen quietly.
    /// </summary>
    [Fact]
    public void ProductionIdleDisconnectTimeout_IsTheDocumentedCeiling() =>
        new KernelHubOptions().IdleDisconnectTimeout.Should().Be(TimeSpan.FromMinutes(15),
            "the per-compile activity hub's retention is BOUNDED by this window, and that bound is "
            + "what #1324 closes on. Changing it changes the documented ceiling — and shortening it "
            + "to make a memory number look better is the band-aid the issue explicitly prohibits: "
            + "#1435 established that the clocks were correct and a heartbeat was resetting them");

    [Fact(Timeout = 600_000)]
    public async Task PerCompileActivityHubs_AreReclaimedOnceTheirIdleWindowElapses()
    {
        await NodeFactory.CreateNode(new MeshNode(TypeId, Partition)
        {
            Name = "Retention Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Recompiled to measure how long its compile activities are retained.",
                Configuration = "config => config"
            }
        }).Should().Within(30.Seconds()).Emit();

        var first = await WhenCompiled(TypePath, null);
        var lastSucceeded = first.LastCompileSucceededAt;

        for (var i = 1; i <= Recompiles; i++)
        {
            var generation = i;
            await Mesh.GetWorkspace().GetMeshNodeStream(TypePath)
                .Update(node => node.Content is NodeTypeDefinition def
                    ? node with
                    {
                        Content = def with
                        {
                            Configuration = $"config => config /* gen {generation} */",
                            CompilationStatus = CompilationStatus.Pending
                        }
                    }
                    : node)
                .FirstAsync()
                .Timeout(30.Seconds())
                .ToTask();

            var settled = await WhenCompiled(TypePath, lastSucceeded);
            lastSucceeded = settled.LastCompileSucceededAt;
        }

        // The population under measurement: the hub for each per-compile `compile-<ts>` activity
        // node, and every hub hosted under it (its data source's own sync/ streams). `compile-state`
        // is deliberately NOT here — it is the type's LIVE compile state, one fixed-id node per
        // NodeType rather than one per compile, so it is rewritten by the next compile and its hub
        // staying warm is correct, not a retention.
        var retained = CompileActivityHubs();
        Output.WriteLine(
            $"after {Recompiles} recompiles: {retained.Count} hubs belong to per-compile activities");
        foreach (var address in retained)
            Output.WriteLine($"  {address}");

        // If nothing is retained the measurement below is vacuous — a poll that succeeds on its
        // first tick would report "reclaimed in 0 s" for a mesh that never held anything. Establish
        // the subject exists before timing its disappearance.
        retained.Should().NotBeEmpty(
            "the residual this measures is the per-compile activity node hub and its sync sub-hubs; "
            + "with none present the reclamation timing below would be measuring nothing");

        // 🚨 THE MEASUREMENT. Poll for the population to empty and record how long it took. Waiting
        // on the CONDITION rather than sleeping the window is what makes this deterministic under CI
        // load (WritingTests.md) — and it is also what produces the number: the elapsed time IS the
        // retention duration, which is the quantity #1324 asks for.
        var clock = Stopwatch.StartNew();
        var remaining = retained;
        await Observable.Interval(250.Milliseconds()).StartWith(0L)
            .Select(_ => remaining = CompileActivityHubs())
            .Where(hubs => hubs.Count == 0)
            .FirstAsync()
            .Timeout(IdleWindow + 120.Seconds(), Observable.Return(remaining))
            .ToTask();
        clock.Stop();

        Output.WriteLine(
            $"RETENTION: {retained.Count} per-compile activity hubs reclaimed after "
            + $"{clock.Elapsed.TotalSeconds:F1}s against a {IdleWindow.TotalSeconds:F0}s idle window "
            + $"({remaining.Count} still live). Production window is "
            + $"{new KernelHubOptions().IdleDisconnectTimeout.TotalMinutes:F0} min.");

        remaining.Should().BeEmpty(
            $"a per-compile activity is never written again once its compile ends, so nothing re-arms "
            + $"KernelContainer's idle timer and its hub must be disposed one window "
            + $"({IdleWindow.TotalSeconds:F0}s) later — the retention is BOUNDED, not a leak. Anything "
            + $"still live after {clock.Elapsed.TotalSeconds:F0}s is something re-arming the timer, and "
            + $"THAT is the defect to name. Do NOT shorten the window (#1324). Still live: "
            + string.Join(", ", remaining));

        // The bound has to be the window, not merely "eventually". A reclamation that took several
        // windows would mean the timer is being re-armed and only stops when the re-armer gives up,
        // which is a different (and still open) defect wearing the same green tick.
        clock.Elapsed.Should().BeLessThan(IdleWindow + 60.Seconds(),
            "the retention ceiling is ONE idle window plus the disposal itself; taking materially "
            + "longer means something re-armed the timer after the activity went terminal");
    }

    /// <summary>
    /// Every live hub belonging to a per-compile <c>_Activity/compile-&lt;ts&gt;</c> node: the node
    /// hub itself and every hub hosted under it, read off the mesh's indented disposal tree (the
    /// only public surface that enumerates hosted hubs). <c>compile-state</c> is excluded — see the
    /// call site.
    /// </summary>
    private List<string> CompileActivityHubs()
    {
        const string marker = "/_Activity/compile-";
        var found = new List<string>();
        var stack = new List<string>();
        foreach (var line in Mesh.GetDisposalDiagnostics().Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)Hub (\S+) RunLevel");
            if (!m.Success) continue;
            var depth = m.Groups[1].Value.Length / 2;
            var address = m.Groups[2].Value;
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            var parent = stack.Count > 0 ? stack[^1] : "<root>";
            stack.Add(address);
            var owned = IsPerCompileActivity(address) || IsPerCompileActivity(parent);
            if (owned) found.Add(address);
        }
        return found;

        static bool IsPerCompileActivity(string address) =>
            address.Contains(marker, StringComparison.Ordinal)
            && !address.EndsWith($"/{NodeTypeCompileStateMirror.StateId}", StringComparison.Ordinal);
    }

    private Task<NodeTypeDefinition> WhenCompiled(string path, DateTimeOffset? after) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Select(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null
                        && d!.CompilationStatus == CompilationStatus.Ok
                        && (after is null || d.LastCompileSucceededAt > after))
            .Select(d => d!)
            .Take(1)
            .Timeout(120.Seconds())
            .ToTask();
}
