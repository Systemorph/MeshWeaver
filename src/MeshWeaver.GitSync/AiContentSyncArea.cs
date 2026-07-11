using System.ComponentModel;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.AI;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The action layout area the "Sync to repo" AI-content menu item navigates to — writes the live
/// Agent + Skill partitions back to the on-disk <c>content/ai</c> section (via
/// <see cref="AiContentDiskWriter"/>) and renders what was written. Navigating to it runs the op
/// (identical to <see cref="GitHubActionArea"/>). Platform-admin + source-checkout gated: a deployed
/// container has no working tree to write to.
/// </summary>
public static class AiContentSyncArea
{
    /// <summary>Area name for the AI-content sync-back action.</summary>
    public const string AreaName = "AiContentSync";

    /// <summary>The href a menu item uses to invoke the sync-back on the containing node.</summary>
    public static string Href(string hubPath) => MeshNodeLayoutAreas.BuildUrl(hubPath, AreaName);

    /// <summary>Runs the sync-back and renders the result.</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(AiContentSyncArea));

        return host.Hub.IsGlobalAdmin().Take(1).SelectMany(isAdmin =>
        {
            if (!isAdmin)
                return Observable.Return<UiControl?>(Message("Platform admins only",
                    "Writing the built-in agents & skills back to the repo is a platform-admin operation.", backHref));

            var root = AiContentLocator.RepoSectionRoot();
            if (root is null)
                return Observable.Return<UiControl?>(Message("Not a source checkout",
                    "AI content sync-back writes to <code>content/ai</code> in the repo working tree — available " +
                    "only when the portal runs from a source checkout. A deployed container has no working tree.", backHref));

            var writer = host.Hub.ServiceProvider.GetService<AiContentDiskWriter>();
            if (writer is null)
                return Observable.Return<UiControl?>(Message("Unavailable",
                    "The AI content sync-back writer is not registered in this host.", backHref));

            return writer.WriteBack(root)
                .Select(result => (UiControl?)BuildResult(root, result, backHref))
                .Catch<UiControl?, Exception>(ex =>
                {
                    logger?.LogWarning(ex, "AI content sync-back failed");
                    return Observable.Return<UiControl?>(Message("Sync failed",
                        System.Net.WebUtility.HtmlEncode(ex.Message), backHref));
                })
                .StartWith((UiControl?)BuildStarting(backHref));
        });
    }

    private static UiControl BuildStarting(string backHref)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 12px;")
            .WithView(BuildHeader("Syncing AI content to repo", backHref))
            .WithView(Controls.Progress("Writing agents & skills…", null!).WithWidth("100%").WithHideNumber(true));

    private static UiControl BuildResult(string root, AiContentSyncResult result, string backHref)
    {
        // Markdown prose + a bulleted file list (a list, not tabular data — the framework markdown
        // control, never hand-built HTML).
        var md = new StringBuilder();
        md.AppendLine($"Wrote **{result.AgentsWritten}** agent(s) and **{result.SkillsWritten}** skill(s) " +
                      $"to `{root}`.");
        md.AppendLine();
        if (result.Files.Count == 0)
            md.AppendLine("_No agent or skill nodes were found to write._");
        else
            foreach (var file in result.Files.OrderBy(f => f, StringComparer.Ordinal))
                md.AppendLine($"- `{file}`");
        md.AppendLine();
        md.AppendLine("Review with `git diff content/ai` and commit to sync the changes to the repo.");

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 12px;")
            .WithView(BuildHeader("AI content synced to repo", backHref))
            .WithView(Controls.Markdown(md.ToString()));
    }

    private static StackControl BuildHeader(string title, string backHref)
        => Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center;")
            .WithView(Controls.Button("Back")
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref))
            .WithView(Controls.H2(title).WithStyle("margin: 0;"));

    // Guard message (admins-only / not-a-checkout / unavailable / failed). The body may contain a
    // pre-rendered <code> snippet, so it stays a Controls.Html paragraph (genuinely pre-rendered rich
    // text — not structured data).
    private static UiControl Message(string title, string bodyHtml, string backHref)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 16px;")
            .WithView(BuildHeader(title, backHref))
            .WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{bodyHtml}</p>"));
}
