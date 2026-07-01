using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Narrows the native-MAUI "keystone". On-device (maccatalyst) a node page — even a pure-prose Markdown doc
/// (verified with "Deployment — AKS") — renders its title + metadata but its Overview CONTENT sub-areas spin
/// forever. This test drives the EXACT path the client uses (<c>NodeAreaView</c> →
/// <c>GetRemoteStream&lt;JsonElement, LayoutAreaReference&gt;((Address)nodePath, new LayoutAreaReference("Overview"))</c>)
/// against the full mesh with REAL per-node hubs, and asserts the Overview control AND each nested sub-area
/// resolve.
/// <para><b>Result: every method here PASSES</b> — the Overview (a StackControl with 4 sub-areas: header
/// Stack, body Html/Collab, an Approvals <c>LayoutAreaControl</c> embed, comments Stack) + each sub-area
/// resolve over the per-node-hub remote stream. Across the methods below this rules OUT, in-process, the
/// three variables that differ from the real client: (1) per-node-hub topology, (2) Admin-vs-device-user
/// identity (<see cref="NodeOverview_AsDeviceUser_SubAreasResolve"/>), and (3) parent-Overview churn
/// (<see cref="NodeOverview_DoesNotChurn"/> — it emits exactly once). The data path is sound.</para>
/// <para>So the on-device perpetual spinner (title + metadata render, 3 content areas spin past the 20s
/// deadline, then a crash) is NOT any of those. The remaining differences are the full MAUI-client runtime
/// (SQLite persistence, <c>AddDocumentation</c> content-collection-backed bodies, the native ContainerView →
/// RenderArea render pipeline these data-path tests bypass) — which only reproduce on-device. On-device
/// instrumentation is currently blocked (the dev build's file log is foiled by app kill/sandbox), so cracking
/// it needs a working device log channel or a test that stands up the exact client mesh config + a real doc.</para>
/// </summary>
public class MauiNodeOverviewAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Namespace = $"{TestPartition}/MauiOverview";

    [Fact(Timeout = 90_000)]
    public async Task NodeOverview_TopControl_ResolvesOverRemoteStream()
    {
        var path = $"{Namespace}/doc1";
        await NodeFactory.CreateNode(new MeshNode("doc1", Namespace)
        {
            Name = "Doc One",
            NodeType = "Markdown",
        }).Should().Emit();

        var workspace = Mesh.GetWorkspace();
        // EXACTLY what NodeAreaView / the MAUI LayoutAreaView(remote) does for a node page.
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            (Address)path, new LayoutAreaReference("Overview"));

        var root = await stream.GetControlStream("Overview")
            .Where(c => c is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        root.Should().NotBeNull("the node Overview top control must render over the per-node-hub remote stream");
        Output.WriteLine($"Overview root: {root!.GetType().Name}");
    }

    [Fact(Timeout = 90_000)]
    public async Task NodeOverview_NestedSubAreas_ResolveOverRemoteStream()
    {
        var path = $"{Namespace}/doc2";
        await NodeFactory.CreateNode(new MeshNode("doc2", Namespace)
        {
            Name = "Doc Two",
            NodeType = "Markdown",
        }).Should().Emit();

        var workspace = Mesh.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            (Address)path, new LayoutAreaReference("Overview"));

        var root = await stream.GetControlStream("Overview")
            .Where(c => c is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        Output.WriteLine($"Overview root: {root!.GetType().Name}");

        // The MAUI RenderArea-per-child path: for every nested NamedAreaControl the container declares, the
        // view opens GetControlStream(child.Area) on the SAME stream. Each must resolve — a spinner-forever
        // is a child whose stream never emits.
        if (root is IContainerControl container)
        {
            var areas = container.Areas.ToList();
            Output.WriteLine($"Overview declares {areas.Count} sub-area(s): {string.Join(", ", areas.Select(a => a.Area))}");
            foreach (var named in areas)
            {
                var area = named.Area!.ToString()!;
                var sub = await stream.GetControlStream(area)
                    .Where(c => c is not null)
                    .FirstAsync().Timeout(30.Seconds()).ToTask();
                sub.Should().NotBeNull($"nested Overview sub-area '{area}' must resolve over the remote stream (not spin forever)");
                Output.WriteLine($"  {area} -> {sub!.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// CHURN probe. On-device the Overview spinners persist PAST the 20s "couldn't load" deadline without ever
    /// flipping to the notice — the tell of a host being recreated faster than the deadline (the parent
    /// Overview re-emitting → MAUI rebuilds the child subtree → fresh spinner + fresh deadline), which also
    /// explains a render-storm crash. The Overview observable is
    /// <c>GetMeshNodeStream().CombineLatest(GetEffectivePermissions(...))</c>; if either side re-emits, the
    /// whole Overview re-renders. This counts Overview emissions over a quiet window — a stable area emits a
    /// small, bounded number of times; continuous churn is the bug.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task NodeOverview_DoesNotChurn()
    {
        var path = $"{Namespace}/doc3";
        await NodeFactory.CreateNode(new MeshNode("doc3", Namespace)
        {
            Name = "Doc Three",
            NodeType = "Markdown",
        }).Should().Emit();

        var workspace = Mesh.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            (Address)path, new LayoutAreaReference("Overview"));

        var count = 0;
        using var sub = stream.GetControlStream("Overview")
            .Where(c => c is not null)
            .Subscribe(_ => Interlocked.Increment(ref count));

        // Wait for the first render, then observe a quiet 5s window — a "confirm nothing keeps happening"
        // negative test, so a fixed delay is the sanctioned shape (no positive signal to filter for).
        await stream.GetControlStream("Overview").Where(c => c is not null).FirstAsync().Timeout(30.Seconds()).ToTask();
        await Task.Delay(5000);

        Output.WriteLine($"Overview emitted {count} time(s) over the observation window");
        count.Should().BeLessThan(10, "the Overview must stabilise, not re-emit continuously (churn → perpetual spinner + render-storm crash)");
    }

    /// <summary>
    /// The untested variable: the MAUI client runs as an anonymous DEVICE USER
    /// (<c>AccessContext{ ObjectId = "device-user" }</c>), not Admin. Every prior in-process test resolved the
    /// Overview as Admin. This opens the same node's Overview under the device-user identity and asserts the
    /// top control + each sub-area resolve — if a sub-area is access-gated for the device user, its render is
    /// skipped and its <c>GetControlStream</c> never emits → the on-device perpetual spinner. A hang here (15s
    /// timeout on a sub-area) reproduces the bug deterministically.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task NodeOverview_AsDeviceUser_SubAreasResolve()
    {
        var path = $"{Namespace}/doc4";
        await NodeFactory.CreateNode(new MeshNode("doc4", Namespace)
        {
            Name = "Doc Four",
            NodeType = "Markdown",
        }).Should().Emit();

        // Switch the circuit to the anonymous device user — exactly the identity MauiProgram sets.
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = "device-user", Name = "Device" });
        try
        {
            var workspace = Mesh.GetWorkspace();
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                (Address)path, new LayoutAreaReference("Overview"));

            var root = await stream.GetControlStream("Overview")
                .Where(c => c is not null)
                .FirstAsync().Timeout(30.Seconds()).ToTask();
            Output.WriteLine($"[device-user] Overview root: {root!.GetType().Name}");

            if (root is IContainerControl container)
            {
                var areas = container.Areas.ToList();
                Output.WriteLine($"[device-user] Overview declares {areas.Count} sub-area(s): {string.Join(", ", areas.Select(a => a.Area))}");
                foreach (var named in areas)
                {
                    var area = named.Area!.ToString()!;
                    var sub = await stream.GetControlStream(area)
                        .Where(c => c is not null)
                        .FirstAsync().Timeout(15.Seconds()).ToTask();
                    sub.Should().NotBeNull($"[device-user] sub-area '{area}' must resolve (not spin forever)");
                    Output.WriteLine($"  [device-user] {area} -> {sub!.GetType().Name}");
                }
            }
        }
        finally
        {
            TestUsers.DevLogin(Mesh, TestUsers.Admin);
        }
    }
}
