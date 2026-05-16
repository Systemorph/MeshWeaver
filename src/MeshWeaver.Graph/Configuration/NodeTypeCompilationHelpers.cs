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

        // No in-memory single-flight flag. CompilationStatus on the
        // NodeTypeDefinition IS the lock: the watcher atomically transitions
        // Pending → Compiling inside the Update lambda and dispatches the
        // activity only when WE were the one that made the transition. Every
        // Pending-flipper (the kickoff below, the CreateReleaseRequest handler
        // in MeshDataSource.DispatchPendingFlip) is status-guarded so two
        // independent requests can't both result in Pending while a compile is
        // already requested or running.

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
                    // Truly-static NodeTypes (registered via IStaticNodeProvider
                    // with their HubConfiguration delegate set directly) carry a
                    // NodeTypeDefinition for metadata but have NO source code to
                    // compile — the framework ships their assembly. Skip kickoff
                    // only when the in-memory HubConfiguration delegate is set
                    // AND the definition has no source code (Configuration /
                    // HubConfiguration / Sources all empty). A dynamic NodeType
                    // whose source string compiled into a delegate at registration
                    // STILL needs a real assembly emitted to the IAssemblyStore so
                    // cross-silo activation can resolve it — only the local hub
                    // has the delegate; remote silos see HubConfiguration=null
                    // after serialisation and must reflect on the stored DLL.
                    var hasSource =
                        !string.IsNullOrWhiteSpace(def.Configuration)
                        || !string.IsNullOrWhiteSpace(def.HubConfiguration)
                        || (def.Sources is { Count: > 0 });
                    if (node.HubConfiguration is not null && !hasSource)
                    {
                        logger?.LogInformation(
                            "Compile kickoff: skip {HubPath} — static NodeType (HubConfiguration set, no source)",
                            hub.Address.Path);
                        return;
                    }
                    if (HasUsableBuild(node, def))
                    {
                        logger?.LogInformation(
                            "Compile kickoff: skip {HubPath} — Ok + compiled assembly present ({Collection}/{Path})",
                            hub.Address.Path, def.LatestAssemblyCollection, def.LatestAssemblyPath);
                        return;
                    }
                    if (System.Threading.Interlocked.CompareExchange(ref kicked, 1, 0) != 0) return;

                    logger?.LogInformation(
                        "Compile kickoff: flipping Pending for {HubPath} (status={Status}, assemblyPresent={Present})",
                        hub.Address.Path, def.CompilationStatus,
                        !string.IsNullOrEmpty(def.LatestAssemblyPath));
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
                && def.CompilationStatus == CompilationStatus.Pending
                // Truly-static NodeTypes (HubConfiguration delegate set AND no
                // source code) ship their assembly with the framework — even if
                // something flips them Pending, there's nothing to compile.
                // Symmetric with the kickoff: a dynamic NodeType whose source
                // string compiled into a delegate at registration still needs a
                // real assembly emit, so allow Pending through when source exists.
                && !(node.HubConfiguration is not null
                    && string.IsNullOrWhiteSpace(def.Configuration)
                    && string.IsNullOrWhiteSpace(def.HubConfiguration)
                    && (def.Sources is null || def.Sources.Count == 0)))
            .Subscribe(
                pendingNode =>
                {
                    logger?.LogInformation("Compile watcher: saw Pending for {HubPath} — attempting Pending → Compiling", hubPath);
                    // Atomic Pending → Compiling transition. CompareExchange
                    // semantics inside the Update lambda: only the caller that
                    // observes status == Pending wins; others see Compiling
                    // (already transitioned by us) and return curr unchanged.
                    // The flag closes over THIS subscribe-handler invocation
                    // (a separate local per emission), so concurrent watcher
                    // emissions for the same logical Pending burst each get
                    // their own `weTransitioned` and only ONE flips to true.
                    var weTransitioned = false;
                    workspace.GetMeshNodeStream().Update(curr =>
                        {
                            if (curr.Content is not NodeTypeDefinition def) return curr;
                            if (def.CompilationStatus != CompilationStatus.Pending) return curr;
                            weTransitioned = true;
                            return curr with
                            {
                                Content = def with
                                {
                                    CompilationStatus = CompilationStatus.Compiling,
                                    LastCompileStartedAt = DateTimeOffset.UtcNow
                                }
                            };
                        })
                        .Take(1)
                        .Subscribe(
                            _ =>
                            {
                                if (!weTransitioned)
                                {
                                    logger?.LogDebug("Compile watcher: another caller already transitioned {HubPath} out of Pending — skipping dispatch", hubPath);
                                    return;
                                }

                                // Activity Control Plane: every long-running
                                // operation runs on an Activity hub
                                // (Doc/Architecture/ActivityControlPlane.md).
                                // We already flipped Compiling above; the
                                // activity OWNS the terminal write (Ok/Error +
                                // LatestAssembly{Collection,Path} + Release
                                // node creation). Single-writer per stage.
                                var meshService = hub.ServiceProvider.GetService<IMeshService>();
                                if (meshService is null)
                                {
                                    // Inline fallback when no IMeshService can
                                    // create the activity (early bootstrap /
                                    // minimal test fixture).
                                    logger?.LogDebug("Compile watcher: activity unavailable for {HubPath}, running inline", hubPath);
                                    RunCompile(workspace, hub, compilationService, pendingNode!, request: null);
                                    return;
                                }

                                NodeTypeCompilationActivity.Start(hub, hubPath, logger!)
                                    .Subscribe(
                                        activityPath => hub.Post(new RunCompileRequest(hubPath),
                                            o => o.WithTarget(new Address(activityPath))),
                                        ex => logger?.LogWarning(ex,
                                            "Compile watcher: activity start faulted for {HubPath}", hubPath));
                            },
                            ex => logger?.LogWarning(ex,
                                "Compile watcher: Pending→Compiling transition faulted for {HubPath}", hubPath));
                },
                ex => logger?.LogWarning(ex,
                    "Compile watcher faulted for {HubPath}", hub.Address.Path));

        return new CompositeDisposable(kickoffSub, watcherSub);
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
    /// <c>LatestAssemblyPath</c> check sound: a never-compiled NodeType has a
    /// null status, so a leftover assembly reference can never falsely satisfy
    /// this predicate.</para>
    ///
    /// <para>This is a metadata-only check — no <see cref="IAssemblyStore"/>
    /// probe, no <c>File.Exists</c>. The kickoff path prefers a redundant
    /// compile over a blocking store round-trip on every stream emission;
    /// the runtime miss is caught later when activation tries to hydrate the
    /// assembly and the store reports a miss.</para>
    /// </summary>
    internal static bool HasUsableBuild(MeshNode node, NodeTypeDefinition def) =>
        def.CompilationStatus == CompilationStatus.Ok
        && !string.IsNullOrEmpty(def.LatestAssemblyCollection)
        && !string.IsNullOrEmpty(def.LatestAssemblyPath)
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
                                    // Stamp LastCompiledVersion to MATCH the version the
                                    // IAssemblyStore upload used (set by
                                    // UploadToStoreIfNeeded — the captured pendingNode.Version
                                    // at compile kickoff). Using curr.Version here would
                                    // point activation at a different version than the one
                                    // the store actually has — TryGetAssemblyPath miss,
                                    // activation falls back to default config without
                                    // AddMeshDataSource, IWorkspace fails to activate.
                                    LastCompiledVersion = outcome.Result.Version ?? curr.Version,
                                    LastCompilationActivityPath = activityPath,
                                    LatestReleasePath = newReleasePath ?? def.LatestReleasePath,
                                    ReleaseNotes = newReleasePath is not null ? null : def.ReleaseNotes,
                                    CompiledSources = outcome.Result.CompiledSources
                                        ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty,
                                    // Cross-silo durable assembly reference. Populated from
                                    // the IAssemblyStore upload during compile (see
                                    // MeshNodeCompilationService.UploadToStoreIfNeeded).
                                    // Falls back to the previous values on a producer that
                                    // hasn't wired a store yet (Null store keeps the new
                                    // fields null and consumers still fall through to the
                                    // legacy AssemblyLocation path during Stage 0/1).
                                    LatestAssemblyCollection = outcome.Result.Collection ?? def.LatestAssemblyCollection,
                                    LatestAssemblyPath = outcome.Result.ContentPath ?? def.LatestAssemblyPath,
                                    // Stamp the framework version the assembly bound
                                    // against — HasUsableBuild compares this to the live
                                    // FrameworkVersion so a MeshWeaver redeploy forces a
                                    // recompile instead of loading an ABI-stale DLL.
                                    CompiledFrameworkVersion = FrameworkVersion
                                }
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
