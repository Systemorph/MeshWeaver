using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// The action layout area the "Sync" node menu items navigate to — performs one instance-sync
/// operation on one registration and renders a confirmation. Menu items carry no click delegate;
/// they navigate to <c>/{node}/SyncAction?source={id}&amp;op={op}</c> and this area does the work
/// (the same StopSync/Export "menu item → area that performs the op" pattern). The op runs through
/// <see cref="InstanceSyncService"/> — control-plane trigger for Sync-now, the <c>Active</c> flag
/// for pause/resume, node delete for remove — so it's identical to what the settings tab did.
/// </summary>
public static class InstanceSyncActionArea
{
    /// <summary>Area name for the instance-sync action.</summary>
    public const string AreaName = "SyncAction";

    /// <summary>Query parameter carrying the sync-source id.</summary>
    public const string SourceParam = "source";

    /// <summary>Query parameter carrying the operation (<c>syncnow</c> / <c>pause</c> / <c>remove</c>).</summary>
    public const string OpParam = "op";

    /// <summary>Op token: flip <see cref="InstanceSyncConfig.SyncRequestedAt"/> (drain now).</summary>
    public const string SyncNow = "syncnow";

    /// <summary>Op token: toggle <see cref="InstanceSyncConfig.Active"/> (pause/resume).</summary>
    public const string Pause = "pause";

    /// <summary>Op token: remove the registration (cancel sync).</summary>
    public const string Remove = "remove";

    /// <summary>Builds the href a menu item uses to invoke <paramref name="op"/> on <paramref name="sourceId"/>.</summary>
    public static string Href(string hubPath, string sourceId, string op)
        => MeshNodeLayoutAreas.BuildUrl(hubPath, AreaName,
            $"{SourceParam}={Uri.EscapeDataString(sourceId)}&{OpParam}={op}");

    /// <summary>Performs the op addressed by the area's query params and renders a confirmation.</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var spacePath = InstanceSyncService.SpaceOf(hubPath);
        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(InstanceSyncActionArea));

        var sourceId = host.Reference.GetParameterValue(SourceParam);
        var op = host.Reference.GetParameterValue(OpParam);
        if (string.IsNullOrEmpty(spacePath))
            return Observable.Return<UiControl?>(Message(
                "Not in a Space", "Sync actions run on a Space; this node has no containing Space.", backHref));
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(op))
            return Observable.Return<UiControl?>(Message("Nothing to do", "No sync source/operation specified.", backHref));

        var sync = host.Hub.ServiceProvider.GetRequiredService<InstanceSyncService>();
        var configPath = InstanceSyncService.ConfigPath(spacePath, sourceId);

        var (title, body) = op switch
        {
            SyncNow => ("Sync requested", $"Requested a sync now for <code>{Encode(sourceId)}</code>."),
            Pause => ("Paused / resumed", $"Toggled the active state of <code>{Encode(sourceId)}</code>."),
            Remove => ("Sync removed", $"Removed the sync registration <code>{Encode(sourceId)}</code>."),
            _ => ("Unknown operation", $"Unrecognized operation <code>{Encode(op)}</code>."),
        };

        IObservable<object?> action = op switch
        {
            SyncNow => sync.UpdateConfig(configPath, c => c with { SyncRequestedAt = DateTimeOffset.UtcNow })
                .Select(n => (object?)n),
            Pause => sync.UpdateConfig(configPath, c => c with { Active = !c.Active }).Select(n => (object?)n),
            Remove => sync.RemoveSyncSource(spacePath, sourceId).Select(b => (object?)b),
            _ => Observable.Return<object?>(null),
        };

        action.Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Sync action {Op} failed for {Path}", op, configPath));
        return Observable.Return<UiControl?>(Message(title, body, backHref));
    }

    private static string Encode(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static UiControl Message(string title, string bodyHtml, string backHref)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center; margin-bottom: 16px;")
                .WithView(Controls.Button("Back")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(backHref))
                .WithView(Controls.H2(title).WithStyle("margin: 0;")))
            .WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{bodyHtml}</p>"));
}
