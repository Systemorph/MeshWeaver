using System.Reactive.Disposables;
using System.Reactive.Linq;
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
        // sees its own NodeTypeDefinition with no compile state and no assembly
        // on disk, flip CompilationStatus = Pending so the watcher fires Roslyn
        // immediately (instead of waiting for the first GetCompilationPathRequest
        // from an instance lookup). This is the "router-accessed-the-NodeType
        // kicks off compilation" behaviour that pre-dates the watcher.
        var ownStream = workspace.GetMeshNodeStream();
        var kicked = 0;
        var kickoffSub = ownStream
            .Where(node => node?.Content is NodeTypeDefinition)
            .Take(1)
            .Subscribe(
                node =>
                {
                    if (node?.Content is not NodeTypeDefinition def) return;
                    if (def.CompilationStatus is not null
                        && def.CompilationStatus != CompilationStatus.Unknown) return;
                    if (!string.IsNullOrEmpty(node.AssemblyLocation)) return;
                    if (System.Threading.Interlocked.CompareExchange(ref kicked, 1, 0) != 0) return;

                    logger?.LogDebug(
                        "Compile kickoff: flipping Pending for {HubPath} (no status, no assembly)",
                        hub.Address.Path);
                    workspace.GetMeshNodeStream().Update(curr =>
                        curr.Content is NodeTypeDefinition d
                            && (d.CompilationStatus is null
                                || d.CompilationStatus == CompilationStatus.Unknown)
                            && string.IsNullOrEmpty(curr.AssemblyLocation)
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
                    if (System.Threading.Interlocked.CompareExchange(ref triggered, 1, 0) != 0)
                        return;
                    try
                    {
                        // Activity Control Plane: every long-running operation runs
                        // on an Activity hub (Doc/Architecture/ActivityControlPlane.md).
                        // Create the activity MeshNode, then dispatch RunCompileRequest
                        // to its hub address. The activity hub owns the Roslyn work,
                        // updates its own ActivityLog with progress, and writes the
                        // terminal CompilationStatus back to this NodeType MeshNode.
                        var activityPath = NodeTypeCompilationActivity.Start(hub, hubPath, logger!);

                        // Flip parent CompilationStatus → Compiling so subsequent
                        // emissions don't refire the watcher and so observers see
                        // the right intermediate state. The activity will write
                        // Ok/Error back on completion.
                        workspace.GetMeshNodeStream().Update(curr =>
                            curr.Content is NodeTypeDefinition d
                                ? curr with
                                {
                                    Content = d with
                                    {
                                        CompilationStatus = CompilationStatus.Compiling,
                                        LastCompileStartedAt = DateTimeOffset.UtcNow,
                                        LastCompilationActivityPath = activityPath
                                    }
                                }
                                : curr)
                            .Subscribe(
                                _ => { },
                                ex => logger?.LogWarning(ex,
                                    "Compile: failed to flip status to Compiling for {HubPath}", hubPath));

                        if (activityPath is null)
                        {
                            // No mesh service to create the activity — fall back to
                            // inline compile so the system still works in tests /
                            // early bootstrap. Logged so it's auditable.
                            logger?.LogDebug("Compile watcher: activity unavailable for {HubPath}, running inline", hubPath);
                            RunCompile(workspace, hub, compilationService, pendingNode!, request: null);
                            return;
                        }

                        hub.Post(new RunCompileRequest(hubPath),
                            o => o.WithTarget(new Address(activityPath)));
                    }
                    finally
                    {
                        // Cleared when the post-compile write-back to Ok/Error
                        // emits (next non-Pending state). Until then, a re-Pending
                        // arriving mid-flight is rare; this guard keeps the
                        // subscription single-firing.
                        System.Threading.Interlocked.Exchange(ref triggered, 0);
                    }
                },
                ex => logger?.LogWarning(ex,
                    "Compile watcher faulted for {HubPath}", hub.Address.Path));

        return new CompositeDisposable(kickoffSub, watcherSub);
    }

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
                            logger?.LogDebug("Compile success for {HubPath} → {Assembly}",
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
                                        ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty
                                },
                                AssemblyLocation = outcome.Result.AssemblyLocation
                            };
                        }

                        var errorSummary = outcome.Error?.Message
                            ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                ? string.Join("; ", errs.Select(m => m.Message))
                                : "Compilation produced no assembly");
                        logger?.LogDebug("Compile failure for {HubPath}: {Error}", hubPath, errorSummary);
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
