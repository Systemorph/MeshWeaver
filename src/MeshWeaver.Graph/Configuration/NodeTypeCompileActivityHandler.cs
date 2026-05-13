using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
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

        // Read the parent NodeType MeshNode (cross-hub) to get the current
        // NodeTypeDefinition snapshot used as the compile input.
        var activityWorkspace = activityHub.GetWorkspace();
        var parentAddress = new Address(parentPath);
        var parentStream = activityWorkspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            parentAddress, new MeshNodeReference());

        if (parentStream is null)
        {
            logger?.LogWarning("[NTCA] Parent stream null for {ParentPath}", parentPath);
            return request.Processed();
        }

        parentStream
            .Where(change => change?.Value?.Content is NodeTypeDefinition)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(change =>
            {
                var pendingNode = change.Value!;
                logger?.LogInformation("[NTCA] starting compile for {ParentPath} (activity={ActivityPath})",
                    parentPath, activityPath);

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

                    // Update activity log → terminal status (observability)
                    if (ok)
                        NodeTypeCompilationActivity.MarkSucceeded(activityHub, activityPath, logger!);
                    else
                        NodeTypeCompilationActivity.MarkFailed(activityHub, activityPath,
                            outcome.Error?.Message
                                ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                    ? string.Join("; ", errs.Select(m => m.Message))
                                    : "Compilation produced no assembly"),
                            logger!);

                    // Write compile state back to parent NodeType MeshNode (cross-hub).
                    // The activity is the owner of this update — the parent hub
                    // watches its own stream and sees the result.
                    string? newReleasePath = null;
                    if (ok)
                    {
                        newReleasePath = MeshDataSourceExtensions.TryCreateReleaseNode(
                            activityHub, parentPath, outcome.Result!, outcome.PendingNode, activityPath, logger);
                    }

                    activityWorkspace.GetMeshNodeStream(parentPath).Update(curr =>
                    {
                        if (curr.Content is not NodeTypeDefinition def)
                            return curr;
                        if (ok)
                        {
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
                                    CompiledSources = outcome.Result!.CompiledSources
                                        ?? System.Collections.Immutable.ImmutableDictionary<string, long>.Empty
                                },
                                AssemblyLocation = outcome.Result.AssemblyLocation
                            };
                        }
                        var errorSummary = outcome.Error?.Message
                            ?? (outcome.Result?.Log?.Errors() is { Count: > 0 } errs
                                ? string.Join("; ", errs.Select(m => m.Message))
                                : "Compilation produced no assembly");
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
                    }).Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex,
                            "[NTCA] failed to write post-compile status for {ParentPath}", parentPath));
                },
                ex => logger?.LogWarning(ex, "[NTCA] compile chain faulted for {ParentPath}", parentPath));

        return request.Processed();
    }

    /// <summary>Per-NodeType compile outcome — either the compiler's result or the exception that aborted it.</summary>
    private record CompileOutcome(NodeCompilationResult? Result, Exception? Error, MeshNode PendingNode);
}
