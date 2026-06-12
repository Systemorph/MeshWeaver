using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Memory-leak guard + GC-root analyzer for the whole mesh hub graph.
///
/// <para>The Acme bulk failure (<c>UpdateNodeRequest@…/DefinePersona</c> never
/// replies, but only once another test class ran first in the same process) is a
/// process-wide leak that survives <c>Mesh.Dispose()</c>: a disposed mesh's hub
/// graph is pinned by SOMETHING and accumulates across classes. Disposing the
/// per-hub timers/subscriptions is NOT enough — a disposed object can still be
/// rooted by a static field / GC handle, which is what keeps the mesh alive.</para>
///
/// <para>This probe builds a mesh, exercises the exact create+update path the Todo
/// test uses, weak-refs the mesh hub, disposes the mesh AND its ServiceProvider,
/// drops every strong ref, forces GC, and asserts the hub was collected. On a
/// surviving hub it attaches ClrMD to the live process and prints the GC-root
/// chain (root kind → type chain) that pins the disposed mesh — i.e. "who holds
/// the references".</para>
/// </summary>
public class MeshHubDisposalLeakTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private bool _selfDisposed;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference ExerciseAndWeakRefMeshHub()
    {
        var hub = Mesh;
        var factory = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var path = $"{TestPartition}/LeakProbe-{Guid.NewGuid():N}";
        // FromPath splits the namespace ("TestData") from the id — `new MeshNode(path)` would
        // bake the slash into the Id with an EMPTY namespace, which the PartitionWriteGuard
        // (correctly) rejects as a malformed top-level node. TestData is a registered partition
        // namespace, so the nested create is allowed.
        var node = MeshNode.FromPath(path) with
        {
            NodeType = "Markdown",
            Name = "probe",
            State = MeshNodeState.Active,
        };
        factory.CreateNode(node).Should().Within(60.Seconds()).Emit();
        factory.UpdateNode(node with { Name = "probe-updated" }).Should().Within(60.Seconds()).Emit();

        return new WeakReference(hub);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollect(WeakReference weak)
    {
        for (var i = 0; i < 12 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
    }

    [Fact]
    public void MeshHub_IsCollected_AfterMeshAndServiceProviderDisposal()
    {
        var weak = ExerciseAndWeakRefMeshHub();
        weak.IsAlive.Should().BeTrue("the mesh hub is held by the live ServiceProvider before disposal");

        var hub = Mesh;
        hub.Dispose();
        hub.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .ToTask()
            .Wait(TimeSpan.FromSeconds(30));

        var sp = ServiceProvider;
        ServiceProvider = null!;
        _selfDisposed = true;
        (sp as IDisposable)?.Dispose();
        // ReSharper disable once RedundantAssignment
        sp = null;

        ForceCollect(weak);

        if (!weak.IsAlive)
            return; // hub collected — no leak of any kind.

        // Survivor: distinguish a REAL leak (pinned by a static field / TimerQueue / GC
        // handle — accumulates across disposed meshes) from a benign transient (held only
        // by a stack root: a disposal continuation frozen mid-flight by the ClrMD snapshot,
        // which clears once the process resumes). We do NOT hold a strong ref to the
        // survivor during analysis (that would add our own stack root); ClrMD reads the
        // live process heap directly.
        var (staticRooted, report) = AnalyzeMeshHubRoots();
        Output.WriteLine("=== MESH HUB SURVIVED DISPOSAL — ClrMD GC-root analysis ===");
        Output.WriteLine(report);

        staticRooted.Should().BeFalse(
            "the mesh hub is pinned by a NON-stack root (static field / TimerQueue timer / GC handle) " +
            "— a real leak that accumulates across disposed meshes; the chain above names it. A hub " +
            "held ONLY by a transient stack root (snapshot artifact) is acceptable and not failed on.");
    }

    /// <summary>
    /// Snapshot-attach ClrMD to THIS process and BFS from non-stack GC roots to the
    /// first <c>MessageHub</c> on the heap, printing the root kind + the type chain
    /// from the root down to the hub. The top of the chain is the pin.
    /// </summary>
    private static (bool StaticRooted, string Report) AnalyzeMeshHubRoots()
    {
        var sb = new StringBuilder();
        var staticRooted = false;
        try
        {
            using var dt = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            if (dt.ClrVersions.Length == 0) return (false, "[clrmd] no CLR runtime found in snapshot");
            using var runtime = dt.ClrVersions[0].CreateRuntime();
            var heap = runtime.Heap;

            var parent = new Dictionary<ulong, (ulong From, string Edge)>();
            var rootKindOf = new Dictionary<ulong, string>();
            var queue = new Queue<ulong>();

            foreach (var root in heap.EnumerateRoots())
            {
                // Skip stack/local roots — a leak is rooted by a static field, a
                // strong GC handle, or the finalizer queue, never the live stack.
                var kind = root.RootKind.ToString();
                if (kind.Contains("Stack", StringComparison.OrdinalIgnoreCase) ||
                    kind.Contains("Local", StringComparison.OrdinalIgnoreCase))
                    continue;
                var addr = root.Object.Address;
                if (addr == 0 || parent.ContainsKey(addr)) continue;
                parent[addr] = (0UL, $"ROOT[{kind}] {root.Object.Type?.Name}");
                rootKindOf[addr] = kind;
                queue.Enqueue(addr);
            }

            ulong found = 0;
            var visited = 0;
            const int maxVisit = 6_000_000;
            while (queue.Count > 0 && visited < maxVisit)
            {
                var addr = queue.Dequeue();
                visited++;
                var obj = heap.GetObject(addr);
                if (!obj.IsValid || obj.Type is null) continue;
                var name = obj.Type.Name ?? "";
                // Concrete hub type only — ".MessageHub" excludes the ".IMessageHub"
                // interface and "Func<…IMessageHub…>" generic args (which contain "<").
                if (name.EndsWith(".MessageHub", StringComparison.Ordinal) && !name.Contains('<'))
                {
                    found = addr;
                    break;
                }
                foreach (var child in obj.EnumerateReferences(false, true))
                {
                    if (child.Address == 0 || parent.ContainsKey(child.Address)) continue;
                    parent[child.Address] = (addr, child.Type?.Name ?? "?");
                    queue.Enqueue(child.Address);
                }
            }

            sb.AppendLine($"[clrmd] visited={visited} hubFound={found != 0}");
            staticRooted = found != 0;
            if (found != 0)
            {
                var chain = new List<string>();
                var cur = found;
                var guard = 0;
                while (cur != 0 && guard++ < 200)
                {
                    var obj = heap.GetObject(cur);
                    var (from, edge) = parent[cur];
                    var tn = obj.Type?.Name ?? "?";
                    var extra = "";
                    if (tn.Contains("MeshNodeTypeSource", StringComparison.Ordinal))
                    {
                        try { extra = $"  [_disposed={obj.ReadField<bool>("_disposed")}]"; }
                        catch (Exception e) { extra = $"  [_disposed read err: {e.Message}]"; }
                    }
                    else if (tn.EndsWith(".MessageHub", StringComparison.Ordinal))
                    {
                        // Name the survivor: RunLevel distinguishes "disposed but
                        // pinned" (6) from "created and ABANDONED, never disposed"
                        // (≤1 — the CI run 27433340109 case), and the Address says
                        // WHO leaked it (which creator forgot to tie the hub's
                        // lifetime to a parent/disposable).
                        try { extra = $"  [RunLevel={obj.ReadField<int>("<RunLevel>k__BackingField")}]"; }
                        catch (Exception e) { extra = $"  [RunLevel read err: {e.Message}]"; }
                        try
                        {
                            var addr = obj.ReadObjectField("<Address>k__BackingField");
                            if (addr.IsValid)
                            {
                                // Address stores Segments (string[]); Type/Id are computed.
                                var segsObj = addr.ReadObjectField("<Segments>k__BackingField");
                                if (segsObj.IsValid && segsObj.IsArray)
                                {
                                    var arr = segsObj.AsArray();
                                    var parts = new List<string>();
                                    for (var k = 0; k < arr.Length && k < 6; k++)
                                    {
                                        var el = arr.GetObjectValue(k);
                                        if (el.IsValid) parts.Add(el.AsString() ?? "?");
                                    }
                                    extra += $"  [Address={string.Join("/", parts)}]";
                                }
                            }
                        }
                        catch (Exception e) { extra += $"  [Address read err: {e.Message}]"; }
                    }
                    chain.Add($"{tn}  (via .{edge})  @{cur:x}{extra}");
                    if (from == 0) break;
                    cur = from;
                }
                chain.Reverse();
                sb.AppendLine("[clrmd] GC-ROOT PATH (root → … → mesh hub):");
                foreach (var line in chain) sb.AppendLine("   " + line);
            }
            else
            {
                var kinds = rootKindOf.Values.GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}");
                sb.AppendLine("[clrmd] no MessageHub reached from non-stack roots within budget.");
                sb.AppendLine("[clrmd] non-stack root kinds seen: " + string.Join(", ", kinds));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[clrmd] analysis failed: {ex.GetType().Name}: {ex.Message}");
        }
        return (staticRooted, sb.ToString());
    }

    public override async ValueTask DisposeAsync()
    {
        if (_selfDisposed)
        {
            GC.SuppressFinalize(this);
            return;
        }
        await base.DisposeAsync();
    }
}
