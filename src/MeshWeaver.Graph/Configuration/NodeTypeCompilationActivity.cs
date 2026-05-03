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
    /// with <see cref="ActivityStatus.Running"/>. Returns the activity path so
    /// the caller can hand it to <see cref="MarkSucceeded"/> /
    /// <see cref="MarkFailed"/> on completion. Returns <c>null</c> when the
    /// CreateNode dispatch couldn't even be posted (e.g. mesh service not
    /// registered yet at startup).
    /// </summary>
    public static string? Start(IMessageHub hub, string nodeTypePath, ILogger logger)
    {
        try
        {
            var meshService = hub.ServiceProvider.GetService<IMeshService>();
            if (meshService is null) return null;

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

            // Fire-and-forget — observability, not correctness.
            meshService.CreateNode(node).Subscribe(
                _ => { },
                ex => logger.LogDebug(ex,
                    "Compile-activity Start failed for {Path} (best-effort, ignored)", nodeTypePath));

            return activityPath;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Compile-activity Start threw for {Path} (best-effort, ignored)", nodeTypePath);
            return null;
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
            var workspace = hub.GetWorkspace();
            // Cross-hub update — the activity hub owns the MeshNodeReference reducer.
            // Use GetRemoteStream + Update so the activity's own workspace ticks and
            // subscribers see the terminal snapshot. Same pattern as
            // Doc/Architecture/AsynchronousCalls.md → "Updating a remote MeshNode".
            workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                    new Address(activityPath!), new MeshNodeReference())
                .Take(1)
                .Subscribe(change =>
                {
                    var current = change.Value;
                    if (current?.Content is not ActivityLog log) return;

                    var updated = log with
                    {
                        Status = status,
                        End = DateTime.UtcNow,
                        Messages = error is { Length: > 0 }
                            ? log.Messages.Add(new LogMessage(error,
                                Microsoft.Extensions.Logging.LogLevel.Error))
                            : log.Messages
                    };
                    var updatedNode = current with { Content = updated };
                    hub.Post(
                        new UpdateNodeRequest(updatedNode),
                        o => o.WithTarget(new Address(activityPath!)));
                },
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
