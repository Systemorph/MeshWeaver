using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area that renders a thin "running activities" stripe for the
/// embedding hub. Lists the activities currently in <see cref="ActivityStatus.Running"/>
/// status whose <see cref="ActivityLog.HubPath"/> equals the embedding hub's path.
///
/// <para>Each row shows the activity's category, message count, a "Cancel"
/// button (patches <c>RequestedStatus = Cancelled</c> per the
/// <see href="xref:Architecture/ActivityControlPlane">Activity Control Plane</see>
/// pattern), and a "Details" link to the activity's Overview view.</para>
///
/// <para>Embed in any executable node's layout via
/// <c>Controls.NamedArea(addr, RunningActivitiesStripe.AreaName)</c>, or render
/// directly via <c>--render RunningActivities</c> in interactive markdown. The
/// stripe collapses (renders nothing) when no activities are running, so it's
/// safe to include unconditionally.</para>
/// </summary>
public static class RunningActivitiesStripe
{
    public const string AreaName = "RunningActivities";

    public static MessageHubConfiguration AddRunningActivitiesStripe(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.WithView(AreaName, Render));

    private static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var ownPath = host.Hub.Address.Path;
        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return<UiControl?>(null);

        // Query activities under {partition}/_Activity/* and filter to ones that
        // originated from this hub. Re-runs every time the workspace changes so
        // newly-spawned runs appear immediately.
        var partitionRoot = host.Hub.Address.Segments.Length > 0
            ? host.Hub.Address.Segments[0]
            : ownPath;
        var activitiesNamespace = $"{partitionRoot}/_Activity";

        return meshService
            .ObserveQuery<MeshNode>(new MeshQueryRequest
            {
                Query = $"namespace:{activitiesNamespace} nodeType:Activity",
                Skip = 0,
                Limit = 50
            })
            .Select(c => c.Items
                .Where(node => node.Content is ActivityLog log
                    && log.Status == ActivityStatus.Running
                    && string.Equals(log.HubPath, ownPath, StringComparison.Ordinal))
                .ToList())
            .DistinctUntilChanged(EqualityComparerForActivityIds.Instance)
            .Select(activities => RenderStripe(activities))
            .Catch<UiControl?, Exception>(_ => Observable.Return<UiControl?>(null));
    }

    private static UiControl? RenderStripe(IReadOnlyList<MeshNode> running)
    {
        if (running.Count == 0) return null;

        var stack = Controls.Stack
            .WithStyle("padding: 8px 16px; gap: 8px; background: var(--neutral-layer-2); " +
                       "border-left: 4px solid var(--accent-fill-rest); border-radius: 4px;")
            .WithView(Controls.Html(
                $"<div style='font-weight: 600; font-size: 0.85rem; color: var(--neutral-foreground-hint);'>" +
                $"⏵ {running.Count} running {(running.Count == 1 ? "activity" : "activities")}</div>"));

        foreach (var activity in running)
        {
            stack = stack.WithView(BuildRow(activity));
        }
        return stack;
    }

    private static UiControl BuildRow(MeshNode activity)
    {
        var log = (ActivityLog)activity.Content!;
        var label = $"{System.Net.WebUtility.HtmlEncode(log.Category)} · {log.Messages.Count} msg";
        var elapsed = log.End is null
            ? FormatElapsed(DateTime.UtcNow - log.Start)
            : FormatElapsed(log.End.Value - log.Start);

        var detailsHref = $"/{activity.Path}";

        // Cancel: patch RequestedStatus on the activity. The activity hub's own
        // control-plane watcher (KernelContainer.StartActivityControlPlane)
        // translates the patch into the internal cancellation. No CancelXRequest
        // — see Doc/Architecture/ActivityControlPlane.md.
        var alreadyCancelling = log.RequestedStatus == ActivityStatus.Cancelled;
        var cancel = Controls.Button("Cancel")
            .WithIconStart(FluentIcons.Dismiss())
            .WithStyle(alreadyCancelling ? "opacity: 0.5;" : "");
        if (!alreadyCancelling)
        {
            cancel = cancel.WithClickAction(ctx =>
            {
                var cancelLogger = ctx.Host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.Graph.RunningActivitiesStripe");
                ctx.Host.Hub.GetWorkspace().GetMeshNodeStream(activity.Path).Update(curr =>
                    curr.Content is ActivityLog l
                        ? curr with { Content = l with { RequestedStatus = ActivityStatus.Cancelled } }
                        : curr).Subscribe(
                            _ => { },
                            ex => cancelLogger?.LogWarning(ex,
                                "RunningActivitiesStripe.Cancel: UpdateMeshNode failed for {Path}", activity.Path));
                return Task.CompletedTask;
            });
        }

        var details = Controls.NavLink("Details", detailsHref);

        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: center; padding: 4px 0;")
            .WithView(Controls.Html(
                $"<span style='flex: 1; font-family: monospace; font-size: 0.85rem;'>{label}</span>" +
                $"<span style='font-size: 0.8rem; color: var(--neutral-foreground-hint);'>{elapsed}</span>"))
            .WithView(details)
            .WithView(cancel);
    }

    private static string FormatElapsed(TimeSpan ts) =>
        ts.TotalSeconds < 1   ? "<1s"
      : ts.TotalSeconds < 60  ? $"{ts.TotalSeconds:F0}s"
      : ts.TotalMinutes < 60  ? $"{ts.TotalMinutes:F0}m"
      :                          $"{ts.TotalHours:F1}h";

    /// <summary>
    /// Compares activity-list snapshots by the set of activity IDs + message
    /// counts so the layout only re-renders when something material changed.
    /// </summary>
    private sealed class EqualityComparerForActivityIds : IEqualityComparer<IReadOnlyList<MeshNode>>
    {
        public static readonly EqualityComparerForActivityIds Instance = new();
        public bool Equals(IReadOnlyList<MeshNode>? x, IReadOnlyList<MeshNode>? y)
        {
            if (x is null || y is null) return ReferenceEquals(x, y);
            if (x.Count != y.Count) return false;
            for (var i = 0; i < x.Count; i++)
            {
                if (!string.Equals(x[i].Path, y[i].Path, StringComparison.Ordinal)) return false;
                var lx = (x[i].Content as ActivityLog)?.Messages.Count ?? 0;
                var ly = (y[i].Content as ActivityLog)?.Messages.Count ?? 0;
                if (lx != ly) return false;
            }
            return true;
        }
        public int GetHashCode(IReadOnlyList<MeshNode> obj) => obj.Count;
    }
}
