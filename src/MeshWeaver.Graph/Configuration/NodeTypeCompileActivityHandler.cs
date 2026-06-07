using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Activity-hub handler for <see cref="RunCompileRequest"/>. The compile work
/// (Roslyn invocation, source-set gathering, write-back) runs on the activity
/// hub's dispatcher — the parent NodeType hub and the mesh hub stay responsive
/// while the activity churns.
///
/// <para>Activity Control Plane doctrine — see
/// <c>Doc/Architecture/ActivityControlPlane.md</c>: "every long-running
/// operation runs on an Activity hub."</para>
///
/// <para>This handler is registered on the Activity NodeType's
/// <c>HubConfiguration</c> (<see cref="ActivityNodeType"/>) so every activity
/// hub can accept compile work. Activities for kernel/script execution simply
/// ignore the message — the handler short-circuits when the parent path isn't
/// a NodeType definition.</para>
/// </summary>
internal static class NodeTypeCompileActivityHandler
{
    public static IMessageDelivery Handle(
        IMessageHub activityHub,
        IMessageDelivery<RunCompileRequest> request)
    {
        var logger = activityHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeTypeCompileActivity");
        var parentPath = request.Message.ParentNodeTypePath;
        var activityPath = activityHub.Address.Path;

        logger?.LogInformation(
            "[COMPILE-TRACE] NodeTypeCompileActivityHandler.Handle entered: parent={ParentPath} activity={ActivityPath}",
            parentPath, activityPath);

        // Compile is infrastructure work. Trigger-time gates whether the caller
        // is allowed to initiate a compile; the compile body itself reads
        // sources, writes assemblies and updates compile state on the parent
        // NodeType — all of which need to succeed regardless of which user (or
        // system path: startup, NodeType version bump, self-heal in
        // EnrichWithNodeType) requested it. The compile body lives entirely
        // under ImpersonateAsSystem so source reads and the terminal
        // WriteToParent (status=Ok/Error + assembly path) are never blocked by
        // the caller's per-node permissions.
        //
        // The using-scope is on the handler's action-block thread; all child
        // observables (CreateNode for the activity log, streamCache.Update for
        // WriteToParent, IAssemblyStore.Put) Subscribe synchronously inside
        // this scope, so AccessContextPropagation captures System for the
        // entire chain.
        var accessService = activityHub.ServiceProvider.GetService<AccessService>();
        var systemScope = accessService?.ImpersonateAsSystem();
        try
        {
            return HandleCore(activityHub, request, logger, parentPath, activityPath);
        }
        finally
        {
            systemScope?.Dispose();
        }
    }

    private static IMessageDelivery HandleCore(
        IMessageHub activityHub,
        IMessageDelivery<RunCompileRequest> request,
        ILogger? logger,
        string parentPath,
        string activityPath)
    {

        var compilationService = activityHub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (compilationService is null)
        {
            logger?.LogWarning("[NTCA] No IMeshNodeCompilationService — cannot compile {ParentPath}", parentPath);
            activityHub.Post(new RunCompileResponse(false, "No IMeshNodeCompilationService registered"),
                o => o.ResponseFor(request));
            return request.Processed();
        }

        // Acknowledge dispatch immediately so the caller knows the activity owns
        // the work. The terminal status will be visible on the activity's own
        // ActivityLog and on the parent NodeType's MeshNode.
        activityHub.Post(new RunCompileResponse(Dispatched: true),
            o => o.ResponseFor(request));

        // The parent NodeType MeshNode is reached ONLY through the shared
        // IMeshNodeStreamCache — never an ad-hoc activityWorkspace.GetRemoteStream.
        // An ad-hoc remote stream from the activity hub is a separate instance;
        // its updates are "lost" — never seen by the readers of the cached
        // stream (NodeTypeEnrichmentHelpers, every per-instance hub). Reads AND
        // writes both go through the one cached handle so the terminal compile
        // state actually lands and propagates.
        var streamCache = activityHub.ServiceProvider.GetService<IMeshNodeStreamCache>();
        if (streamCache is null)
        {
            logger?.LogWarning("[NTCA] IMeshNodeStreamCache not registered — cannot compile {ParentPath}", parentPath);
            return request.Processed();
        }

        // 1. NO initial WriteToParent Compiling — that flip is owned by the
        //    per-NodeType hub's `InstallCompileWatcher`, which already
        //    transitions Pending → Compiling on OWN via `UpdateOwn` BEFORE
        //    starting this activity. A second `WriteToParent Compiling` from
        //    the activity hub (which is `UpdateRemote` via the mesh-hub
        //    streamCache) computes its patch from MESH's cached view of OWN —
        //    that view may still lag behind the caller's just-issued patch
        //    (e.g. `ReleaseNotes + RequestedReleaseAt`). The whole-entity
        //    EntityUpdate then clobbers OWN's just-written fields back to
        //    their stale values — the regression reproduced by
        //    `NodeTypeReleaseTest`: release lands with `Notes = null`.
        //
        //    `LastCompilationActivityPath` is set later (inside the
        //    pendingNode-gated SelectMany below) once we know MESH's cache has
        //    caught up to OWN's Compiling state. The terminal WriteToParent
        //    (Ok / Error) also sets it as a safety net.
        NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
            $"Compile started for {parentPath} (activity hub: {activityHub.Address.Path})",
            logger!);

        // Prefer the snapshot the dispatcher (per-NodeType hub's CompileWatcher)
        // captured at the moment it flipped `Compiling`. That capture IS the
        // post-Update emission on OWN's local stream — race-free with any
        // caller patch the watcher just absorbed (test's `ReleaseNotes +
        // RequestedReleaseAt` write, `ReleaseRequestWatcher`'s Pending stamp,
        // etc.). Reading from `streamCache.GetStream(parentPath)` (a mesh-hub-
        // cached remote stream over OWN) lags those writes by DataChangedEvent
        // fan-out — the regression reproduced by `NodeTypeReleaseTest` where
        // the explicit-release MeshNode landed with `Notes = null` because the
        // pendingNode read returned a stale pre-trigger snapshot.
        //
        // Fallback: when `ParentSnapshot` is null (legacy callers, inline-
        // fallback compile path, etc.) wait for `Status == Compiling` on the
        // cached stream as a best-effort watermark. The kickoff compile uses
        // this fallback shape transparently — its dispatched Pending→Compiling
        // happens before any caller mutation, so the cached-stream read is
        // sufficient.
        var snapshotObservable = request.Message.ParentSnapshot is { } providedSnapshot
            ? Observable.Return(providedSnapshot)
            : streamCache.GetStream(parentPath, activityHub.JsonSerializerOptions)
                .Where(node => node?.Content is NodeTypeDefinition d
                    && d.CompilationStatus == CompilationStatus.Compiling)
                .Take(1);

        snapshotObservable
            // Wait for the Compiling snapshot to land before invoking Roslyn.
            // This is the activity's own internal wait, NOT the trigger (the
            // caller already got RunCompileResponse(Dispatched: true) above), so
            // a longer budget here never blocks the trigger — it only governs
            // how patiently the activity waits for the cached stream to catch up
            // to OWN's Compiling flip. 30s could strand the activity (it faults
            // → no terminal WriteToParent → NodeType stuck Compiling forever →
            // every instance renders the slow-path overlay). Capped at 60s to
            // match the consumer-side SlowPathTimeout — a longer wait does not
            // rescue a wedged grain, it only delays the fault, so 60s is the max.
            .Timeout(TimeSpan.FromSeconds(60))
            .SelectMany(pendingNode =>
            {
                logger?.LogInformation("[NTCA] starting Roslyn for {ParentPath} (activity={ActivityPath})",
                    parentPath, activityPath);
                NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
                    $"Read NodeType MeshNode snapshot (version={pendingNode.Version}). Invoking Roslyn…",
                    logger!);

                // Stamp `LastCompilationActivityPath` while the activity is
                // running so UI bindings can link to the live compile log.
                // Safe now that the cached stream has caught up to Compiling:
                // `current` inside the transform has all of OWN's prior
                // patches (test's `ReleaseNotes`, the watcher's
                // `LastReleaseRequestHandledAt`, etc.), so the EntityUpdate
                // carries them through instead of clobbering with stale values.
                WriteToParent(streamCache, activityHub.JsonSerializerOptions, parentPath, def => def with
                {
                    LastCompileStartedAt = def.LastCompileStartedAt ?? DateTimeOffset.UtcNow,
                    LastCompilationActivityPath = activityPath
                }, logger, parentPath, "Compiling");

                // Fetch sources via UNCACHED IMeshService.Query — when it
                // emits, the result is the post-write fresh source set. The
                // cached SyncedQuery's Replay(1) can return the pre-update V1
                // snapshot when this compile fires immediately after a source
                // edit (the upstream change event has been emitted but the
                // SyncedQuery's gate hasn't propagated through the Replay
                // buffer yet). Repro:
                // CodeEditRecompileTest.CodeEdit_ExplicitRelease_IsUpToDate_RecompilesOnSourceChange.
                //
                // 5s Timeout + null-fallback: Query's MergeProviderObservables
                // gates the merged Initial on every provider emitting Initial — when
                // one provider's emission stalls (storage adapter's async enumeration
                // blocked by security-service init, source MeshNode not yet visible
                // to the provider's worker thread, etc.), Take(1) waits forever.
                // Falling back to <c>sourcesOverride: null</c> lets
                // CompileAndGetConfigurations resolve sources via its cached
                // SyncedQuery (workspace.GetQuery in GetSourceCollection), which
                // takes a different aggregation path and works even when
                // Query's merge is stalled. The V1→V2 freshness regression
                // the override was added for only surfaces when a source edit
                // happens within the same compile cycle; the kickoff-driven first
                // 🚨 Source-read freshness: read each source MeshNode via the
                // per-path live stream (workspace.GetMeshNodeStream(path)),
                // NOT via the index-backed Query. The previous flow
                // (meshService.Query → IMeshQueryProvider) is gated on
                // a Replay(1) per provider plus query-merge bookkeeping and
                // can return PRE-UPDATE source MeshNodes for a window after
                // a source edit lands on the owning code hub.
                //
                // Roslyn is deterministic: read V1 source → produce V1 bytes
                // → upload to a "V2" filename. Activations resolve the V2
                // metadata correctly but the bytes are V1 — the instance
                // hub binds V1's HubConfiguration and renders V1 even
                // though every visible state says V2 is the latest.
                // Repro: CodeEditRecompileTest.NodeType_RequestedReleasePath_…
                //
                // The path list itself comes from
                // pendingNode.Content.CurrentSourceVersions — written by
                // InstallSourcesWatcher off the SAME synced query the
                // SOLE source of truth, so the watcher and the compile
                // see the same set. For each path, GetMeshNodeStream
                // returns the OWNING per-node hub's stream — authoritative
                // content, no index lag.
                var pendingDef = pendingNode.Content as NodeTypeDefinition;
                IObservable<IReadOnlyList<MeshNode>?> sourcesObservable;
                if (pendingDef?.CurrentSourceVersions is { Count: > 0 } versions)
                {
                    // 🚨 Each per-source stream MUST emit exactly ONE value
                    // (real MeshNode OR a sentinel `null!`) — never
                    // `Observable.Empty`. CombineLatest waits for EVERY
                    // input to emit at least once before it fires; if any
                    // input's 5s Timeout-then-Catch returned Empty, that
                    // input completed WITHOUT emitting, CombineLatest never
                    // emitted, `.Take(1)` completed silently, the outer
                    // SelectMany never fired, and the compile activity
                    // hung indefinitely. Observed: 28s gap between
                    // "[NTCA] starting Roslyn" and "Compiling assembly"
                    // in LinkedInTelemetryImportTest local trace; same
                    // shape behind the prod `rbuergi/CatBond` cascade
                    // (slow per-node hubs → source stream timeouts →
                    // never-firing compile → stale 30s+ callbacks).
                    //
                    // Fix: emit `null!` sentinel on Timeout/Catch so the
                    // input ALWAYS produces a value. Filter nulls AFTER
                    // CombineLatest. Outer `Timeout(10s, …)` is a
                    // hard backstop in case Subscribe itself never
                    // returns (defence in depth).
                    var paths = versions.Keys.ToArray();
                    sourcesObservable = paths
                        .Select(p => activityHub.GetWorkspace().GetMeshNodeStream(p)
                            .Where(n => n != null)
                            .Take(1)
                            .Timeout(TimeSpan.FromSeconds(5))
                            .Catch<MeshNode, Exception>(ex =>
                            {
                                logger?.LogWarning(
                                    "[NTCA] live stream read for {SourcePath} faulted ({ExType}) — emitting null sentinel; CombineLatest needs a value per input",
                                    p, ex.GetType().Name);
                                return Observable.Return<MeshNode>(null!);
                            }))
                        .CombineLatest()
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(10),
                            Observable.Return<IList<MeshNode>>(Array.Empty<MeshNode>()))
                        .Select(nodes => (IReadOnlyList<MeshNode>?)nodes
                            .Where(n => n is not null).ToList())
                        .Catch<IReadOnlyList<MeshNode>?, Exception>(ex =>
                        {
                            logger?.LogWarning(
                                "[NTCA] live source reads for {ParentPath} faulted ({ExType}) — falling back to cached SyncedQuery",
                                parentPath, ex.GetType().Name);
                            return Observable.Return((IReadOnlyList<MeshNode>?)null);
                        });
                }
                else
                {
                    // No CurrentSourceVersions yet — falls back to cached
                    // SyncedQuery via CompileAndGetConfigurations(sourcesOverride: null).
                    // This is correct for V1 (first compile): the watcher's
                    // first emission and the compile race, and either order
                    // produces V1 bytes (no pre-existing source to be stale
                    // against).
                    sourcesObservable = Observable.Return((IReadOnlyList<MeshNode>?)null);
                }

                return sourcesObservable.SelectMany(fresh =>
                    compilationService.CompileAndGetConfigurations(pendingNode, sourcesOverride: fresh)
                        .Take(1)
                        .Select(result => new CompileOutcome(result, null, pendingNode))
                        .Catch<CompileOutcome, Exception>(ex =>
                            Observable.Return(new CompileOutcome(null, ex, pendingNode))));
            })
            .Subscribe(
                outcome =>
                {
                    var ok = outcome.Error is null
                        && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation);

                    // Accumulate EVERY activity-log line (CompileCore's discovery +
                    // result lines, the Roslyn-produced/failed summary, the release
                    // outcome) into ONE list, then write them WITH the terminal status
                    // in a single atomic Update (NodeTypeCompilationActivity.Complete).
                    // The old code emitted each line as an independent fire-and-forget
                    // AppendLog and THEN a separate MarkSucceeded/MarkFailed — so a
                    // reader (UI/test) that observed the terminal status could see it
                    // before the diagnostic lines landed, surfacing a Failed activity
                    // with an empty/partial log. One write makes "status is terminal"
                    // and "every diagnostic line is present" the same observable event.
                    var activityMessages = System.Collections.Immutable.ImmutableList.CreateBuilder<LogMessage>();

                    // CompileCore's discovery + result log lines (the full Roslyn
                    // diagnostics live here — see MeshNodeCompilationService.FormatCompileFailure).
                    if (outcome.Result?.Log is { } compileLog && compileLog.Messages.Count > 0)
                        activityMessages.AddRange(compileLog.Messages);

                    if (ok)
                    {
                        activityMessages.Add(new LogMessage(
                            $"Roslyn produced assembly at: {outcome.Result!.AssemblyLocation}",
                            Microsoft.Extensions.Logging.LogLevel.Information));
                    }
                    else
                    {
                        var errMsg = outcome.Error?.Message
                            ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                ? string.Join("; ", errs.Select(m => m.Message))
                                : "Compilation produced no assembly");
                        activityMessages.Add(new LogMessage(
                            $"Roslyn failed: {errMsg}", Microsoft.Extensions.Logging.LogLevel.Error));
                    }

                    // Release MeshNode created BEFORE the parent's Ok write so the
                    // parent's LatestReleasePath points at an existing node.
                    // Release.CompilationActivityPath links back to this activity so
                    // the UI can follow Release → Activity to see the full build detail.
                    string? newReleasePath = null;
                    if (ok)
                    {
                        newReleasePath = MeshDataSourceExtensions.TryCreateReleaseNode(
                            activityHub, parentPath, outcome.Result!, outcome.PendingNode, activityPath, logger);
                        if (newReleasePath is not null)
                        {
                            activityMessages.Add(new LogMessage(
                                $"Release created: {newReleasePath} → assembly={outcome.Result!.AssemblyLocation}, activity={activityPath}",
                                Microsoft.Extensions.Logging.LogLevel.Information));
                            logger?.LogInformation(
                                "[NTCA] Release {ReleasePath} linked to activity {ActivityPath} + assembly {AssemblyLocation}",
                                newReleasePath, activityPath, outcome.Result.AssemblyLocation);
                        }
                        else
                        {
                            activityMessages.Add(new LogMessage(
                                "Release node NOT created (no IMeshService available) — assembly still usable directly",
                                Microsoft.Extensions.Logging.LogLevel.Warning));
                        }
                    }

                    // ONE atomic write: terminal status + every diagnostic line.
                    NodeTypeCompilationActivity.Complete(activityHub, activityPath,
                        ok ? ActivityStatus.Succeeded : ActivityStatus.Failed,
                        activityMessages.ToImmutable(), logger!);

                    // 4. Activity writes terminal state to parent MeshNode via
                    //    the shared cached stream. AssemblyLocation,
                    //    CompiledSources, LatestReleasePath all land in a
                    //    single Update.
                    WriteToParent(streamCache, activityHub.JsonSerializerOptions, parentPath, def =>
                    {
                        if (ok)
                            return def with
                            {
                                CompilationStatus = CompilationStatus.Ok,
                                CompilationError = null,
                                LastCompileSucceededAt = DateTimeOffset.UtcNow,
                                LastCompilationActivityPath = activityPath,
                                LatestReleasePath = newReleasePath ?? def.LatestReleasePath,
                                ReleaseNotes = newReleasePath is not null ? null : def.ReleaseNotes,
                                CompiledSources = outcome.Result!.CompiledSources
                                    ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty,
                                // Cross-silo durable assembly reference — denormalised
                                // from the IAssemblyStore upload in CompileAndGetConfigurations.
                                // Falls back to def's existing values when the producer
                                // didn't populate them (Null store).
                                LatestAssemblyCollection = outcome.Result.Collection ?? def.LatestAssemblyCollection,
                                LatestAssemblyPath = outcome.Result.ContentPath ?? def.LatestAssemblyPath,
                                // Pin the compiled assembly to the current
                                // framework version. HasUsableBuild reads this
                                // back to decide if the cached bytes are
                                // still safe to load; without it the field
                                // stays null and every activation falls
                                // through to the error overlay even with a
                                // valid local DLL.
                                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion
                            };
                        var errorSummary = outcome.Error?.Message
                            ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                ? string.Join("; ", errs.Select(m => m.Message))
                                : "Compilation produced no assembly");
                        return def with
                        {
                            CompilationStatus = CompilationStatus.Error,
                            CompilationError = errorSummary,
                            LastCompilationActivityPath = activityPath,
                            CompiledSources = null
                        };
                    }, logger, parentPath,
                       ok ? "Ok" : "Error",
                       // Stamp LastCompiledVersion to MATCH the version the IAssemblyStore
                       // upload used as its key (set by UploadToStoreIfNeeded in
                       // MeshNodeCompilationService — the captured pendingNode.Version
                       // from when the compile was kicked off). Using `curr.Version`
                       // at write-back time would point activation at a different version
                       // than the one the store actually has → TryGetAssemblyPath miss
                       // → activation falls back to default config and IWorkspace fails
                       // to activate (no AddData). Found via CodeEditRecompileTest.
                       compiledVersion: ok ? outcome.Result?.Version : null);
                },
                ex => logger?.LogWarning(ex, "[NTCA] compile chain faulted for {ParentPath}", parentPath));

        return request.Processed();
    }

    /// <summary>
    /// Helper: apply a <see cref="NodeTypeDefinition"/> transformation to the
    /// parent MeshNode through the shared <see cref="IMeshNodeStreamCache"/> —
    /// the ONE cached handle every reader of the parent stream shares, so the
    /// write actually lands and propagates (an ad-hoc remote stream would be a
    /// lost separate instance). When <paramref name="compiledVersion"/> is
    /// non-null, <c>LastCompiledVersion</c> is stamped with that value — the
    /// captured snapshot version that the IAssemblyStore upload used as its
    /// key, so activation's later <c>TryGetAssemblyPath</c> finds the bytes.
    /// </summary>
    private static void WriteToParent(
        IMeshNodeStreamCache streamCache,
        JsonSerializerOptions options,
        string parentPath,
        Func<NodeTypeDefinition, NodeTypeDefinition> transform,
        ILogger? logger,
        string parentPathForLog,
        string transitionTag,
        long? compiledVersion = null)
    {
        streamCache.Update(parentPath, curr =>
            {
                if (curr?.Content is not NodeTypeDefinition def)
                    return curr!;
                var nextDef = transform(def);
                if (compiledVersion is { } v)
                    nextDef = nextDef with { LastCompiledVersion = v };
                return curr with { Content = nextDef };
            }, options)
            .Subscribe(
                result => logger?.LogInformation(
                    "[NTCA] WriteToParent {Transition} for {ParentPath} completed — status={Status} coll={Coll} path={Path} isDirty={IsDirty} compiledSourcesCount={Count}",
                    transitionTag, parentPathForLog,
                    (result?.Content as NodeTypeDefinition)?.CompilationStatus,
                    (result?.Content as NodeTypeDefinition)?.LatestAssemblyCollection,
                    (result?.Content as NodeTypeDefinition)?.LatestAssemblyPath,
                    (result?.Content as NodeTypeDefinition)?.IsDirty,
                    (result?.Content as NodeTypeDefinition)?.CompiledSources?.Count ?? 0),
                ex => logger?.LogWarning(ex,
                    "[NTCA] failed to write {Transition} state to parent {ParentPath}",
                    transitionTag, parentPathForLog));
    }

    /// <summary>Per-NodeType compile outcome — either the compiler's result or the exception that aborted it.</summary>
    private record CompileOutcome(NodeCompilationResult? Result, Exception? Error, MeshNode PendingNode);
}
