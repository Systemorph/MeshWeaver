using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Static helpers for NodeType compilation, owned by the per-NodeType hub
/// (the actor that "is" the NodeType). The hub is at <c>Address(nodeTypePath)</c>;
/// its own <see cref="MeshNode"/> carries every property the compile needs
/// (<c>NodeTypeDefinition.CompilationStatus</c>, <c>CompilationError</c>,
/// <c>AssemblyLocation</c>, …) and the result of every compile is written
/// back to that same MeshNode. The NodeType is its own boss
/// (see <c>Doc/Architecture/SyncedMeshNodeQueries.md</c> +
/// <c>feedback_dirty_flag_on_owner</c>).
///
/// <para>This file exists so the auto-watcher and the on-demand
/// <c>CreateReleaseRequest</c> handler share one body (<see cref="RunCompile"/>)
/// and so the soon-to-be-deleted <c>NodeTypeService</c> stops being the home
/// of compilation logic.</para>
///
/// <para>Reactive end-to-end — no <c>await</c>, no <c>.ToTask()</c> at this
/// layer; the only Task is buried inside
/// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>
/// which wraps the Roslyn invocation as <c>Observable.FromAsync</c>.</para>
/// </summary>
internal static class NodeTypeCompilationHelpers
{
    /// <summary>
    /// Subscribes to the per-NodeType hub's own MeshNode stream and auto-fires
    /// <see cref="RunCompile"/> whenever <see cref="NodeTypeDefinition.CompilationStatus"/>
    /// flips to <see cref="CompilationStatus.Pending"/>. Wired from the per-NodeType
    /// hub's <c>WithInitialization</c> hook (<c>SubscribeToOwnDeletion</c>) so the
    /// watcher's lifetime matches the hub's.
    ///
    /// <para>Trigger model: callers that previously called
    /// <c>NodeTypeService.InvalidateCache(path)</c> or <c>GetAssemblyPath(path)</c>
    /// (which lazily compiled) now write <c>CompilationStatus = Pending</c> to the
    /// NodeType MeshNode via <c>workspace.GetMeshNodeStream(path).Update(...)</c>.
    /// The watcher sees the flip and runs the compile; the result lands on the
    /// MeshNode and every subscriber sees it through synced-query fan-out.</para>
    /// </summary>
    public static IDisposable InstallCompileWatcher(
        IMessageHub hub,
        IWorkspace workspace,
        IMeshNodeCompilationService compilationService)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");

        // Re-entry guard: the first thing RunCompile does is flip status to
        // Compiling, which is itself an emission. Without this guard the
        // subscription would see its own Compiling-emission and try to fire
        // again. Cleared by the post-compile write-back to Ok/Error.
        var triggered = 0;

        // Eager kickoff on hub activation: when the per-NodeType hub starts and
        // its own NodeTypeDefinition is NOT backed by a usable compiled
        // assembly, flip CompilationStatus = Pending on its OWN stream so the
        // watcher below fires Roslyn immediately. This is a LOCAL UpdateOwn —
        // it lands on the hub's own MeshNode, which the watcher (same hub)
        // observes.
        //
        // 🚨 Verify-before-skip — the kickoff must NOT trust a bare
        // CompilationStatus == Ok. CompilationStatus + AssemblyLocation are
        // runtime state, but they are persisted into the NodeType's own
        // MeshNode JSON. A stale Ok therefore survives across process
        // boundaries: it can be baked into seed/sample data by a previous run
        // (the test-seed-pollution class of bug), or it can point at a temp /
        // .mesh-cache assembly that has since been cleaned up. Trusting it
        // strands the NodeType — the kickoff skips, no recompile runs, and
        // every instance hub falls back to the default config (no
        // MeshNodeReference reducer → "No reducer defined for
        // MeshNodeReference" on every subscribe). The ONLY safe skip condition
        // is "Ok AND the compiled assembly still exists on disk"
        // (<see cref="HasUsableBuild"/>); everything else — null / Unknown /
        // Compiling (interrupted) / Error / Ok-but-assembly-gone — recompiles.
        //
        // The AssemblyLocation check is sound precisely because it is gated on
        // status == Ok. A never-compiled NodeType has a null status: enrichment
        // stamps MeshNode.AssemblyLocation with the FRAMEWORK assembly hosting
        // the NodeType meta-type (e.g. MeshWeaver.Graph.dll), but it never sets
        // status to Ok — so a framework-dll AssemblyLocation can never falsely
        // satisfy HasUsableBuild.
        var ownStream = workspace.GetMeshNodeStream();
        var kicked = 0;
        var kickoffSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition)
            .Take(1)
            .Subscribe(
                node =>
                {
                    if (node?.Content is not NodeTypeDefinition def) return;
                    if (HasUsableBuild(node, def))
                    {
                        logger?.LogInformation(
                            "Compile kickoff: skip {HubPath} — Ok + compiled assembly present ({Assembly})",
                            hub.Address.Path, node.AssemblyLocation);
                        return;
                    }
                    if (System.Threading.Interlocked.CompareExchange(ref kicked, 1, 0) != 0) return;

                    logger?.LogInformation(
                        "Compile kickoff: flipping Pending for {HubPath} (status={Status}, assemblyPresent={Present})",
                        hub.Address.Path, def.CompilationStatus,
                        !string.IsNullOrEmpty(node.AssemblyLocation)
                            && System.IO.File.Exists(node.AssemblyLocation));
                    workspace.GetMeshNodeStream().Update(curr =>
                        curr.Content is NodeTypeDefinition d
                            && !HasUsableBuild(curr, d)
                            && d.CompilationStatus != CompilationStatus.Pending
                            && d.CompilationStatus != CompilationStatus.Compiling
                            ? curr with
                            {
                                Content = d with { CompilationStatus = CompilationStatus.Pending }
                            }
                            : curr)
                        .Subscribe(
                            _ => { },
                            ex => logger?.LogWarning(ex,
                                "Compile kickoff: failed to flip Pending for {HubPath}",
                                hub.Address.Path));
                },
                ex => logger?.LogWarning(ex,
                    "Compile kickoff: own-stream faulted for {HubPath}", hub.Address.Path));

        var hubPath = hub.Address.Path;
        var watcherSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.CompilationStatus == CompilationStatus.Pending)
            .Subscribe(
                pendingNode =>
                {
                    logger?.LogInformation("Compile watcher: saw Pending for {HubPath} — dispatching compile", hubPath);
                    // Single-flight guard: keep `triggered` set across the async
                    // activity dispatch so any additional Pending emissions
                    // (e.g. a follow-up UpdateNode that happens to carry the
                    // stale captured Pending status, or a remote-stream replay
                    // through a fresh subscriber) don't fire a SECOND activity
                    // that would race the first into the parent's terminal
                    // write. Resetting in `finally` (the previous behaviour)
                    // only guards against synchronous re-entry — the dispatch
                    // IS async (`NodeTypeCompilationActivity.Start` chains a
                    // `meshService.CreateNode` and a `hub.Post`), so the flag
                    // flipped right back to 0 before the activity even began
                    // compiling. With many Pending emissions on the stream,
                    // each fired a fresh activity, and each activity issued two
                    // <c>WriteToParent</c> <c>DataChangeRequest</c>s on the
                    // mesh hub (the leak fingerprint behind the
                    // CompilationPending_CreatesReleaseMeshNode_WithNotes test
                    // regression). The flag is now cleared by the trailing
                    // `settleSub` on the next non-Pending emission — that's
                    // the natural single-flight boundary.
                    if (System.Threading.Interlocked.CompareExchange(ref triggered, 1, 0) != 0)
                        return;

                    try
                    {
                        // Activity Control Plane: every long-running operation runs
                        // on an Activity hub (Doc/Architecture/ActivityControlPlane.md).
                        // Create the activity MeshNode and dispatch RunCompileRequest
                        // to its hub address. The activity OWNS the parent's compile
                        // state: it writes Compiling at start and Ok/Error +
                        // AssemblyLocation + CompiledSources at end. The watcher does
                        // NOT touch the parent MeshNode here — single-writer
                        // (the activity) avoids races.
                        var meshService = hub.ServiceProvider.GetService<IMeshService>();
                        if (meshService is null)
                        {
                            // Inline fallback only when no IMeshService can create
                            // the activity (early bootstrap / minimal test fixture).
                            // RunCompile writes Compiling itself, no risk of races
                            // because no activity exists.
                            logger?.LogDebug("Compile watcher: activity unavailable for {HubPath}, running inline", hubPath);
                            RunCompile(workspace, hub, compilationService, pendingNode!, request: null);
                            return;
                        }

                        // Start returns the activity path ONLY after the activity
                        // node's CreateNode completes — so the RunCompileRequest
                        // never races a not-yet-routable activity.
                        NodeTypeCompilationActivity.Start(hub, hubPath, logger!)
                            .Subscribe(
                                activityPath => hub.Post(new RunCompileRequest(hubPath),
                                    o => o.WithTarget(new Address(activityPath))),
                                ex =>
                                {
                                    logger?.LogWarning(ex,
                                        "Compile watcher: activity start faulted for {HubPath}", hubPath);
                                    // Failed to dispatch — drop the guard so a
                                    // subsequent Pending flip can retry.
                                    System.Threading.Interlocked.Exchange(ref triggered, 0);
                                });
                    }
                    catch
                    {
                        // Synchronous failure — drop the guard so a subsequent
                        // Pending flip can retry. The async failure path is
                        // handled in the `Start` subscription above.
                        System.Threading.Interlocked.Exchange(ref triggered, 0);
                        throw;
                    }
                },
                ex => logger?.LogWarning(ex,
                    "Compile watcher faulted for {HubPath}", hub.Address.Path));

        // Trailing watcher: clears `triggered` once the parent settles into a
        // non-Pending state (Compiling / Ok / Error / Unknown). That's the
        // natural single-flight boundary — Compiling means "the compile is
        // mine," Ok/Error means "the compile is done." A FRESH Pending arriving
        // after that transition is the legitimate "user kicked off another
        // compile" signal and SHOULD fire the watcher.
        var settleSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.CompilationStatus != CompilationStatus.Pending)
            .Subscribe(_ => System.Threading.Interlocked.Exchange(ref triggered, 0));

        return new CompositeDisposable(kickoffSub, watcherSub, settleSub);
    }

    /// <summary>
    /// The live MeshWeaver framework version — the identity a compiled NodeType
    /// release is pinned to. Two regimes, picked automatically:
    /// <list type="bullet">
    ///   <item><b>Deployed builds</b> — the NuGet pack process stamps a real
    ///     semver into <c>AssemblyInformationalVersion</c> (e.g.
    ///     <c>"3.0.0-preview2"</c>). That value is identical on every server
    ///     running the same deployed build, so the version alone is the
    ///     framework identity — a redeploy at a new version invalidates every
    ///     release; a file write-time (which differs per machine) would not.</item>
    ///   <item><b>Un-packed dev builds</b> — the version stays the frozen
    ///     default (<c>"1.0.0"</c>) across every <c>dotnet build</c>, so a
    ///     version-only check would never recompile a NodeType after the
    ///     framework is rebuilt locally. There we append the
    ///     <c>MeshWeaver.Graph</c> assembly's last-write time: on the single dev
    ///     machine it is "frozen" per build (stable within a run, changes on
    ///     rebuild) — exactly the dev-iteration signal we want.</item>
    /// </list>
    /// Computed once per process.
    /// </summary>
    internal static string FrameworkVersion => _frameworkVersion.Value;

    private static readonly Lazy<string> _frameworkVersion = new(() =>
    {
        var asm = typeof(NodeTypeCompilationHelpers).Assembly;
        var info = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        // "{semver}+{gitSha}" → keep only the semver part.
        string semver;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            semver = plus >= 0 ? info[..plus] : info;
        }
        else
        {
            semver = asm.GetName().Version?.ToString() ?? "0.0.0";
        }

        // Un-packed dev default — the version never moves across local
        // rebuilds, so fold in the assembly file's last-write time to keep
        // dev iteration honest. Deployed builds carry a real version and
        // skip this (stable across servers).
        if (semver is "1.0.0" or "1.0.0.0" or "0.0.0")
        {
            var loc = asm.Location;
            if (!string.IsNullOrEmpty(loc) && System.IO.File.Exists(loc))
                return $"{semver}+{System.IO.File.GetLastWriteTimeUtc(loc):O}";
        }
        return semver;
    });

    /// <summary>
    /// True when a NodeType's persisted compile state is backed by a compiled
    /// assembly that (a) still exists on disk and (b) was compiled against the
    /// CURRENT MeshWeaver framework version — the ONLY condition under which the
    /// compile kickoff may safely skip a (re)compile.
    ///
    /// <para><c>CompilationStatus</c>, <c>MeshNode.AssemblyLocation</c> and
    /// <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> are runtime
    /// state, but they are persisted into the NodeType MeshNode's JSON. A stale
    /// <c>Ok</c> therefore outlives the process — and the temp /
    /// <c>.mesh-cache</c> assembly — that produced it (seed-data pollution,
    /// cleaned-up caches, cross-machine checkouts, <b>and a redeployed
    /// framework</b>). The two checks below make a cold hub start self-healing
    /// instead of trusting a pointer into the void:</para>
    /// <list type="number">
    ///   <item><b>Assembly present</b> — the compiled DLL still exists on disk.</item>
    ///   <item><b>Framework match</b> — it was compiled against the current
    ///     <see cref="FrameworkVersion"/>. A MeshWeaver redeploy at a new version
    ///     changes the framework assemblies the cached DLL bound against, so a
    ///     mismatch forces a recompile (which mints a new release and leaves the
    ///     old one as history for instances still loaded on it).</item>
    /// </list>
    ///
    /// <para>Gating on <c>status == Ok</c> first is what makes the
    /// <c>AssemblyLocation</c> check sound: a never-compiled NodeType has a
    /// null status (enrichment stamps the FRAMEWORK assembly on
    /// <c>AssemblyLocation</c> but never sets <c>Ok</c>), so a framework-dll
    /// path can never falsely satisfy this predicate.</para>
    /// </summary>
    internal static bool HasUsableBuild(MeshNode node, NodeTypeDefinition def) =>
        def.CompilationStatus == CompilationStatus.Ok
        && !string.IsNullOrEmpty(node.AssemblyLocation)
        && System.IO.File.Exists(node.AssemblyLocation)
        && string.Equals(def.CompiledFrameworkVersion, FrameworkVersion, StringComparison.Ordinal);

    /// <summary>
    /// Compile-and-write-back loop for one NodeType. Runs Roslyn via
    /// <see cref="IMeshNodeCompilationService.CompileAndGetConfigurations"/>,
    /// writes the outcome back to the NodeType's own MeshNode
    /// (<see cref="NodeTypeDefinition.CompilationStatus"/>,
    /// <see cref="NodeTypeDefinition.CompilationError"/>,
    /// <see cref="MeshNode.AssemblyLocation"/>,
    /// <see cref="NodeTypeDefinition.LastCompileSucceededAt"/>,
    /// <see cref="NodeTypeDefinition.LatestReleasePath"/>,
    /// <see cref="NodeTypeDefinition.CompiledSources"/>), and (best-effort)
    /// publishes the post-compile MeshNode onto the mesh change feed so other
    /// silos invalidate their caches.
    ///
    /// <para>Shared by two callers:
    /// <list type="number">
    ///   <item><see cref="InstallCompileWatcher"/> auto-triggers on
    ///     <see cref="CompilationStatus.Pending"/> — passes <paramref name="request"/> = null.</item>
    ///   <item>The <c>CreateReleaseRequest</c> handler in <c>MeshDataSource</c>
    ///     responds to a UI "Create Release" click — passes the delivery so
    ///     <c>CreateReleaseResponse</c> can be returned to the requester.</item>
    /// </list></para>
    /// </summary>
    public static void RunCompile(
        IWorkspace workspace,
        IMessageHub hub,
        IMeshNodeCompilationService compilationService,
        MeshNode pendingNode,
        IMessageDelivery<CreateReleaseRequest>? request,
        IReadOnlyList<MeshNode>? sourcesOverride = null)
    {
        var hubPath = hub.Address.Path;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.CompileWatcher");

        workspace.GetMeshNodeStream().Update(curr =>
            curr.Content is NodeTypeDefinition def
                ? curr with
                {
                    Content = def with
                    {
                        CompilationStatus = CompilationStatus.Compiling,
                        LastCompileStartedAt = DateTimeOffset.UtcNow
                    }
                }
                : curr)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "Compile: failed to flip status to Compiling for {HubPath}", hubPath));

        if (request is not null)
            hub.Post(new CreateReleaseResponse(true), o => o.ResponseFor(request));

        var sub = compilationService.CompileAndGetConfigurations(pendingNode, sourcesOverride)
            .Take(1)
            .Select(result => new CompileOutcome(result, null, pendingNode))
            .Catch<CompileOutcome, Exception>(ex =>
                Observable.Return(new CompileOutcome(null, ex, pendingNode)))
            .Subscribe(
                outcome =>
                {
                    var activityPath = outcome.Result?.Log is { } compileLog
                        ? $"{hubPath}/_activity/{compileLog.Id}"
                        : null;

                    string? newReleasePath = null;
                    if (outcome.Error is null && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation))
                    {
                        newReleasePath = MeshDataSourceExtensions.TryCreateReleaseNode(
                            hub, hubPath, outcome.Result!, outcome.PendingNode, activityPath, logger);
                    }

                    workspace.GetMeshNodeStream().Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def)
                            return curr;

                        if (outcome.Error is null && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation))
                        {
                            logger?.LogInformation("Compile success for {HubPath} → {Assembly}",
                                hubPath, outcome.Result!.AssemblyLocation);
                            return curr with
                            {
                                Content = def with
                                {
                                    CompilationStatus = CompilationStatus.Ok,
                                    CompilationError = null,
                                    LastCompileSucceededAt = DateTimeOffset.UtcNow,
                                    LastCompiledVersion = curr.Version,
                                    LastCompilationActivityPath = activityPath,
                                    LatestReleasePath = newReleasePath ?? def.LatestReleasePath,
                                    ReleaseNotes = newReleasePath is not null ? null : def.ReleaseNotes,
                                    CompiledSources = outcome.Result.CompiledSources
                                        ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty,
                                    // Stamp the framework version the assembly bound
                                    // against — HasUsableBuild compares this to the live
                                    // FrameworkVersion so a MeshWeaver redeploy forces a
                                    // recompile instead of loading an ABI-stale DLL.
                                    CompiledFrameworkVersion = FrameworkVersion
                                },
                                AssemblyLocation = outcome.Result.AssemblyLocation
                            };
                        }

                        var errorSummary = outcome.Error?.Message
                            ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                ? string.Join("; ", errs.Select(m => m.Message))
                                : "Compilation produced no assembly");
                        logger?.LogWarning("Compile failure for {HubPath}: {Error}", hubPath, errorSummary);
                        return curr with
                        {
                            Content = def with
                            {
                                CompilationStatus = CompilationStatus.Error,
                                CompilationError = errorSummary,
                                LastCompilationActivityPath = activityPath,
                                CompiledSources = null
                            }
                        };
                    })
                    .Subscribe(
                        saved =>
                        {
                            // Publish the post-compile MeshNode update onto the
                            // mesh change feed for cross-silo cache invalidation.
                            try
                            {
                                hub.ServiceProvider.GetService<IMeshChangeFeed>()
                                    ?.Publish(MeshChangeEvent.Updated(saved));
                            }
                            catch (Exception publishEx)
                            {
                                logger?.LogWarning(publishEx,
                                    "Compile: failed to publish post-compile change-feed event for {HubPath}",
                                    hubPath);
                            }
                        },
                        ex => logger?.LogWarning(ex,
                            "Compile: failed to write post-compile status for {HubPath}", hubPath));
                },
                ex => logger?.LogWarning(ex, "Compile faulted for {HubPath}", hubPath));

        hub.RegisterForDisposal(sub);
    }

    /// <summary>Per-NodeType compile outcome — either the compiler's result or the exception that aborted it.</summary>
    private record CompileOutcome(NodeCompilationResult? Result, Exception? Error, MeshNode PendingNode);
}
