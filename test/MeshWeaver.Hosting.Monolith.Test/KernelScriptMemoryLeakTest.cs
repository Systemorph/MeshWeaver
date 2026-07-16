using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Memory-leak guard + GC-root analyzer for the Roslyn script-execution path.
///
/// <para>CI memory-delta logs show every kernel-script-executing test class
/// (ScriptExecutionInUserHomeTest ~190 MiB/test, MonolithKernelTest ~100 MiB/test,
/// InteractiveMarkdownExecutionTest, ActivityLogStreamTest) retaining large
/// anonymous-native memory AFTER mesh + ServiceProvider disposal + forced GC,
/// with only a small managed delta. That signature = a small managed pin holding
/// the script's <c>ScriptState</c> → <c>Compilation</c> → the ~300
/// <c>AssemblyMetadata</c> native metadata blocks built by
/// <c>KernelExecutor.EnsureInitialized</c> (one per loaded assembly).</para>
///
/// <para>This probe runs one kernel script the same way MonolithKernelTest does,
/// disposes the mesh AND its ServiceProvider, forces GC, and then BFS-walks the
/// live heap from non-stack GC roots with ClrMD. If a
/// <c>ScriptState</c>/<c>CSharpCompilation</c>/<c>AssemblyMetadata</c> is still
/// reachable, the printed chain names the pin.</para>
/// </summary>
public class KernelScriptMemoryLeakTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    /// <summary>Client needs layout/data so GetWorkspace().GetMeshNodeStream(...) works.</summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private bool _selfDisposed;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task RunOneKernelScript()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var activityNode = new MeshNode($"leakprobe-{kernelId}", activityNamespace)
        {
            Name = "Script leak probe",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("KernelExecution") { Status = ActivityStatus.Running }
        };
        await meshService.CreateNode(activityNode).Should().Within(60.Seconds()).Emit();
        var kernelAddress = new Address($"{activityNamespace}/leakprobe-{kernelId}");

        var client = GetClient();
        var logStream = client.GetWorkspace()
            .GetMeshNodeStream(kernelAddress.Path)
            .Select(change => change?.Content as ActivityLog)
            .Where(log => log is not null && log!.Messages.Any(m => m.Message.Contains("leak-probe-done")));

        client.Post(
            new SubmitCodeRequest("Console.WriteLine(\"leak-probe-done\");"),
            o => o.WithTarget(kernelAddress));

        await logStream.Should().Within(60.Seconds()).Emit();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollect()
    {
        for (var i = 0; i < 12; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ScriptState_IsCollected_AfterMeshAndServiceProviderDisposal()
    {
        await RunOneKernelScript();

        var hub = Mesh;
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

        ForceCollect();

        // CI trace shows alc count +1 per kernel-script test, never reclaimed —
        // name every surviving context (Roslyn's script LoadContext is the suspect).
        foreach (var alc in System.Runtime.Loader.AssemblyLoadContext.All)
        {
            Output.WriteLine(
                $"[alc] {alc.GetType().FullName} name={alc.Name} collectible={alc.IsCollectible} " +
                $"assemblies=[{string.Join(", ", alc.Assemblies.Select(a => a.GetName().Name).Take(5))}{(alc.Assemblies.Count() > 5 ? ", …" : "")}]");
        }

        var (pinned, report) = AnalyzeScriptRoots();
        Output.WriteLine(report);

        pinned.Should().BeFalse(
            "no per-session Roslyn script object (ScriptState / submission CSharpCompilation) may stay " +
            "reachable from a non-stack GC root after the mesh and its ServiceProvider are disposed — a " +
            "pinned session graph retains its compilation and submission state (~tens of MiB/run); the " +
            "chain above names the pin. (AssemblyMetadata reachable via the process-shared " +
            "KernelScriptReferences memo is by design and not failed on.)");
    }

    /// <summary>
    /// BFS from non-stack GC roots to the first instance of each Roslyn script
    /// object kind, printing root kind + type chain per surviving kind.
    /// </summary>
    private static (bool Pinned, string Report) AnalyzeScriptRoots()
    {
        var sb = new StringBuilder();
        var pinned = false;
        try
        {
            // 🚨 Pin the DAC for process lifetime BEFORE ClrMD loads it: DataTarget.Dispose
            // otherwise dlcloses libmscordaccore.so while its PAL's process-global pthread-key
            // destructor still points into it → any later thread exit SIGSEGVs the host
            // (the endemic exit=139). See ClrMdDacPin / ClrMdDacUnloadCrashTest.
            ClrMdDacPin.EnsurePinned();
            using var dt = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            if (dt.ClrVersions.Length == 0) return (false, "[clrmd] no CLR runtime found in snapshot");
            using var runtime = dt.ClrVersions[0].CreateRuntime();
            var heap = runtime.Heap;

            var parent = new Dictionary<ulong, (ulong From, string Edge)>();
            var queue = new Queue<ulong>();

            foreach (var root in heap.EnumerateRoots())
            {
                var kind = root.RootKind.ToString();
                if (kind.Contains("Stack", StringComparison.OrdinalIgnoreCase) ||
                    kind.Contains("Local", StringComparison.OrdinalIgnoreCase))
                    continue;
                var addr = root.Object.Address;
                if (addr == 0 || parent.ContainsKey(addr)) continue;
                parent[addr] = (0UL, $"ROOT[{kind}] {root.Object.Type?.Name}");
                queue.Enqueue(addr);
            }

            // Kind → first surviving instance address.
            var found = new Dictionary<string, ulong>();
            static string? TargetKind(string typeName)
            {
                if (typeName.StartsWith("Microsoft.CodeAnalysis.Scripting.ScriptState", StringComparison.Ordinal))
                    return "ScriptState";
                if (typeName == "Microsoft.CodeAnalysis.CSharp.CSharpCompilation")
                    return "CSharpCompilation";
                if (typeName == "Microsoft.CodeAnalysis.AssemblyMetadata")
                    return "AssemblyMetadata";
                return null;
            }

            var visited = 0;
            const int maxVisit = 8_000_000;
            var metadataCount = 0;
            // Live-set dominator table: type → (count, bytes) over everything
            // reachable from non-stack roots. Names whatever actually retains
            // the ~200 MiB/test the CI + local memory-delta logs show surviving
            // dispose + GC.
            var byType = new Dictionary<string, (long Count, long Bytes)>();
            var firstOfType = new Dictionary<string, ulong>();
            while (queue.Count > 0 && visited < maxVisit)
            {
                var addr = queue.Dequeue();
                visited++;
                var obj = heap.GetObject(addr);
                if (!obj.IsValid || obj.Type is null) continue;
                var typeName = obj.Type.Name ?? "?";
                var entry = byType.TryGetValue(typeName, out var e) ? e : (0L, 0L);
                byType[typeName] = (entry.Item1 + 1, entry.Item2 + (long)obj.Size);
                firstOfType.TryAdd(typeName, addr);
                var kind = TargetKind(typeName);
                if (kind is not null)
                {
                    if (kind == "AssemblyMetadata") metadataCount++;
                    found.TryAdd(kind, addr);
                    // keep walking — we want counts + every distinct kind
                }
                foreach (var child in obj.EnumerateReferences(false, true))
                {
                    if (child.Address == 0 || parent.ContainsKey(child.Address)) continue;
                    parent[child.Address] = (addr, child.Type?.Name ?? "?");
                    queue.Enqueue(child.Address);
                }
            }

            sb.AppendLine($"[clrmd] visited={visited} survivors: " +
                          (found.Count == 0
                              ? "none"
                              : string.Join(", ", found.Keys) + $" (AssemblyMetadata×{metadataCount})"));
            // AssemblyMetadata reachable via the process-shared KernelScriptReferences
            // memo is BY DESIGN (one materialization per assembly per process — the fix
            // for the ~200 MiB-per-session leak). The leak signature this probe guards
            // is the SESSION graph surviving: ScriptState / submission compilation.
            pinned = found.ContainsKey("ScriptState") || found.ContainsKey("CSharpCompilation");

            var liveBytes = byType.Values.Sum(v => v.Bytes);
            sb.AppendLine($"[clrmd] live-from-non-stack-roots total={liveBytes / 1024 / 1024}MiB; top types by retained size:");
            foreach (var (typeName, agg) in byType.OrderByDescending(kv => kv.Value.Bytes).Take(30))
                sb.AppendLine($"   {agg.Bytes / 1024,10}KiB  ×{agg.Count,-7} {typeName}");

            var gcInfo = GC.GetGCMemoryInfo();
            sb.AppendLine($"[gc] heapSize={gcInfo.HeapSizeBytes / 1024 / 1024}MiB committed={gcInfo.TotalCommittedBytes / 1024 / 1024}MiB " +
                          $"workingSet={Environment.WorkingSet / 1024 / 1024}MiB");

            // Root chains for the heaviest non-framework-noise types — names WHO
            // holds the retained bytes, not just what they are.
            var chainTargets = byType
                .OrderByDescending(kv => kv.Value.Bytes)
                .Select(kv => kv.Key)
                .Where(t => t is not ("System.String" or "System.Object[]" or "System.Byte[]" or "System.Char[]"))
                .Take(5);
            foreach (var typeName in chainTargets)
            {
                if (!firstOfType.TryGetValue(typeName, out var addr)) continue;
                sb.AppendLine($"[clrmd] GC-ROOT PATH (root → … → {typeName}):");
                var cur = addr;
                var chain = new List<string>();
                var guard = 0;
                while (cur != 0 && guard++ < 200)
                {
                    var obj = heap.GetObject(cur);
                    var (from, edge) = parent[cur];
                    chain.Add($"{obj.Type?.Name ?? "?"}  (via .{edge})  @{cur:x}");
                    if (from == 0) break;
                    cur = from;
                }
                chain.Reverse();
                foreach (var line in chain) sb.AppendLine("   " + line);
            }

            foreach (var (kind, addr) in found)
            {
                sb.AppendLine($"[clrmd] GC-ROOT PATH (root → … → {kind}):");
                var cur = addr;
                var chain = new List<string>();
                var guard = 0;
                while (cur != 0 && guard++ < 200)
                {
                    var obj = heap.GetObject(cur);
                    var (from, edge) = parent[cur];
                    chain.Add($"{obj.Type?.Name ?? "?"}  (via .{edge})  @{cur:x}");
                    if (from == 0) break;
                    cur = from;
                }
                chain.Reverse();
                foreach (var line in chain) sb.AppendLine("   " + line);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[clrmd] analysis failed: {ex.GetType().Name}: {ex.Message}");
        }
        return (pinned, sb.ToString());
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
