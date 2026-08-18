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
///
/// <para>🚨 <b>A PASS HERE PINS NOTHING — this is a sampling probe, not a regression
/// test.</b> Do not add a leak fix and treat a green run as proof, and do not read a
/// green run as "no leak":</para>
/// <list type="bullet">
///   <item><description><b>It samples.</b> A root that is live only for a bounded window
///     (#991: an uncancelled 1 s <c>Observable.Timer</c> on the process-wide
///     <c>TimerQueue</c>) is caught only if the probe's forced GC lands inside that window.
///     Fire first → collected → green, with the defect fully present.</description></item>
///   <item><description><b>It cannot attribute.</b> It reports the FIRST
///     <c>MessageHub</c> reachable from ANY non-stack root. A green run says "no hub was
///     reached within the visit budget", not "root X is fixed"; a red run names whatever
///     chain it happened to walk, which may be a different defect than the one you are
///     chasing.</description></item>
///   <item><description><b>It is inconclusive off Linux.</b> ClrMD snapshot-attach throws
///     on macOS, so a surviving hub SKIPs (#674) — locally you learn nothing either
///     way.</description></item>
/// </list>
/// <para>So: pin the specific root with a deterministic test next to the code that owns it,
/// and prove it by reverting the fix and watching that test fail. #991's pin lives in
/// <c>MeshWeaver.Hosting.Test.ActivityControlPlaneResubscribeTest</c>. This probe's job is
/// DISCOVERY — naming a root nobody knew about — not verification.</para>
/// </summary>
public class MeshHubDisposalLeakTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private bool _selfDisposed;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ExerciseAndWeakRefMeshHub()
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
        await factory.CreateNode(node).Should().Within(60.Seconds()).Emit();
        await factory.UpdateNode(node with { Name = "probe-updated" }).Should().Within(60.Seconds()).Emit();

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

    /// <summary>
    /// The equality the ClrMD matcher rests on: an address's <c>Path</c> IS its segments joined by
    /// "/", which is the only thing readable from a heap snapshot.
    ///
    /// <para>Runs on every platform — unlike the leak test itself, which needs ClrMD/DAC and skips
    /// on macOS. If this ever diverges, the matcher stops matching and the leak gate quietly
    /// degrades into a check that always passes; this fails first and says why.</para>
    /// </summary>
    [Fact]
    public void AddressPath_IsTheJoinedSegments()
    {
        var address = Mesh.Address;
        Assert.Equal(string.Join("/", address.Segments), address.Path);
    }

    [Fact]
    public async Task MeshHub_IsCollected_AfterMeshAndServiceProviderDisposal()
    {
        var weak = await ExerciseAndWeakRefMeshHub();
        weak.IsAlive.Should().BeTrue("the mesh hub is held by the live ServiceProvider before disposal");

        var hub = Mesh;
        // The identity of the hub UNDER TEST, captured as a plain string so it retains nothing.
        // Without it the analysis below cannot tell our hub from any other live hub in the process.
        //
        // 🚨 Address.PATH, not ToString(): ToString() appends "~host" when a Host is set, while the
        // snapshot side can only join the raw Segments. The two would then never be equal, the
        // matcher would never match, and the gate would report "no leak" for every run — a check
        // that cannot fail. AddressPath_IsTheJoinedSegments pins that equality.
        var hubUnderTest = hub.Address.Path;
        hub.Dispose();
        await hub.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask();

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
        var (outcome, report) = AnalyzeMeshHubRoots(hubUnderTest);
        Output.WriteLine("=== MESH HUB SURVIVED DISPOSAL — ClrMD GC-root analysis ===");
        Output.WriteLine(report);

        // A guard that could not LOOK must not report "no leak" (#674): on macOS the
        // snapshot-attach throws PlatformNotSupportedException, and folding that into
        // false let this assertion pass while the hub was demonstrably alive.
        // Inconclusive is a SKIP, never a pass — Linux (CI) runs the real analysis.
        Assert.SkipWhen(outcome == ClrMdRootAnalysisOutcome.Unavailable,
            "the mesh hub SURVIVED disposal but the ClrMD root analysis could not run on this " +
            "platform/process — the verdict is inconclusive here, not green (#674); run on Linux " +
            "for the GC-root chain. " + report);

        (outcome == ClrMdRootAnalysisOutcome.Detected).Should().BeFalse(
            "the mesh hub is pinned by a NON-stack root (static field / TimerQueue timer / GC handle) " +
            "— a real leak that accumulates across disposed meshes; the chain above names it. A hub " +
            "held ONLY by a transient stack root (snapshot artifact) is acceptable and not failed on.");
    }

    /// <summary>
    /// Snapshot-attach ClrMD to THIS process and BFS from non-stack GC roots to the
    /// <c>MessageHub</c> whose address is <paramref name="hubUnderTest"/>, printing the root kind +
    /// the type chain from the root down to it. The top of the chain is the pin.
    ///
    /// <para>🚨 <b>It must be THAT hub, not any hub.</b> This walk used to stop at the first object
    /// whose type ended in <c>.MessageHub</c> and report it as the leak. A test process is full of
    /// perfectly healthy live hubs — other test classes' meshes, client hubs, per-node hubs — and
    /// every one of them is reachable from a non-stack root, because that is what being alive
    /// MEANS. So the assertion fired on whichever unrelated hub the breadth-first walk happened to
    /// reach first, which is why it failed on PRs that changed no compiled code at all
    /// (#1841: a two-file YAML diff; tracked in #1843), and why the printed chain was a ~25-long walk down the
    /// process-global TimerQueue linked list — the path to a stranger's hub, not to ours.</para>
    ///
    /// <para>Matching on the address keeps the gate able to FAIL: if hubs are found but none of
    /// their addresses can be read, that is <see cref="ClrMdRootAnalysisOutcome.Unavailable"/> —
    /// inconclusive — never a pass. A guard that cannot look must not report "no leak" (#674), and
    /// a matcher that can never match would be exactly that.</para>
    /// </summary>
    /// <param name="hubUnderTest">The disposed hub's address, e.g. <c>mesh/abc123</c>.</param>
    private static (ClrMdRootAnalysisOutcome Outcome, string Report) AnalyzeMeshHubRoots(string hubUnderTest)
    {
        var sb = new StringBuilder();
        try
        {
            // 🚨 Pin the DAC for process lifetime BEFORE ClrMD loads it: DataTarget.Dispose
            // otherwise dlcloses libmscordaccore.so while its PAL's process-global pthread-key
            // destructor still points into it → any later thread exit SIGSEGVs the host
            // (the endemic exit=139). See ClrMdDacPin / ClrMdDacUnloadCrashTest.
            ClrMdDacPin.EnsurePinned();
            using var dt = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            if (dt.ClrVersions.Length == 0)
                return (ClrMdRootAnalysisOutcome.Unavailable, "[clrmd] no CLR runtime found in snapshot");
            using var runtime = dt.ClrVersions[0].CreateRuntime();
            var heap = runtime.Heap;

            var hubsSeen = 0;
            var addressesRead = 0;
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
                    hubsSeen++;
                    var hubAddress = TryReadHubAddress(obj);
                    if (hubAddress is not null)
                    {
                        addressesRead++;
                        if (string.Equals(hubAddress, hubUnderTest, StringComparison.Ordinal))
                        {
                            found = addr;
                            break;
                        }
                    }
                    // A DIFFERENT hub — almost certainly a live one, which is reachable by design.
                    // Walk on rather than stopping: stopping here is what made this a coin flip.
                }
                foreach (var child in obj.EnumerateReferences(false, true))
                {
                    if (child.Address == 0 || parent.ContainsKey(child.Address)) continue;
                    parent[child.Address] = (addr, child.Type?.Name ?? "?");
                    queue.Enqueue(child.Address);
                }
            }

            sb.AppendLine($"[clrmd] visited={visited} hubFound={found != 0}");
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
                            // MessageHub.Address => Configuration.Address — the hub has no
                            // address field of its own; read through the config object.
                            var config = obj.ReadObjectField("<Configuration>k__BackingField");
                            var addr = config.IsValid
                                ? config.ReadObjectField("<Address>k__BackingField")
                                : default;
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
                sb.AppendLine($"[clrmd] hub under test ({hubUnderTest}) NOT reached from non-stack "
                              + $"roots within budget; {hubsSeen} other MessageHub(s) seen and walked past.");
                sb.AppendLine("[clrmd] non-stack root kinds seen: " + string.Join(", ", kinds));
            }

            // The matcher must be demonstrably capable of matching. If hubs were on the heap but not
            // one address could be read, we did not actually test anything — report inconclusive
            // rather than a green that means nothing (#674).
            if (found == 0 && hubsSeen > 0 && addressesRead == 0)
            {
                sb.AppendLine($"[clrmd] {hubsSeen} MessageHub(s) found but NO address could be read — "
                              + "the address matcher could not have matched, so this run proves nothing.");
                return (ClrMdRootAnalysisOutcome.Unavailable, sb.ToString());
            }

            return (found != 0 ? ClrMdRootAnalysisOutcome.Detected : ClrMdRootAnalysisOutcome.NotDetected,
                sb.ToString());
        }
        catch (Exception ex)
        {
            // Snapshot-attach unsupported (macOS PlatformNotSupportedException) or any other
            // analysis fault: the probe DID NOT LOOK — that is Unavailable, never NotDetected.
            sb.AppendLine($"[clrmd] analysis failed: {ex.GetType().Name}: {ex.Message}");
            return (ClrMdRootAnalysisOutcome.Unavailable, sb.ToString());
        }
    }

    /// <summary>
    /// The <c>Address</c> of a <c>MessageHub</c> on the snapshot heap, as
    /// <c>string.Join("/", Segments)</c> — exactly what <c>Address.Path</c> produces, so it compares
    /// directly against a captured live <c>Path</c>. Deliberately NOT <c>ToString()</c>, which
    /// appends <c>"~host"</c> when a Host is set and therefore does not equal this.
    ///
    /// <para>Returns null when the shape cannot be read, and NEVER throws: an unreadable stranger
    /// must not abort a walk that is looking for a different hub. 🚨 Null must also be returned for
    /// any address it cannot reproduce EXACTLY — a partially-read address would compare unequal to
    /// the real one and silently turn this gate into one that always passes.</para>
    /// </summary>
    private static string? TryReadHubAddress(ClrObject hub)
    {
        try
        {
            // MessageHub has no address field of its own — it reads through Configuration.
            var config = hub.ReadObjectField("<Configuration>k__BackingField");
            if (!config.IsValid) return null;
            var address = config.ReadObjectField("<Address>k__BackingField");
            if (!address.IsValid) return null;
            var segments = address.ReadObjectField("<Segments>k__BackingField");
            if (!segments.IsValid || !segments.IsArray) return null;
            var arr = segments.AsArray();
            var parts = new List<string>();
            // EVERY segment, and no substitutes. A cap here (there was one, at 8) truncates longer
            // addresses so they can never equal the captured Path, and an unreadable segment
            // replaced by "" corrupts the join the same way — both make the comparison fail
            // silently, which reports "no leak" forever. Unreadable => null => Unavailable.
            for (var i = 0; i < arr.Length; i++)
            {
                var element = arr.GetObjectValue(i);
                if (!element.IsValid) return null;
                if (element.AsString() is not { } segment) return null;
                parts.Add(segment);
            }
            return parts.Count == 0 ? null : string.Join("/", parts);
        }
        catch
        {
            return null;
        }
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
