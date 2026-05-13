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

        // Long-lived remote stream to the parent NodeType MeshNode — same
        // pattern as ThreadExecution.responseStream. Every state transition
        // (Compiling → Ok/Error with AssemblyLocation + CompiledSources) is
        // a single `parentStream.Update(...)` on this stream so the parent
        // hub's MeshNodeReference reducer sees one continuous patch series
        // instead of repeated point-reads.
        var activityWorkspace = activityHub.GetWorkspace();
        var parentAddress = new Address(parentPath);
        var parentStream = activityWorkspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            parentAddress, new MeshNodeReference());

        if (parentStream is null)
        {
            logger?.LogWarning("[NTCA] Parent stream null for {ParentPath}", parentPath);
            return request.Processed();
        }

        // 1. Activity owns the "Compiling" transition. The watcher only
        //    flipped Pending → no state on the parent until the activity
        //    writes it. Single-writer = no race vs the watcher's previous
        //    Compiling write.
        WriteToParent(parentStream, def => def with
        {
            CompilationStatus = CompilationStatus.Compiling,
            LastCompileStartedAt = DateTimeOffset.UtcNow,
            LastCompilationActivityPath = activityPath
        }, logger, parentPath, "Compiling");
        NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
            $"Compile started for {parentPath} (activity hub: {activityHub.Address.Path})",
            logger!);

        parentStream
            .Where(change => change?.Value?.Content is NodeTypeDefinition)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(change =>
            {
                var pendingNode = change.Value!;
                logger?.LogInformation("[NTCA] starting Roslyn for {ParentPath} (activity={ActivityPath})",
                    parentPath, activityPath);
                NodeTypeCompilationActivity.AppendLog(activityHub, activityPath,
                    $"Read NodeType MeshNode snapshot (version={pendingNode.Version}). Invoking Roslyn…",
                    logger!);

                return compilationService.CompileAndGetConfigurations(pendingNode, sourcesOverride: null)
                    .Take(1)
                    .Select(result => new CompileOutcome(result, null, pendingNode))
                    .Catch<CompileOutcome, Exception>(ex =>
                        Observable.Return(new CompileOutcome(null, ex, pendingNode)));
            })
            .Subscribe(
                outcome =>
                {
                    var ok = outcome.Error is null
                        && !string.IsNullOrEmpty(outcome.Result?.AssemblyLocation);

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

                    // 4. Activity writes terminal state to parent MeshNode via the
                    //    same long-lived stream. AssemblyLocation, CompiledSources,
                    //    LatestReleasePath all land in a single Update.
                    WriteToParent(parentStream, def =>
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
                                    ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty
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
                       extraAssemblyLocation: ok ? outcome.Result!.AssemblyLocation : null,
                       extraLastCompiledVersion: ok);
                },
                ex => logger?.LogWarning(ex, "[NTCA] compile chain faulted for {ParentPath}", parentPath));

        return request.Processed();
    }

    /// <summary>
    /// Helper: apply a <see cref="NodeTypeDefinition"/> transformation to the
    /// parent MeshNode via the long-lived <paramref name="parentStream"/>. One
    /// <see cref="ChangeItem{T}"/> patch per call. <paramref name="extraAssemblyLocation"/>
    /// (when non-null) is written to <see cref="MeshNode.AssemblyLocation"/>;
    /// <paramref name="extraLastCompiledVersion"/> stamps <c>LastCompiledVersion</c>
    /// from the current MeshNode version. Mirrors the
    /// <c>ThreadExecution.responseStream.Update(...)</c> pattern.
    /// </summary>
    private static void WriteToParent(
        ISynchronizationStream<MeshNode> parentStream,
        Func<NodeTypeDefinition, NodeTypeDefinition> transform,
        ILogger? logger,
        string parentPath,
        string transitionTag,
        string? extraAssemblyLocation = null,
        bool extraLastCompiledVersion = false)
    {
        parentStream.Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def)
                return null;
            var nextDef = transform(def);
            if (extraLastCompiledVersion)
                nextDef = nextDef with { LastCompiledVersion = curr.Version };
            var next = curr with
            {
                Content = nextDef,
                AssemblyLocation = extraAssemblyLocation ?? curr.AssemblyLocation
            };
            return new ChangeItem<MeshNode>(
                Value: next,
                ChangedBy: WellKnownUsers.System,
                StreamId: parentStream.StreamId,
                ChangeType: ChangeType.Full,
                Version: parentStream.Hub.Version,
                Updates: null);
        }, ex => logger?.LogWarning(ex,
            "[NTCA] failed to write {Transition} state to parent {ParentPath}",
            transitionTag, parentPath));
    }

    /// <summary>Per-NodeType compile outcome — either the compiler's result or the exception that aborted it.</summary>
    private record CompileOutcome(NodeCompilationResult? Result, Exception? Error, MeshNode PendingNode);
}
