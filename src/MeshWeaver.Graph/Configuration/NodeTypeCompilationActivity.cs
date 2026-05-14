using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Emits an <c>Activity</c> MeshNode at <c>{nodeTypePath}/_Activity/compile-{ts}</c>
/// for every NodeType compile cycle so UI overlays and MCP agents can observe
/// compile progress / diagnostics through the canonical Activity Control Plane
/// (<c>workspace.GetMeshNodeStream(activityPath)</c>) instead of polling
/// <c>NodeTypeService.GetCompilationError</c>.
///
/// <para>Step 4 of the Activity-Control-Plane plan. The plan eventually replaces
/// <c>NodeTypeService._compilationErrors</c> / <c>_compilingInProgress</c>
/// dictionaries with a stream-backed cache keyed off these activities. This
/// helper is the additive first step: emit the activity, leave the in-memory
/// state in place. Future PRs can flip the source of truth (the "gut" phase
/// noted in the plan) once consumers have migrated to the activity stream.</para>
///
/// <para>Stateless static helpers — no DI service. Per
/// <c>Doc/Architecture/AsynchronousCalls.md</c> → "Static handlers compose".
/// All operations are best-effort: a failure to emit the activity must NEVER
/// break a compile, so every method is wrapped in a try/catch that logs and
/// swallows.</para>
/// </summary>
internal static class NodeTypeCompilationActivity
{
    /// <summary>
    /// Create the activity at <c>{nodeTypePath}/_Activity/compile-{guid}</c>
    /// with <see cref="ActivityStatus.Running"/>. Returns an observable that
    /// emits the activity path <b>after</b> the activity MeshNode's
    /// <c>CreateNode</c> completes — so the caller never races a
    /// <c>RunCompileRequest</c> against a not-yet-routable activity (the
    /// "NotFound for ...&#47;_Activity/compile..." routing warning). Emits
    /// nothing (completes empty) when no <see cref="IMeshService"/> is
    /// available or the create fails — the caller falls back to an inline
    /// compile in that case.
    /// </summary>
    public static IObservable<string> Start(IMessageHub hub, string nodeTypePath, ILogger logger)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Empty<string>();

        try
        {
            var activityId = $"compile-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}".AsActivityId();
            var activityNamespace = $"{nodeTypePath}/_Activity";
            var activityPath = $"{activityNamespace}/{activityId}";

            var node = new MeshNode(activityId, activityNamespace)
            {
                Name = $"Compile {nodeTypePath}",
                NodeType = ActivityNodeType.NodeType,
                MainNode = nodeTypePath,
                State = MeshNodeState.Active,
                Content = new ActivityLog("NodeTypeCompilation")
                {
                    Id = activityId,
                    HubPath = nodeTypePath,
                    Status = ActivityStatus.Running
                }
            };

            // Emit the path only once CreateNode has persisted + registered the
            // activity node — then it is routable for the RunCompileRequest.
            return meshService.CreateNode(node)
                .Select(_ => activityPath)
                .Catch<string, Exception>(ex =>
                {
                    logger.LogDebug(ex,
                        "Compile-activity Start failed for {Path} (best-effort, ignored)", nodeTypePath);
                    return Observable.Empty<string>();
                });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Compile-activity Start threw for {Path} (best-effort, ignored)", nodeTypePath);
            return Observable.Empty<string>();
        }
    }

    /// <summary>
    /// Append an Information-level <see cref="LogMessage"/> to the activity
    /// log so callers can see progress in real time. No-op when
    /// <paramref name="activityPath"/> is null. Best-effort: failures log and
    /// swallow — observability must never break a compile.
    /// </summary>
    public static void AppendLog(IMessageHub hub, string? activityPath, string message, ILogger logger,
        Microsoft.Extensions.Logging.LogLevel level = Microsoft.Extensions.Logging.LogLevel.Information)
    {
        if (string.IsNullOrEmpty(activityPath)) return;
        try
        {
            // Set the property on the activity's stream — GetMeshNodeStream
            // auto-detects own vs remote (the compile-activity handler runs ON
            // the activity hub, so this is its OWN stream — GetRemoteStream
            // would throw "Owner cannot be the same as the subscriber"). The
            // Update rides the synchronization protocol; no message post.
            hub.GetWorkspace().GetMeshNodeStream(activityPath!)
                .Update(current =>
                    current?.Content is ActivityLog log
                        ? current with
                        {
                            Content = log with
                            {
                                Messages = log.Messages.Add(new LogMessage(message, level))
                            }
                        }
                        : current!)
                .Subscribe(
                    _ => { },
                    ex => logger.LogDebug(ex,
                        "Compile-activity AppendLog failed for {Path} (best-effort, ignored)", activityPath));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Compile-activity AppendLog threw for {Path} (best-effort, ignored)", activityPath);
        }
    }

    /// <summary>
    /// Flip the activity content to <see cref="ActivityStatus.Succeeded"/>.
    /// No-op when <paramref name="activityPath"/> is <c>null</c>.
    /// </summary>
    public static void MarkSucceeded(IMessageHub hub, string? activityPath, ILogger logger) =>
        Update(hub, activityPath, ActivityStatus.Succeeded, error: null, logger);

    /// <summary>
    /// Flip the activity content to <see cref="ActivityStatus.Failed"/>, attaching
    /// <paramref name="error"/> as a single Error-level <see cref="LogMessage"/>
    /// (typically the formatted Roslyn diagnostics from
    /// <c>CompilationException.Message</c>).
    /// </summary>
    public static void MarkFailed(IMessageHub hub, string? activityPath, string error, ILogger logger) =>
        Update(hub, activityPath, ActivityStatus.Failed, error, logger);

    private static void Update(
        IMessageHub hub, string? activityPath, ActivityStatus status, string? error, ILogger logger)
    {
        if (string.IsNullOrEmpty(activityPath)) return;

        try
        {
            // Set the terminal status property on the activity's stream.
            // GetMeshNodeStream auto-detects own vs remote — the compile-activity
            // handler runs ON the activity hub, so this writes through its OWN
            // stream (GetRemoteStream would throw "Owner cannot be the same as
            // the subscriber"). The Update rides the synchronization protocol;
            // no UpdateNodeRequest message post.
            hub.GetWorkspace().GetMeshNodeStream(activityPath!)
                .Update(current =>
                    current?.Content is ActivityLog log
                        ? current with
                        {
                            Content = log with
                            {
                                Status = status,
                                End = DateTime.UtcNow,
                                Messages = error is { Length: > 0 }
                                    ? log.Messages.Add(new LogMessage(error,
                                        Microsoft.Extensions.Logging.LogLevel.Error))
                                    : log.Messages
                            }
                        }
                        : current!)
                .Subscribe(
                    _ => { },
                    ex => logger.LogDebug(ex,
                        "Compile-activity Update failed for {Path} (best-effort, ignored)", activityPath));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Compile-activity Update threw for {Path} (best-effort, ignored)", activityPath);
        }
    }

    private static string AsActivityId(this string s) =>
        s.Replace(":", "").Replace("-", "");
}
