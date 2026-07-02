using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic repro for issue #185 residual 1: <c>InstallReleaseRequestWatcher</c>
/// advanced its process-local <c>dispatchHighWater</c> EAGERLY in the Subscribe
/// callback — BEFORE the <c>workspace.GetMeshNodeStream().Update(...)</c> that
/// stamps <c>LastReleaseRequestHandledAt</c> commits. The Update lambda has bail
/// paths (status already Pending/Compiling, trigger already handled) that return
/// <c>curr</c> WITHOUT stamping — yet the high-water was already past the trigger,
/// so the post-settle re-emission failed the <c>req &gt; dispatchHighWater</c> gate
/// and the trigger was LOST for the lifetime of the process.
///
/// <para><b>The forced interleaving</b> (all four writes serialize FIFO on the
/// owner's MeshNode stream hub as <c>UpdateStreamRequest</c>s):</para>
/// <list type="number">
///   <item>A gate write parks the owner's stream action block (a handshake
///     latch confirms the park has ENGAGED before anything else is posted),
///     so the two trigger writes P1 (req=T1) and P2 (req=T2) are BOTH
///     enqueued before either applies. This is what makes the interleaving
///     deterministic: the watcher's own Update U1 is only posted after P1
///     applies (after the gate opens), so U1 provably lands BEHIND P2 in
///     the queue.</item>
///   <item>P1 applies → emission (req=T1, status settled) → watcher fires,
///     posts U1.</item>
///   <item>P2 applies → emission (req=T2, status STILL settled — U1 hasn't
///     applied) → watcher fires again (T2 &gt; T1), posts U2.</item>
///   <item>U1 applies → commits Pending + stamps LastReleaseRequestHandledAt=T1
///     → compile dispatches.</item>
///   <item>U2 applies → status is Pending → bail → NO stamp. With the eager
///     advance, dispatchHighWater is already T2, so when the compile settles
///     the re-emission (req=T2 &gt; handled=T1, status Ok) fails
///     <c>req &gt; dispatchHighWater</c> — T2 never dispatches. LOST.</item>
/// </list>
///
/// <para><b>The contract this test pins</b> (the watcher's own in-code promise:
/// "a still-unhandled fresher request re-fires on the next settled emission"):
/// after the burst settles, <c>LastReleaseRequestHandledAt</c> must reach T2.
/// Fixed by advancing the high-water ONLY on the Update COMMIT path — where
/// <c>LastReleaseRequestHandledAt</c> is actually stamped.</para>
/// </summary>
public class ReleaseRequestWatcherHighWaterTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), $"MeshWeaverHighWaterTest-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_cacheDir);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = _cacheDir;
                    o.EnableCompilationCache = true;
                    o.EnableDiskCache = true;
                }));
    }

    /// <summary>Async disposal — xUnit v3 awaits this naturally, so the mesh tears
    /// down exactly once with no sync-over-async blocking; the per-test compile
    /// cache dir is cleaned after the mesh (and its compile pipeline) is gone.</summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private const string CodeV1 = """
        using MeshWeaver.Layout.Composition;
        public static class HighWaterLayoutAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>HIGHWATER_V1</div>");
        }
        """;

    [Fact(Timeout = 60000)]
    public async Task BurstOfTwoReleaseTriggers_SecondTriggerIsNotLost()
    {
        var typePath = $"{TestPartition}/HighWaterType";

        // 1. Create the NodeType + a valid source; the first-build kickoff
        //    compiles it. Wait for the settled Ok state (this also guarantees
        //    RequestedReleaseAt / LastReleaseRequestHandledAt are still null —
        //    the kickoff flips Pending directly, never the release trigger).
        await NodeFactory.CreateNode(new MeshNode("HighWaterType", TestPartition)
        {
            Name = "High Water Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Repro for the eager high-water advance (issue #185).",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", HighWaterLayoutAreas.Overview))",
            }
        }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(50.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath)
                && d.RequestedReleaseAt is null
                && d.LastReleaseRequestHandledAt is null);
        Output.WriteLine("=== first build settled Ok ===");

        // Diagnostic tail: print every (status, req, handled) transition so a
        // timeout failure shows exactly where the state machine stopped.
        using var diag = Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition)
            .Select(n => (NodeTypeDefinition)n!.Content!)
            .DistinctUntilChanged(d => (d.CompilationStatus, d.RequestedReleaseAt, d.LastReleaseRequestHandledAt))
            .Subscribe(d => Output.WriteLine(
                $"[node] status={d.CompilationStatus} req={d.RequestedReleaseAt:O} handled={d.LastReleaseRequestHandledAt:O}"));

        // 2. Grab the per-NodeType hub — the OWNER. Its own-stream writes are
        //    the ones that serialize FIFO with the watcher's Update.
        var nodeHub = Mesh.GetHostedHub(new Address(typePath), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("the per-NodeType hub must be live after the first compile");
        var ownWs = nodeHub!.GetWorkspace();

        // 3. Park the owner's MeshNode stream action block with a no-op gate
        //    write, then enqueue BOTH trigger writes while it is parked. This
        //    forces the queue order [gate, P1, P2, U1, U2]: the watcher's U1
        //    can only be posted after P1 applies — after the gate opens — so it
        //    provably lands behind P2. Both trigger emissions therefore pass
        //    the watcher's settled-status Where before U1 flips Pending.
        //
        //    `parked` is the handshake that PROVES the park engaged: it is set at
        //    the top of the gate lambda (i.e. the gate write is actively executing
        //    on the owner's serialized write path), and the test blocks on it
        //    BEFORE enqueueing the triggers. Without it, on a busy runner the gate
        //    write could still be queued when the triggers are posted and only
        //    execute after gate.Set() — nothing parked, the interleaving silently
        //    degrades to ordinary timing, and the test could false-PASS a future
        //    regression. Cross-thread ManualResetEventSlim handshake is the
        //    project's established pattern for synchronous Subscribe-side signals
        //    (see DeleteNodeBehaviorTest); both waits are bounded so a broken run
        //    can never wedge the suite.
        using var parked = new ManualResetEventSlim(false);
        using var gate = new ManualResetEventSlim(false);
        ownWs.GetMeshNodeStream().Update(curr =>
        {
            // Handshake: the serialized write queue is now provably parked.
            parked.Set();
            if (!gate.Wait(TimeSpan.FromSeconds(20)))
                Output.WriteLine("!!! gate wait timed out — interleaving no longer forced");
            return curr; // no-op
        }).Subscribe(
            _ => { },
            ex => Output.WriteLine($"gate write failed: {ex}"));

        parked.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue(
            "the gate write must be executing (owner write queue parked) before the "
            + "triggers are enqueued — otherwise the lost-trigger interleaving is not forced");

        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddMilliseconds(200);
        ownWs.GetMeshNodeStream().Update(curr =>
            curr.Content is not NodeTypeDefinition d
                ? curr
                : curr with
                {
                    Content = d with { RequestedReleaseAt = t1, RequestedReleaseForce = true }
                })
            .Subscribe(_ => { }, ex => Output.WriteLine($"P1 write failed: {ex}"));
        ownWs.GetMeshNodeStream().Update(curr =>
            curr.Content is not NodeTypeDefinition d
                ? curr
                : curr with
                {
                    Content = d with { RequestedReleaseAt = t2, RequestedReleaseForce = true }
                })
            .Subscribe(_ => { }, ex => Output.WriteLine($"P2 write failed: {ex}"));

        Output.WriteLine($"=== triggers enqueued: t1={t1:O} t2={t2:O} — opening gate ===");
        gate.Set();

        // 4. The CONTRACT: every trigger is eventually handled. The watcher's
        //    Update for T2 bails (status is Pending from T1's dispatch), so T2
        //    must re-fire on the post-settle emission and be stamped. With the
        //    eager high-water advance, that re-fire is gated off and
        //    LastReleaseRequestHandledAt stays at t1 forever — this wait times
        //    out, pinning the lost trigger.
        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(45.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.LastReleaseRequestHandledAt is { } handled
                && handled >= t2);
        Output.WriteLine("=== t2 handled ===");

        // And the whole machine settles cleanly afterwards.
        await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Should().Within(45.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok);
    }
}
