using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// The progress / cancel surface a content-indexing command sees while running inside an Activity.
/// The command calls <see cref="Log"/> to append a live progress line to the activity node and
/// observes <see cref="CancellationToken"/> to honour a user-requested cancel.
/// </summary>
public sealed class ContentIndexingActivityContext
{
    private readonly Action<string, LogLevel> append;

    internal ContentIndexingActivityContext(string activityPath, CancellationToken ct, Action<string, LogLevel> append)
    {
        ActivityPath = activityPath;
        CancellationToken = ct;
        this.append = append;
    }

    /// <summary>The activity node path (<c>{partition}/_Activity/{id}</c>) the command runs under.</summary>
    public string ActivityPath { get; }

    /// <summary>Trips when the user requests cancel (<c>RequestedStatus = Cancelled</c> on the activity).</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Appends a progress line to the activity's <see cref="ActivityLog.Messages"/> (shown live).</summary>
    public void Log(string message, LogLevel level = LogLevel.Information) => append(message, level);
}

/// <summary>
/// Runs a content-indexing operation as an <c>Activity</c> (operations-as-scripts): the indexing work
/// NEVER runs inline on the upload handler's continuation — it runs in its own observable, cancellable
/// Activity node, so it is inspectable, cancellable, and crash-recoverable through the canonical
/// Activity Control Plane (see <c>Doc/Architecture/ActivityControlPlane.md</c>).
///
/// <para>The runner:
/// <list type="number">
///   <item>creates an <c>Activity</c> MeshNode at <c>{partition}/_Activity/{id}</c> (Status = Running);</item>
///   <item>watches it for <c>RequestedStatus = Cancelled</c> and trips the command's
///     <see cref="ContentIndexingActivityContext.CancellationToken"/>;</item>
///   <item>runs the command (whose I/O goes through <c>IIoPool</c>, keeping the action block
///     responsive), forwarding <see cref="ContentIndexingActivityContext.Log"/> lines onto the node live;</item>
///   <item>writes the terminal <c>Status</c> (Succeeded / Failed / Cancelled) + messages in one atomic
///     update.</item>
/// </list></para>
///
/// <para>🚨 Reactive, no <c>async</c>/<c>await</c>. Mirrors the agreed <c>MeshWeaver.GitSync.ActivityRunner</c>
/// contract; kept here so the indexing project takes no GitSync dependency.</para>
/// </summary>
internal static class ContentIndexingActivity
{
    /// <summary>
    /// Starts <paramref name="command"/> as an activity under <paramref name="partitionPath"/> and
    /// returns the activity node path. Subscribe to <c>GetMeshNodeStream(path)</c> to watch progress;
    /// cancel via <c>hub.CancelActivity(path)</c>. <paramref name="onActivityCreated"/> fires once with
    /// the path (so a GUI / test can immediately bind to it).
    /// </summary>
    public static IObservable<string> Run(
        IMessageHub hub,
        string partitionPath,
        string title,
        Func<ContentIndexingActivityContext, IObservable<Unit>> command,
        Action<string>? onActivityCreated = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(command);

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.ContentCollections.Indexing.Graph.ContentIndexingActivity");
        var workspace = hub.GetWorkspace();

        var id = Guid.NewGuid().ToString("N")[..8];
        var activityPath = $"{partitionPath}/_Activity/{id}";
        var node = new MeshNode(id, $"{partitionPath}/_Activity")
        {
            NodeType = "Activity",
            Name = title,
            State = MeshNodeState.Active,
            MainNode = partitionPath,
            Content = new ActivityLog(ActivityCategory.DataUpdate)
            {
                Id = id,
                HubPath = activityPath,
                Status = ActivityStatus.Running,
                Messages = ImmutableList.Create(new LogMessage(title, LogLevel.Information)),
            },
        };

        // STEP 1: create the activity node (captures the caller's identity → stamps
        // CreatedBy = owner). Completes on its own; the execution is a SEPARATE step.
        return meshService.CreateNode(node).SelectMany(created =>
        {
            onActivityCreated?.Invoke(activityPath);

            // STEP 2 (separate): run under the activity OWNER. The indexing command +
            // its Append/Finish writes fire on IIoPool / reactive hops where the
            // originating AccessContext has cleared — re-establish the owner AT THE
            // SUBSCRIBE via Observable.Using(FromNode), and re-stamp it on each
            // cross-hub write (Append/Finish) at its own .Update() invocation. Mirrors
            // the thread-round owner model + MeshWeaver.GitSync.ActivityRunner.
            var owner = OwnerContextOf(created);
            return Observable.Using(
                () => AccessContextScope.FromNode(created, accessService, logger),
                _ =>
                {
                    var cts = new CancellationTokenSource();
                    // Cancel watcher: RequestedStatus = Cancelled trips the command's token (Activity
                    // Control Plane). Subscribed for the life of the run; disposed on completion.
                    var cancelWatch = workspace.GetMeshNodeStream(activityPath)
                        .Select(n => n.ContentAs<ActivityLog>(hub.JsonSerializerOptions)?.RequestedStatus)
                        .Where(s => s == ActivityStatus.Cancelled)
                        .Take(1)
                        .Subscribe(_ =>
                        {
                            logger?.LogInformation("Indexing activity {Path} cancel requested", activityPath);
                            try { cts.Cancel(); } catch { /* already disposed */ }
                        });

                    var ctx = new ContentIndexingActivityContext(activityPath, cts.Token,
                        (msg, level) => Append(workspace, accessService, owner, activityPath, msg, level, logger));

                    return command(ctx)
                        .DefaultIfEmpty(Unit.Default)
                        .TakeLast(1)
                        .SelectMany(_ => Finish(workspace, accessService, owner, activityPath, ActivityStatus.Succeeded, null, logger))
                        .Catch<Unit, Exception>(ex =>
                        {
                            var cancelled = ex is OperationCanceledException || cts.IsCancellationRequested;
                            logger?.LogWarning(ex, "Indexing activity {Path} {Outcome}", activityPath,
                                cancelled ? "cancelled" : "failed");
                            return Finish(workspace, accessService, owner, activityPath,
                                    cancelled ? ActivityStatus.Cancelled : ActivityStatus.Failed,
                                    cancelled ? "Cancelled." : ex.Message, logger)
                                // A genuine failure (not a user cancel) is surfaced to platform
                                // admins — the graceful sink for an infrastructure failure. Best
                                // effort: never re-throws, so it cannot turn one failed activity
                                // into a second failure. See the /storm + /async skills.
                                .SelectMany(_ => cancelled
                                    ? Observable.Return(Unit.Default)
                                    : NotifyAdminsOfFailure(hub, accessService, activityPath, ex.Message, logger));
                        })
                        .Finally(() =>
                        {
                            cancelWatch.Dispose();
                            cts.Dispose();
                        })
                        .Select(_ => activityPath);
                });
        });
    }

    /// <summary>
    /// The owner identity to re-stamp on every activity-execution write — the created
    /// node's <see cref="MeshNode.CreatedBy"/>. Null for framework-owned nodes with no
    /// CreatedBy (the outer <c>FromNode</c> already chose System for that case).
    /// </summary>
    private static AccessContext? OwnerContextOf(MeshNode created) =>
        string.IsNullOrEmpty(created.CreatedBy)
            ? null
            : new AccessContext { ObjectId = created.CreatedBy, Name = created.CreatedBy };

    private static void Append(
        IWorkspace workspace, AccessService? accessService, AccessContext? owner,
        string activityPath, string message, LogLevel level, ILogger? logger)
    {
        // 🚨 Re-establish the owner identity at THIS write's .Update() invocation — ctx.Log
        // fires from the command's own threads (IIoPool body / reactive hops) where the
        // ambient AccessContext has cleared; without re-stamping, the cross-hub patch posts
        // context-null → the partition RLS denies → the progress line never lands.
        using (owner is not null && accessService is not null ? accessService.SwitchAccessContext(owner) : null)
        {
            workspace.GetMeshNodeStream(activityPath).Update(node =>
            {
                // ContentAs<T>, NOT `Content as ActivityLog`: the activity node is owned by the partition's
                // _Activity hub, so this cross-hub Update lambda diffs against a LOCAL mirror whose Content
                // can be a JsonElement (not strongly-typed). A plain cast would be null → the update no-ops →
                // the RFC 7396 patch is never sent. ContentAs deserializes the JsonElement so the write lands.
                // (project_baddata_contentas_pattern.)
                var log = node.ContentAs<ActivityLog>(workspace.Hub.JsonSerializerOptions, logger);
                if (log is null) return node;
                return node with { Content = log with { Messages = log.Messages.Add(new LogMessage(message, level)) } };
            }).Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Indexing activity log append failed for {Path}", activityPath));
        }
    }

    private static IObservable<Unit> Finish(
        IWorkspace workspace, AccessService? accessService, AccessContext? owner,
        string activityPath, ActivityStatus status, string? finalMessage, ILogger? logger)
    {
        // 🚨 Same owner re-stamp as Append: the terminal Status write fires on the command's
        // completion hop, past the originating scope. Re-establish the owner so the partition
        // RLS lets the write land — otherwise the activity hangs Running forever.
        using (owner is not null && accessService is not null ? accessService.SwitchAccessContext(owner) : null)
        {
            return workspace.GetMeshNodeStream(activityPath).Update(node =>
            {
                // ContentAs<T>, NOT `Content as ActivityLog`: the cross-hub Update lambda diffs against a
                // local mirror whose Content may be a JsonElement; a plain cast would no-op and the terminal
                // Status would never be written → the activity hangs Running forever (the ReindexAll / Upload
                // indexing-activity timeout). See project_baddata_contentas_pattern.
                var log = node.ContentAs<ActivityLog>(workspace.Hub.JsonSerializerOptions, logger);
                if (log is null) return node;
                var messages = string.IsNullOrEmpty(finalMessage)
                    ? log.Messages
                    : log.Messages.Add(new LogMessage(finalMessage,
                        status == ActivityStatus.Failed ? LogLevel.Error : LogLevel.Information));
                return node with
                {
                    Content = log with { Messages = messages, Status = status, End = DateTime.UtcNow, RequestedStatus = null }
                };
            }).Select(_ => Unit.Default);
        }
    }

    /// <summary>
    /// On an indexing activity ending Failed, emits ONE notification anchored under the
    /// <c>Admin</c> partition (PermissionEvaluator.AdminScope) so every platform admin — and only
    /// they, via RLS on the Admin partition — sees it in their bell; the bell navigates to the
    /// failed activity. Emitted as <b>System</b> (infrastructure identity, so the write does not
    /// depend on the failed round's user context surviving the reactive hop). Best-effort: a
    /// notify error is logged and absorbed here — this IS the graceful sink, so it must never throw
    /// (a thrown notify would turn one failed activity into a second failure / a retry). Mirrors
    /// NodeTypeCompileParkRegistry.EmitFailureNotification.
    /// </summary>
    private static IObservable<Unit> NotifyAdminsOfFailure(
        IMessageHub hub, AccessService? accessService, string activityPath, string error, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(Unit.Default);

        // ImpersonateAsSystem is set when CreateNotification CALLS CreateNode (the write primitive
        // snapshots the ambient AccessContext at call time and carries it through the later
        // Subscribe — see AccessContextPropagation.md), so the cold write still runs as System.
        using (accessService?.ImpersonateAsSystem())
            return NotificationService.CreateNotification(
                    meshService,
                    "Admin",
                    "Content indexing failed",
                    $"The indexing activity '{activityPath}' ended in failure: {error}",
                    NotificationType.General,
                    targetNodePath: activityPath,
                    createdBy: "system")
                .Select(_ => Unit.Default)
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "Failed to emit admin notification for failed indexing activity {Path}", activityPath);
                    return Observable.Return(Unit.Default);
                });
    }
}
