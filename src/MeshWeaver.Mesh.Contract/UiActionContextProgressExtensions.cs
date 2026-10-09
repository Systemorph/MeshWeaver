using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Mesh;

/// <summary>
/// Binds a click's busy state (<see cref="ClickProgress"/>, <c>Doc/GUI/ButtonPendingState</c>) to work
/// that lives in the mesh: an <see cref="ActivityLog"/> the click started, or any node whose state says
/// when the click's effect has landed. Both read the node through <c>GetMeshNodeStream(path)</c> — the
/// authoritative live stream — never a query.
/// </summary>
public static class UiActionContextProgressExtensions
{
    /// <summary>
    /// Keeps the clicked control busy over the activity at <paramref name="activityPath"/>: its latest
    /// message is the status line, a terminal <see cref="ActivityLog.Status"/> settles the click
    /// (<see cref="ActivityStatus.Failed"/> shows the last error; <see cref="ActivityStatus.Cancelled"/>
    /// states "Cancelled"), and Cancel patches the activity's <c>RequestedStatus</c> through
    /// <see cref="HubActivityExtensions.CancelActivity"/> and keeps watching until it lands.
    /// <para>🚨 The node must EXIST when this is called (the click created it, or it is a known path): a
    /// point read of an absent node is answered NotFound and trips the storm-breaker on that path
    /// (<c>Doc/Architecture/CqrsAndContentAccess</c>).</para>
    /// </summary>
    /// <param name="context">The click's action context.</param>
    /// <param name="activityPath">Path of the activity node the click started.</param>
    public static void TrackActivity(this UiActionContext context, string activityPath)
    {
        var hub = context.Host.Hub;
        // A cancel write that is refused or fails would otherwise leave the click "Cancelling…" forever,
        // waiting for a Cancelled the activity will never report: it fails the click instead.
        var cancelFailed = new Subject<ClickProgress>();
        context.OnCancel(() => hub.CancelActivity(activityPath,
            error => cancelFailed.OnError(new InvalidOperationException(error))));
        context.Track(
            context.Host.Workspace.GetMeshNodeStream(activityPath)
                .Select(node => node.ContentAs<ActivityLog>(hub.JsonSerializerOptions))
                .Where(log => log is not null)
                .Select(log => FromActivity(context.Host, activityPath, log!))
                .Merge(cancelFailed),
            followThroughCancel: true);
    }

    /// <summary>
    /// Keeps the clicked control busy until the node at <paramref name="path"/> says the click's effect
    /// landed — e.g. an approval click that writes <c>RequestedAction</c> stays busy until the owner's
    /// watcher has moved the request on. <paramref name="progress"/> maps each node version to the click
    /// state to show; return <see cref="ClickProgress.Running"/> = false (or a non-null
    /// <see cref="ClickProgress.Error"/>) to settle. Same existence rule as <see cref="TrackActivity"/>.
    /// </summary>
    /// <param name="context">The click's action context.</param>
    /// <param name="path">Path of the node to watch.</param>
    /// <param name="progress">Maps the node to the click state; null skips that version.</param>
    public static void TrackNode(this UiActionContext context, string path, Func<MeshNode, ClickProgress?> progress)
        => context.Track(
            context.Host.Workspace.GetMeshNodeStream(path)
                .Select(progress)
                .Where(p => p is not null)
                .Select(p => p!));

    /// <summary>The click state an activity's current content stands for. Pure.</summary>
    /// <param name="host">The rendering host — localizes the activity's messages for the viewer.</param>
    /// <param name="activityPath">The activity's path, carried so a client can link to it.</param>
    /// <param name="log">The activity's content.</param>
    public static ClickProgress FromActivity(LayoutAreaHost host, string activityPath, ActivityLog log)
    {
        var last = log.Messages.Count > 0 ? host.Localize(log.Messages[^1]) : null;
        return log.Status switch
        {
            ActivityStatus.Running => new ClickProgress { Running = true, Status = last, ActivityPath = activityPath },
            ActivityStatus.Failed => new ClickProgress
            {
                Running = false,
                ActivityPath = activityPath,
                Error = log.Messages.LastOrDefault(m => m.LogLevel >= Microsoft.Extensions.Logging.LogLevel.Error) is { } error
                    ? host.Localize(error)
                    : last ?? host.Localize("click.failed", activityPath),
            },
            ActivityStatus.Cancelled => new ClickProgress
            {
                Running = false, ActivityPath = activityPath, Summary = host.Localize("click.cancelled"),
            },
            _ => new ClickProgress { Running = false, ActivityPath = activityPath, Summary = last },
        };
    }
}
