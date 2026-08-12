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
        // 🚨 A compile activity is INFRASTRUCTURE observability, often kicked off with no user on the
        // calling thread (background recompile, grain activation, fan-out). Without an identity the
        // never-null guard fails the CreateNode closed and the .Catch below SWALLOWS it → the activity
        // node never lands, yet the parent still gets stamped LastCompilationActivityPath → progress
        // readers subscribe to a non-existent node → "NotFound for …/_Activity/compile…" resubscribe
        // storm (prod 2026-06-18). Run the write as System so it always persists.
        var accessService = hub.ServiceProvider.GetService<AccessService>();

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
                Content = new ActivityLog(ActivityCategory.Compilation)
                {
                    Id = activityId,
                    HubPath = nodeTypePath,
                    Status = ActivityStatus.Running
                }
            };

            // Emit the path only once CreateNode has persisted + registered the
            // activity node — then it is routable for the RunCompileRequest.
            // Observable.Using holds the System impersonation across the cold CreateNode's
            // Subscribe (a `using` around the return would have lapsed before the subscribe runs).
            return Observable.Using<string, IDisposable>(
                    () => accessService?.ImpersonateAsSystem()
                          ?? System.Reactive.Disposables.Disposable.Empty,
                    _ => meshService.CreateNode(node).Select(__ => activityPath))
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
    /// Append a single <see cref="LogMessage"/> (Information-level by default) to the activity
    /// log so callers can see progress in real time. No-op when
    /// <paramref name="activityPath"/> is null. Best-effort: failures log and
    /// swallow — observability must never break a compile.
    ///
    /// <para>🚨 ONE write per call, and a write is <b>O(size of the whole activity node)</b>: the
    /// patcher <c>SerializeToNode</c>s the entire content and diffs every element on EVERY update.
    /// So N appends to one activity cost <b>O(N²)</b> CPU + allocation. Never call this in a loop —
    /// collect the messages and use <see cref="AppendLogs"/> (per phase) or <see cref="Complete"/>
    /// (terminal). The per-item shape burned 719 MB of serialisation on a single memex-cloud import
    /// activity (5,239 writes over a 141 kB node) and was half of the CFS throttling on that pod.</para>
    /// </summary>
    public static void AppendLog(IMessageHub hub, string? activityPath, string message, ILogger logger,
        Microsoft.Extensions.Logging.LogLevel level = Microsoft.Extensions.Logging.LogLevel.Information) =>
        AppendLogs(hub, activityPath, [new LogMessage(message, level)], logger);

    /// <summary>
    /// Append MANY <see cref="LogMessage"/>s to the activity log in a <b>SINGLE</b>
    /// <c>stream.Update</c> — the batched form of <see cref="AppendLog"/>, and the one to use for
    /// anything that produces a line PER ITEM (per imported file, per pruned node, per diagnostic).
    /// No-op when <paramref name="activityPath"/> is null or there is nothing to append.
    /// Best-effort: failures log and swallow — observability must never break the work it observes.
    ///
    /// <para>Why batching is not a micro-optimisation: each <c>Update</c> re-serialises the WHOLE
    /// activity node to compute its patch, so appending N lines one at a time is O(N²) in CPU and
    /// allocation while appending them together is O(N). Emitting one line per phase instead of one
    /// per item is the deliberate trade — coarser live progress, bounded cost.</para>
    /// </summary>
    public static void AppendLogs(
        IMessageHub hub, string? activityPath, IReadOnlyList<LogMessage> messages, ILogger logger)
    {
        if (string.IsNullOrEmpty(activityPath) || messages.Count == 0) return;
        try
        {
            // Set the property on the activity's stream — GetMeshNodeStream
            // auto-detects own vs remote (the compile-activity handler runs ON
            // the activity hub, so this is its OWN stream — GetRemoteStream
            // would throw "Owner cannot be the same as the subscriber"). The
            // Update rides the synchronization protocol; no message post.
            // Impersonate System: an infrastructure activity-log write must not fail closed when the
            // calling thread carries no user (see Start).
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            using (accessService?.ImpersonateAsSystem())
                hub.GetWorkspace().GetMeshNodeStream(activityPath!)
                    .Update(current =>
                        current?.Content is ActivityLog log
                            ? current with
                            {
                                Content = log with
                                {
                                    Messages = log.Messages.AddRange(messages)
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
    /// Writes the terminal <paramref name="status"/> AND <paramref name="messages"/> to the
    /// activity log in a SINGLE atomic Update. Use this instead of N separate
    /// <see cref="AppendLog"/> calls followed by <see cref="MarkSucceeded"/>/<see cref="MarkFailed"/>:
    /// those are independent fire-and-forget writes, so a reader observing the terminal status
    /// could see it BEFORE the per-message appends land (or miss some entirely). One Update
    /// guarantees that the moment the activity is Failed/Succeeded, every diagnostic line is
    /// already present — so retrieving the activity log always shows the full compile output.
    /// </summary>
    public static void Complete(
        IMessageHub hub, string? activityPath, ActivityStatus status,
        IReadOnlyList<LogMessage> messages, ILogger logger)
    {
        if (string.IsNullOrEmpty(activityPath)) return;
        try
        {
            // Impersonate System: terminal activity-log write must not fail closed (see Start).
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            using (accessService?.ImpersonateAsSystem())
                hub.GetWorkspace().GetMeshNodeStream(activityPath!)
                    .Update(current =>
                        current?.Content is ActivityLog log
                            ? current with
                            {
                                Content = log with
                                {
                                    Status = status,
                                    End = DateTime.UtcNow,
                                    Messages = log.Messages.AddRange(messages)
                                }
                            }
                            : current!)
                    .Subscribe(
                        _ => { },
                        ex => logger.LogDebug(ex,
                            "Compile-activity Complete failed for {Path} (best-effort, ignored)", activityPath));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Compile-activity Complete threw for {Path} (best-effort, ignored)", activityPath);
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
            // Impersonate System: a terminal compile-activity write is INFRASTRUCTURE
            // observability — it must not fail closed when the calling thread carries no
            // user (background recompile, grain activation) or a non-writer user (a compile
            // on a read-only partition). MarkSucceeded/MarkFailed route through here, so
            // this is the one place the System scope was missing from its Start/AppendLog/
            // Complete siblings (prod 2026-06-18 phantom-activity storm class).
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            using (accessService?.ImpersonateAsSystem())
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
