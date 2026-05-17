using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // 1. Activity owns the "Compiling" transition. The watcher only
        //    flipped Pending → no state on the parent until the activity
        //    writes it. Single-writer = no race vs the watcher's previous
        //    Compiling write.
        WriteToParent(streamCache, parentPath, def => def with
        {
            CompilationStatus = CompilationStatus.Compiling,
            LastCompileStartedAt = DateTimeOffset.UtcNow,
            LastCompilationActivityPath = activityPath
        }, logger, parentPath, "Compiling");
        NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
            $"Compile started for {parentPath} (activity hub: {activityHub.Address.Path})",
            logger!);

        streamCache.GetStream(parentPath)
            .Where(node => node?.Content is NodeTypeDefinition)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(pendingNode =>
            {
                logger?.LogInformation("[NTCA] starting Roslyn for {ParentPath} (activity={ActivityPath})",
                    parentPath, activityPath);
                NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
                    $"Read NodeType MeshNode snapshot (version={pendingNode.Version}). Invoking Roslyn…",
                    logger!);

                // 🚨 Fetch sources via UNCACHED IMeshService.ObserveQuery, not via
                // the compiler's cached SyncedQuery. The cached query's Replay(1)
                // can return the pre-update V1 snapshot when this compile fires
                // immediately after a source edit (the upstream change event has
                // been emitted but the SyncedQuery's gate hasn't propagated it
                // through the Replay buffer yet). Compiling V1 source under a V2
                // version key uploads V1 bytes to v(V2-version).dll → instance2
                // activates against V1 layout → MARKER_V1 instead of MARKER_V2.
                // Repro: CodeEditRecompileTest.CodeEdit_ExplicitRelease_IsUpToDate_RecompilesOnSourceChange.
                var meshService = activityHub.ServiceProvider.GetService<IMeshService>();
                var sourcesObservable = meshService is null
                    ? Observable.Return((IReadOnlyList<MeshNode>?)null)
                    : meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                            $"namespace:{parentPath}/Source scope:subtree nodeType:Code"))
                        .Take(1)
                        .Select(r => (IReadOnlyList<MeshNode>?)r.Items.ToList())
                        .Catch<IReadOnlyList<MeshNode>?, Exception>(_ =>
                            Observable.Return((IReadOnlyList<MeshNode>?)null));

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

                    // Propagate CompileCore's discovery + result log lines onto the
                    // activity MeshNode so the GetCompilationPathRequest hydration
                    // shortcut (NodeTypeContractHandler.BuildResponseFromLocal +
                    // GetConfigurationsFromExistingAssembly) can surface them as
                    // response.Log without re-running Roslyn. Without this, the
                    // "Source query / matched N Code / Compiled assembly" lines
                    // live only on the in-process NodeCompilationResult.Log and
                    // are lost the moment the activity hub completes. Repro:
                    // CompileActivityLogTest.SuccessfulCompile_ReportsActivityLog…
                    if (outcome.Result?.Log is { } compileLog && compileLog.Messages.Count > 0)
                    {
                        foreach (var msg in compileLog.Messages)
                            NodeTypeCompilationActivity.AppendLog(
                                activityHub, activityPath, msg.Message, logger!, msg.LogLevel);
                    }

                    if (ok)
                    {
                        NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
                            $"Roslyn produced assembly at: {outcome.Result!.AssemblyLocation}",
                            logger!);
                    }
                    else
                    {
                        var errMsg = outcome.Error?.Message
                            ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                ? string.Join("; ", errs.Select(m => m.Message))
                                : "Compilation produced no assembly");
                        NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
                            $"Roslyn failed: {errMsg}", logger!,
                            Microsoft.Extensions.Logging.LogLevel.Error);
                    }

                    // 2. Release MeshNode created BEFORE the parent's Ok write
                    //    so the parent's LatestReleasePath points at an existing
                    //    node. Release.CompilationActivityPath links back to
                    //    this activity for full build-detail traceability —
                    //    the UI can follow Release → Activity to see Roslyn
                    //    diagnostics, source list, and timing.
                    string? newReleasePath = null;
                    if (ok)
                    {
                        newReleasePath = MeshDataSourceExtensions.TryCreateReleaseNode(
                            activityHub, parentPath, outcome.Result!, outcome.PendingNode, activityPath, logger);
                        // Assert the Release was created AND linked correctly.
                        // TryCreateReleaseNode returns the path on success or
                        // null when CreateNode couldn't be dispatched (no
                        // IMeshService — e.g. early bootstrap).
                        if (newReleasePath is not null)
                        {
                            NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
                                $"Release created: {newReleasePath} → assembly={outcome.Result!.AssemblyLocation}, activity={activityPath}",
                                logger!);
                            logger?.LogInformation(
                                "[NTCA] Release {ReleasePath} linked to activity {ActivityPath} + assembly {AssemblyLocation}",
                                newReleasePath, activityPath, outcome.Result.AssemblyLocation);
                        }
                        else
                        {
                            NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
                                "Release node NOT created (no IMeshService available) — assembly still usable directly",
                                logger!, Microsoft.Extensions.Logging.LogLevel.Warning);
                        }
                    }

                    // 3. Activity terminal state lands on its own ActivityLog
                    //    AFTER the release-creation log so subscribers see
                    //    "release linked" before the activity closes.
                    if (ok)
                        NodeTypeCompilationActivity.MarkSucceeded(activityHub, activityPath, logger!);
                    else
                        NodeTypeCompilationActivity.MarkFailed(activityHub, activityPath,
                            outcome.Error?.Message
                                ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                    ? string.Join("; ", errs.Select(m => m.Message))
                                    : "Compilation produced no assembly"),
                            logger!);

                    // 4. Activity writes terminal state to parent MeshNode via
                    //    the shared cached stream. AssemblyLocation,
                    //    CompiledSources, LatestReleasePath all land in a
                    //    single Update.
                    WriteToParent(streamCache, parentPath, def =>
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
                                LatestAssemblyPath = outcome.Result.ContentPath ?? def.LatestAssemblyPath
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
            })
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "[NTCA] failed to write {Transition} state to parent {ParentPath}",
                    transitionTag, parentPathForLog));
    }

    /// <summary>Per-NodeType compile outcome — either the compiler's result or the exception that aborted it.</summary>
    private record CompileOutcome(NodeCompilationResult? Result, Exception? Error, MeshNode PendingNode);
}
