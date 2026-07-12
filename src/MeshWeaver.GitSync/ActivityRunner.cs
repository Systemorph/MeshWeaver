using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The progress/cancel surface a command sees while running inside an activity. The command
/// calls <see cref="Log"/> to append a progress line (visible live on the activity node) and
/// observes <see cref="CancellationToken"/> to honour a user-requested cancel.
/// </summary>
public sealed class ActivityContext
{
    private readonly Action<string, LogLevel> append;

    internal ActivityContext(string activityPath, CancellationToken ct, Action<string, LogLevel> append)
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
/// A generic "run a command as an activity" framework — the agreed contract for any long-running
/// operation (GitHub I/O, export, mirror, …). A <b>command</b> is
/// <c>Func&lt;<see cref="ActivityContext"/>, IObservable&lt;Unit&gt;&gt;</c>; the runner:
/// <list type="number">
///   <item>creates an <c>Activity</c> MeshNode at <c>{partition}/_Activity/{id}</c> (Status = Running);</item>
///   <item>watches that node for <c>RequestedStatus = Cancelled</c> and trips the command's
///     <see cref="CancellationToken"/> (the standard Activity Control Plane — see
///     <c>Doc/Architecture/ActivityControlPlane.md</c>);</item>
///   <item>runs the command (its I/O already goes through <c>IIoPool</c>, so the action block stays
///     responsive), forwarding <see cref="ActivityContext.Log"/> lines onto the node live;</item>
///   <item>writes the terminal <c>Status</c> (Succeeded / Failed / Cancelled) + messages in one
///     atomic update.</item>
/// </list>
///
/// <para>🚨 Reactive, no <c>async</c>/<c>await</c>. The public surface is a static
/// <see cref="IMessageHub"/> extension (<see cref="RunActivity"/>): the GUI calls it from a click,
/// tests call it in isolation, and both observe progress / drive cancel through the returned
/// activity path. This is the single entry point — there is no per-operation request type.</para>
/// </summary>
public static class ActivityRunner
{
    /// <summary>
    /// Starts <paramref name="command"/> as an activity under <paramref name="partitionPath"/> and
    /// returns the activity node path. Subscribe to <c>GetMeshNodeStream(path)</c> to watch progress
    /// (<see cref="ActivityLog.Messages"/> / <see cref="ActivityLog.Status"/>); cancel via
    /// <c>hub.CancelActivity(path)</c>. <paramref name="onActivityCreated"/> fires once with the
    /// path (so a GUI can immediately bind to it).
    /// </summary>
    public static IObservable<string> RunActivity(
        this IMessageHub hub,
        string partitionPath,
        string category,
        string title,
        Func<ActivityContext, IObservable<Unit>> command,
        Action<string>? onActivityCreated = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(command);
        // 🚨 Explicit owner dependency: the activity is created at {partitionPath}/_Activity/{id} with
        // MainNode = partitionPath, so partitionPath MUST be a real, routable owning node. An empty /
        // whitespace partitionPath collapses to a bare _Activity/{id} (empty owner) — there is no
        // partition / per-node hub to route to, so every poster/subscriber NotFound-storms the router.
        // Fail fast here rather than relying solely on the create-before-execute order (STEP 1 below)
        // + the create-boundary ownerless guard to reject it downstream; both are the backstop, but the
        // precondition belongs at the entry point. A not-yet-provisioned (but non-empty) partition is
        // fine — EnsurePartitionBootstrap on the CreateNode path provisions + roots it.
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionPath);

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.GitSync.ActivityRunner");
        var workspace = hub.GetWorkspace();

        var id = Guid.NewGuid().ToString("N")[..8];
        var activityPath = $"{partitionPath}/_Activity/{id}";
        var node = new MeshNode(id, $"{partitionPath}/_Activity")
        {
            NodeType = "Activity",
            Name = title,
            State = MeshNodeState.Active,
            MainNode = partitionPath,
            Content = new ActivityLog(category)
            {
                Id = id,
                HubPath = activityPath,
                Status = ActivityStatus.Running,
                Messages = ImmutableList.Create(new LogMessage(title, LogLevel.Information)),
            },
        };

        // STEP 1: create the activity node. CreateNode captures the caller's
        // identity synchronously and stamps it as the node's CreatedBy — so the
        // created node IS the owner record. This step completes on its own; the
        // execution is a SEPARATE step (below), never nested in the create.
        return meshService.CreateNode(node).SelectMany(created =>
        {
            onActivityCreated?.Invoke(activityPath);

            // STEP 2 (separate): run the activity under its OWNER. The command and
            // its Append/Finish writes fire on IIoPool / reactive scheduler hops
            // where the originating AsyncLocal AccessContext has cleared — so we
            // re-establish the activity owner (created.CreatedBy) AT THE SUBSCRIBE
            // via Observable.Using(FromNode). Mirrors the thread-round owner model
            // (ThreadExecution.InstallExecRoundWatcher) exactly. The owner snapshot
            // is also threaded into Append/Finish so each cross-hub write re-stamps
            // it at its own .Update() invocation (those fire from arbitrary command
            // threads, past the outer scope). RLS: the activity satellite delegates
            // Update to the partition MainNode, so only a principal who can Update
            // the partition — the owner, by construction — may write it.
            var owner = OwnerContextOf(created);
            return Observable.Using(
                () => AccessContextScope.FromNode(created, accessService, logger),
                _ =>
                {
                    var cts = new CancellationTokenSource();
                    // Cancel watcher: RequestedStatus = Cancelled trips the command's token
                    // (Activity Control Plane). Subscribed for the life of the run; disposed on
                    // completion.
                    var cancelWatch = workspace.GetMeshNodeStream(activityPath)
                        .Select(n => n.ContentAs<ActivityLog>(hub.JsonSerializerOptions)?.RequestedStatus)
                        .Where(s => s == ActivityStatus.Cancelled)
                        .Take(1)
                        .Subscribe(_ =>
                        {
                            logger?.LogInformation("Activity {Path} cancel requested", activityPath);
                            try { cts.Cancel(); } catch { /* already disposed */ }
                        });

                    var ctx = new ActivityContext(activityPath, cts.Token,
                        (msg, level) => Append(workspace, accessService, owner, activityPath, msg, level, logger));

                    // Run the command; on terminal, write the final Status + dispose watcher/cts.
                    return command(ctx)
                        .DefaultIfEmpty(Unit.Default)
                        .TakeLast(1)
                        .SelectMany(_ => Finish(workspace, accessService, owner, activityPath, ActivityStatus.Succeeded, null, logger))
                        .Catch<Unit, Exception>(ex =>
                        {
                            var cancelled = ex is OperationCanceledException || cts.IsCancellationRequested;
                            logger?.LogWarning(ex, "Activity {Path} {Outcome}", activityPath,
                                cancelled ? "cancelled" : "failed");
                            return Finish(workspace, accessService, owner, activityPath,
                                cancelled ? ActivityStatus.Cancelled : ActivityStatus.Failed,
                                cancelled ? "Cancelled." : ex.Message, logger);
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
    /// The owner identity to re-stamp on every activity-execution write. Reads the
    /// created node's <see cref="MeshNode.CreatedBy"/> (the principal who triggered
    /// the activity). Null for framework-owned nodes with no CreatedBy — Append/Finish
    /// then leave the ambient untouched (the outer <c>FromNode</c> already chose System
    /// for that case).
    /// </summary>
    private static AccessContext? OwnerContextOf(MeshNode created) =>
        string.IsNullOrEmpty(created.CreatedBy)
            ? null
            : new AccessContext { ObjectId = created.CreatedBy, Name = created.CreatedBy };

    private static void Append(
        IWorkspace workspace, AccessService? accessService, AccessContext? owner,
        string activityPath, string message, LogLevel level, ILogger? logger)
    {
        // 🚨 Re-establish the owner identity AT THIS write's .Update() invocation.
        // ctx.Log fires from the command's own threads (IIoPool body, reactive
        // hops) where the ambient AccessContext has cleared; MeshNodeStreamHandle.Update
        // captures Context ?? CircuitContext synchronously here, so without re-stamping
        // the cross-hub patch posts context-null → the partition's RLS denies → the
        // progress line never lands. The owner can Update the partition (the satellite
        // rule delegates there), so the write succeeds under their identity.
        using (owner is not null && accessService is not null ? accessService.SwitchAccessContext(owner) : null)
        {
            workspace.GetMeshNodeStream(activityPath).Update(node =>
            {
                if (node.Content is not ActivityLog log) return node;
                return node with { Content = log with { Messages = log.Messages.Add(new LogMessage(message, level)) } };
            }).Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Activity log append failed for {Path}", activityPath));
        }
    }

    private static IObservable<Unit> Finish(
        IWorkspace workspace, AccessService? accessService, AccessContext? owner,
        string activityPath, ActivityStatus status, string? finalMessage, ILogger? logger)
    {
        // 🚨 Same owner re-stamp as Append: the terminal Status write fires on the
        // command's completion hop, past the originating scope. Re-establish the owner
        // so the partition RLS lets the write land — otherwise the activity hangs
        // Running forever.
        using (owner is not null && accessService is not null ? accessService.SwitchAccessContext(owner) : null)
        {
            return workspace.GetMeshNodeStream(activityPath).Update(node =>
            {
                if (node.Content is not ActivityLog log) return node;
                var messages = string.IsNullOrEmpty(finalMessage)
                    ? log.Messages
                    : log.Messages.Add(new LogMessage(finalMessage,
                        status == ActivityStatus.Failed ? LogLevel.Error : LogLevel.Information));
                // Honour what the command reported via ctx.Log: ActivityLog.Finish computes
                // MAX(status, roll-up from Messages) — an Error line a command appended flips
                // a would-be Succeeded terminal to Failed, a Warning line to Warning; explicit
                // Failed / Cancelled always stand (they are more severe in the enum order).
                // Without this, a command that reports per-item errors but completes without
                // throwing would end "Succeeded" with errors in its own log.
                var finished = (log with { Messages = messages }).Finish(log.Version, status);
                return node with
                {
                    Content = finished with { RequestedStatus = null }
                };
            }).Select(_ => Unit.Default);
        }
    }
}
