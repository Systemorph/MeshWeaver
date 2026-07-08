using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The action layout area the "GitHub" node menu items navigate to — runs one one-click GitHub
/// operation (Sync now / Update to latest / Check branch) on one sync source and renders a
/// confirmation. Menu items carry no click delegate; they navigate to
/// <c>/{node}/GitHubAction?source={id}&amp;op={op}</c> and this area starts the activity via the
/// existing <see cref="GitHubActivityExtensions"/> — identical to the settings-tab buttons. The
/// activity's live progress surfaces through the node's activity/notification stream. Input-driven
/// operations (re-import at a specific commit, the PR draft/submit workflow) stay in the settings tab.
/// </summary>
public static class GitHubActionArea
{
    /// <summary>Area name for the GitHub one-click action.</summary>
    public const string AreaName = "GitHubAction";

    /// <summary>Query parameter carrying the sync-source id (empty = the primary source).</summary>
    public const string SourceParam = "source";

    /// <summary>Query parameter carrying the operation.</summary>
    public const string OpParam = "op";

    /// <summary>Op token: commit the Space to GitHub.</summary>
    public const string Commit = "commit";

    /// <summary>Op token: update the Space to the branch HEAD.</summary>
    public const string Update = "update";

    /// <summary>Op token: check whether the Space is up to date with the branch.</summary>
    public const string Check = "check";

    /// <summary>Builds the href a menu item uses to invoke <paramref name="op"/> on <paramref name="sourceId"/>.</summary>
    public static string Href(string hubPath, string? sourceId, string op)
        => MeshNodeLayoutAreas.BuildUrl(hubPath, AreaName,
            $"{SourceParam}={Uri.EscapeDataString(sourceId ?? "")}&{OpParam}={op}");

    /// <summary>Runs the op addressed by the area's query params and renders a confirmation.</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var spacePath = hubPath.Split('/', 2)[0];   // GitHub sync acts on the containing Space (top segment)
        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(GitHubActionArea));

        var op = host.Reference.GetParameterValue(OpParam);
        var rawSource = host.Reference.GetParameterValue(SourceParam);
        var sourceId = string.IsNullOrEmpty(rawSource) ? null : rawSource;
        var userId = host.Hub.ServiceProvider.GetService<AccessService>()?.Context?.ObjectId ?? "";

        if (string.IsNullOrEmpty(spacePath))
            return Observable.Return<UiControl?>(Message(
                "Not in a Space", "GitHub actions run on a Space; this node has no containing Space.", backHref));
        if (string.IsNullOrEmpty(op))
            return Observable.Return<UiControl?>(Message("Nothing to do", "No operation specified.", backHref));
        if (string.IsNullOrEmpty(userId))
            return Observable.Return<UiControl?>(Message(
                "Sign-in required", "Could not resolve your identity for the GitHub operation.", backHref));

        // The op extension calls onActivityCreated(activityPath) the moment the
        // Activity node is created. Capture it in a ReplaySubject so we can render
        // the LIVE activity view as soon as the path is known (and replay it to any
        // late render subscription). This subject is per-render — NOT static — so it
        // dies with the render and holds no cross-request state.
        var pathSubject = new ReplaySubject<string>(1);
        void OnCreated(string p) => pathSubject.OnNext(p);

        var (title, action) = op switch
        {
            Commit => ("Committing to GitHub",
                host.Hub.CommitToGitHub(spacePath, userId, OnCreated, sourceId)),
            Update => ("Updating from GitHub",
                host.Hub.UpdateToLatestFromGitHub(spacePath, userId, OnCreated, sourceId)),
            Check => ("Checking branch state",
                host.Hub.CheckBranchStateOnGitHub(spacePath, userId, OnCreated, sourceId)),
            _ => (null, (IObservable<string>?)null),
        };
        if (action is null)   // unknown op — don't claim an activity started
            return Observable.Return<UiControl?>(Message(
                "Unknown operation", $"Unrecognized GitHub operation <code>{System.Net.WebUtility.HtmlEncode(op)}</code>.", backHref));

        // Fire the work. The activity node it creates streams its own live progress.
        action.Subscribe(_ => { }, ex => logger?.LogWarning(ex, "GitHub action {Op} failed for {Space}", op, spacePath));

        // Render the live activity Overview as soon as the activity path arrives;
        // until then show a "Starting…" placeholder.
        return pathSubject
            .Select(path => (UiControl?)BuildLiveView(title!, path, backHref))
            .StartWith((UiControl?)BuildStarting(title!, backHref));
    }

    /// <summary>Back button + title + a "Starting…" indeterminate progress, shown until the activity path is known.</summary>
    private static UiControl BuildStarting(string title, string backHref)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 12px;")
            .WithView(BuildHeader(title, backHref))
            .WithView(Controls.Progress("Starting…", null!).WithWidth("100%").WithHideNumber(true));

    /// <summary>Back button + title + the live activity Overview embed (live progress + message log).</summary>
    private static UiControl BuildLiveView(string title, string activityPath, string backHref)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 12px;")
            .WithView(BuildHeader(title, backHref))
            .WithView(new LayoutAreaControl(
                new Address(activityPath),
                new LayoutAreaReference(ActivityLayoutAreas.OverviewArea)));

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

    // Early-return guard message (Not in a Space / Nothing to do / Sign-in required /
    // Unknown operation). The body may contain a pre-rendered <code> snippet, so it
    // stays a Controls.Html paragraph; the header is the shared control-based row.
    private static UiControl Message(string title, string bodyHtml, string backHref)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; gap: 16px;")
            .WithView(BuildHeader(title, backHref))
            .WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{bodyHtml}</p>"));
}
