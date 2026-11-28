using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>
/// The PULL REQUEST DRAFT node's own page. Until this existed, the only way to open a drafted pull request
/// was the Sync tab's editor, so a person who navigated to the draft node (<c>{space}/_PullRequest/{id}</c>)
/// — from a notification, a link, the node's parent — found only the default node properties and no
/// "Open pull request" button.
///
/// <para>The page is the SAME editor the Sync tab renders (<see cref="GitHubSyncSettingsTab.BuildPullRequestEditor"/>):
/// the node-content editor bound directly to the node stream, a Submit button that runs
/// <c>OpenPullRequestOnGitHub</c> as an activity, a live "check status" button, and the link row. One
/// submit path, two surfaces — nothing replicated and no second submit implementation to drift.</para>
/// </summary>
public static class PullRequestLayoutAreas
{
    /// <summary>The landing area of a pull-request draft node.</summary>
    public const string OverviewArea = "Overview";

    private const string ResultId = "prDraftResult";
    private const string ActivityPathId = "prDraftActivityPath";

    /// <summary>Registers the draft node's page on <paramref name="configuration"/>.</summary>
    /// <param name="configuration">The pull-request node type's hub configuration.</param>
    public static MessageHubConfiguration AddPullRequestViews(this MessageHubConfiguration configuration)
        => configuration
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithNodePage(OverviewArea, Overview));

    /// <summary>The draft's page: the editor with its Submit button, the activity progress and the result notice.</summary>
    /// <param name="host">The layout area host (addressed at the draft node).</param>
    /// <param name="context">The rendering context.</param>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext context)
    {
        var prPath = host.Hub.Address.ToString();
        var spacePath = prPath.Split('/', 2)[0];
        var userId = host.Hub.ServiceProvider.GetService<AccessService>()?.Context?.ObjectId ?? "";
        // No signed-in identity: Submit and Check status would run under an empty user, so refuse
        // up front with the same hint the Sync tab shows rather than render a button that cannot work.
        if (string.IsNullOrEmpty(userId))
            return Observable.Return<UiControl?>(GitSyncViews.Hint(host.Localize("ui.gitSync.tab.signInToConnect")));
        var prService = host.Hub.ServiceProvider.GetRequiredService<PullRequestService>();
        var locale = host.ViewerLocale();

        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap:12px;")
            .WithView(GitHubSyncSettingsTab.BuildPullRequestEditor(
                prService, spacePath, prPath, userId, locale, ActivityPathId, ResultId))
            .WithView((h, _) => h.Stream.GetDataStream<string>(ActivityPathId)
                .Select(path => (UiControl?)GitHubSyncSettingsTab.BuildActivityPanel(path))
                .StartWith((UiControl?)Controls.Stack.WithWidth("100%")))
            .WithView((h, _) => h.NoticeView(ResultId));
        return Observable.Return<UiControl?>(stack);
    }
}
