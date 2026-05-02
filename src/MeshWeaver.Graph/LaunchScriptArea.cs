using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Reusable layout view that renders a "Launch script" button against an
/// executable Code node. Designed to be embedded in interactive markdown via
/// <c>--render LaunchScript/{codeNodePath}</c>.
///
/// <para>Click flow:</para>
/// <list type="number">
///   <item>Click posts <see cref="ExecuteScriptRequest"/> to the target Code node.</item>
///   <item>The Code node creates an <see cref="ActivityLog"/> in the configured
///   <c>ActivityParentPath</c> (defaults to user's home).</item>
///   <item>The activity surface streams progress messages via the standard
///   <c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c> pattern — viewers
///   navigate to the activity to watch the run unfold.</item>
/// </list>
/// </summary>
public static class LaunchScriptArea
{
    public const string AreaName = "LaunchScript";

    /// <summary>
    /// Adds the LaunchScript view to the host's layout. Embedders pass the
    /// target Code node path as the area suffix:
    /// <c>--render LaunchScript/{codeNodePath}</c>.
    /// </summary>
    public static MessageHubConfiguration AddLaunchScriptArea(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.WithView(AreaName, Render));

    private static UiControl Render(LayoutAreaHost host, RenderingContext ctx)
    {
        // The full area string is "LaunchScript/{codeNodePath}". Strip the prefix.
        var prefix = AreaName + "/";
        var codeNodePath = ctx.Area.StartsWith(prefix, StringComparison.Ordinal)
            ? ctx.Area[prefix.Length..]
            : null;

        if (string.IsNullOrWhiteSpace(codeNodePath))
        {
            return Controls.Markdown(
                "*No Code node specified — use `LaunchScript/{codeNodePath}` to target a script.*");
        }

        var targetAddress = new Address(codeNodePath);

        var button = Controls.Button("Launch script")
            .WithIconStart(FluentIcons.Play())
            .WithAppearance(Appearance.Accent)
            .WithClickAction(clickCtx =>
            {
                // Fire-and-forget: the click triggers the run; observers go
                // watch the activity feed (e.g. via the user's home page).
                clickCtx.Host.Hub.Post(
                    new ExecuteScriptRequest(),
                    o => o.WithTarget(targetAddress));
                return Task.CompletedTask;
            });

        var hint = Controls.Html(
            "<div style='margin-top: 8px; font-size: 0.85rem; color: var(--neutral-foreground-hint);'>" +
            $"Runs <code>{System.Net.WebUtility.HtmlEncode(codeNodePath)}</code>. " +
            "Watch progress in your home's activity feed." +
            "</div>");

        return Controls.Stack
            .WithStyle("padding: 8px;")
            .WithView(button)
            .WithView(hint);
    }
}
