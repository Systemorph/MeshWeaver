using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

namespace Memex.Portal.Shared;

/// <summary>
/// Custom views for Organization nodes.
/// </summary>
public static class OrganizationLayoutAreas
{
    private const string ThinScrollbar = "scrollbar-width: thin; scrollbar-color: rgba(128,128,128,0.3) transparent;";
    private const string ContentMaxWidth = "max-width: 1280px; margin: 0 auto; padding: 0 24px;";

    /// <summary>
    /// GitHub-style organization header view with live dashboard below.
    /// Shows logo, name, description, stats, then a set of MeshSearch sections scoped
    /// to the organization's own partition, and a chat input inviting content creation.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var orgStream = host.Workspace.GetStream<Organization>()
            ?.Select(orgs => orgs?.FirstOrDefault())
            ?? Observable.Return<Organization?>(null);

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath))
            ?? Observable.Return<MeshNode?>(null);

        return orgStream.CombineLatest(nodeStream).Select(t =>
        {
            var (org, node) = t;
            if (org == null && node == null)
                return Controls.Markdown("*Loading...*") as UiControl;

            return BuildOrganizationView(host, org, node);
        });
    }

    private static UiControl BuildOrganizationView(
        LayoutAreaHost host,
        Organization? org,
        MeshNode? node)
    {
        var orgPath = node?.Path ?? host.Hub.Address.ToString();
        var orgName = org?.Name ?? node?.Name ?? orgPath;

        var shell = Controls.Stack
            .WithWidth("100%")
            .WithStyle($"height: 100%; overflow-y: auto; {ThinScrollbar}");

        shell = shell.WithView(BuildHeader(org, node, orgName));
        shell = shell.WithView(BuildBodyContent(org, node));

        if (IsSystemorph(orgPath))
            shell = shell.WithView(BuildSystemorphHighlights(orgPath));
        else
            shell = shell.WithView(BuildDashboardGrid(orgPath));

        return shell;
    }

    /// <summary>
    /// Logo + name + description + stats row. GitHub-style org header, fixed at the top.
    /// </summary>
    private static UiControl BuildHeader(Organization? org, MeshNode? node, string orgName)
    {
        var description = org?.Description;
        var logo = org?.Logo ?? GetNodeLogo(node);
        var website = org?.Website;
        var location = org?.Location;
        var email = org?.Email;
        var isVerified = org?.IsVerified ?? false;

        var container = Controls.Stack
            .WithStyle("flex-shrink: 0; padding: 24px 0 16px 0; width: 100%;");

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle($"gap: 24px; align-items: flex-start; width: 100%; {ContentMaxWidth}");

        // Logo (large, rounded square like GitHub)
        UiControl logoControl;
        if (!string.IsNullOrEmpty(logo))
        {
            logoControl = Controls.Html(
                $"<img src=\"{logo}\" alt=\"\" style=\"width: 100px; height: 100px; border-radius: 12px; object-fit: cover; background: var(--neutral-layer-2);\" />");
        }
        else
        {
            var initials = GetInitials(orgName);
            logoControl = Controls.Html(
                $"<div style=\"width: 100px; height: 100px; border-radius: 12px; background: var(--accent-fill-rest); display: flex; align-items: center; justify-content: center; color: white; font-size: 2.5rem; font-weight: 600;\">" +
                $"{System.Web.HttpUtility.HtmlEncode(initials)}</div>");
        }

        headerRow = headerRow.WithView(logoControl);

        var infoColumn = Controls.Stack.WithStyle("gap: 8px; flex: 1;");

        infoColumn = infoColumn.WithView(Controls.Html(
            $"<h1 style=\"margin: 0; font-size: 2rem; font-weight: 600;\">{System.Web.HttpUtility.HtmlEncode(orgName)}</h1>"));

        if (!string.IsNullOrEmpty(description))
        {
            infoColumn = infoColumn.WithView(
                Controls.Markdown(description).WithStyle("color: var(--neutral-foreground-hint); font-size: 1rem;"));
        }

        if (isVerified)
        {
            infoColumn = infoColumn.WithView(Controls.Html(
                "<span style=\"display: inline-flex; align-items: center; gap: 4px; padding: 2px 8px; border-radius: 12px; border: 1px solid #3fb950; color: #3fb950; font-size: 0.75rem; font-weight: 500; width: fit-content;\">" +
                "<svg width=\"12\" height=\"12\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M13.78 4.22a.75.75 0 010 1.06l-7.25 7.25a.75.75 0 01-1.06 0L2.22 9.28a.75.75 0 111.06-1.06L6 10.94l6.72-6.72a.75.75 0 011.06 0z\"/></svg>" +
                "Verified</span>"));
        }

        var statsRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 24px; margin-top: 12px; flex-wrap: wrap;");

        if (!string.IsNullOrEmpty(location))
        {
            statsRow = statsRow.WithView(Controls.Html(
                $"<span style=\"display: inline-flex; align-items: center; gap: 6px; color: var(--neutral-foreground-hint);\">" +
                $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M8 0a5 5 0 0 0-5 5c0 4.17 4.42 10.22 4.62 10.48a.5.5 0 0 0 .76 0C8.58 15.22 13 9.17 13 5a5 5 0 0 0-5-5Zm0 7.5a2.5 2.5 0 1 1 0-5 2.5 2.5 0 0 1 0 5Z\"/></svg>" +
                $"{System.Web.HttpUtility.HtmlEncode(location)}</span>"));
        }

        if (!string.IsNullOrEmpty(website))
        {
            var displayUrl = website.Replace("https://", "").Replace("http://", "").TrimEnd('/');
            statsRow = statsRow.WithView(Controls.Html(
                $"<a href=\"{System.Web.HttpUtility.HtmlAttributeEncode(website)}\" target=\"_blank\" style=\"display: inline-flex; align-items: center; gap: 6px; color: var(--accent-foreground-rest); text-decoration: none;\">" +
                $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M7.775 3.275a.75.75 0 0 0 1.06 1.06l1.25-1.25a2 2 0 1 1 2.83 2.83l-2.5 2.5a2 2 0 0 1-2.83 0 .75.75 0 0 0-1.06 1.06 3.5 3.5 0 0 0 4.95 0l2.5-2.5a3.5 3.5 0 0 0-4.95-4.95l-1.25 1.25Zm-.025 5.368-1.25 1.25a2 2 0 0 1-2.83-2.83l2.5-2.5a2 2 0 0 1 2.83 0 .75.75 0 0 0 1.06-1.06 3.5 3.5 0 0 0-4.95 0l-2.5 2.5a3.5 3.5 0 1 0 4.95 4.95l1.25-1.25a.75.75 0 0 0-1.06-1.06Z\"/></svg>" +
                $"{System.Web.HttpUtility.HtmlEncode(displayUrl)}</a>"));
        }

        if (!string.IsNullOrEmpty(email))
        {
            statsRow = statsRow.WithView(Controls.Html(
                $"<a href=\"mailto:{System.Web.HttpUtility.HtmlAttributeEncode(email)}\" style=\"display: inline-flex; align-items: center; gap: 6px; color: var(--neutral-foreground-hint); text-decoration: none;\">" +
                $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M1.75 2h12.5c.966 0 1.75.784 1.75 1.75v8.5A1.75 1.75 0 0 1 14.25 14H1.75A1.75 1.75 0 0 1 0 12.25v-8.5C0 2.784.784 2 1.75 2ZM1.5 12.25c0 .138.112.25.25.25h12.5a.25.25 0 0 0 .25-.25V5.809L8.38 9.397a.75.75 0 0 1-.76 0L1.5 5.809v6.441Zm13-8.181v-.319a.25.25 0 0 0-.25-.25H1.75a.25.25 0 0 0-.25.25v.319l6.5 3.98 6.5-3.98Z\"/></svg>" +
                $"{System.Web.HttpUtility.HtmlEncode(email)}</a>"));
        }

        infoColumn = infoColumn.WithView(statsRow);
        headerRow = headerRow.WithView(infoColumn);

        container = container.WithView(headerRow);

        // Divider
        container = container.WithView(Controls.Html(
            $"<div style=\"{ContentMaxWidth}\"><hr style=\"border: none; border-top: 1px solid var(--neutral-stroke-rest); margin: 16px 0 0 0;\" /></div>"));

        return container;
    }

    /// <summary>
    /// Body content — priority: node.PreRenderedHtml → org.Body → default welcome markdown.
    /// </summary>
    private static UiControl BuildBodyContent(Organization? org, MeshNode? node)
    {
        var bodyStyle = $"{ContentMaxWidth} padding-top: 24px; padding-bottom: 8px;";

        if (!string.IsNullOrWhiteSpace(node?.PreRenderedHtml))
            return new MarkdownControl("") { Html = node.PreRenderedHtml }.WithStyle(bodyStyle);

        if (!string.IsNullOrWhiteSpace(org?.Body))
            return Controls.Markdown(org!.Body!).WithStyle(bodyStyle);

        return Controls.Markdown(OrganizationNodeType.WelcomeMarkdown).WithStyle(bodyStyle);
    }

    /// <summary>
    /// Dashboard grid mirroring the UserActivity layout but scoped to this organization's partition:
    /// Latest Threads, Items, Activity Feed.
    /// </summary>
    private static UiControl BuildDashboardGrid(string orgPath)
    {
        var grid = Controls.LayoutGrid
            .WithStyle($"{ContentMaxWidth} padding-top: 24px; padding-bottom: 24px; gap: 24px; width: 100%;");

        // Latest Threads — full width
        grid = grid.WithView(BuildLatestThreads(orgPath), skin => skin.WithXs(12));

        // Items in this organization — full width, grouped by type
        grid = grid.WithView(BuildItems(orgPath), skin => skin.WithXs(12));

        // Activity feed — 2/3 width on desktop
        grid = grid.WithView(BuildActivityFeed(orgPath), skin => skin.WithXs(12).WithSm(8));

        // Recently updated main content — 1/3 width on desktop
        grid = grid.WithView(BuildRecentUpdates(orgPath), skin => skin.WithXs(12).WithSm(4));

        return grid;
    }

    /// <summary>
    /// Systemorph-specific highlight strip — Featured Stories grid, embedded Event Calendar,
    /// and a Post Pipeline of Social Media posts. Each section uses rich Thumbnail layout areas
    /// where available so cards have visual punch instead of plain icon+name rows.
    /// </summary>
    private static UiControl BuildSystemorphHighlights(string orgPath)
    {
        var stack = Controls.Stack
            .WithStyle($"{ContentMaxWidth} padding-top: 24px; padding-bottom: 24px; gap: 32px; width: 100%;");

        stack = stack.WithView(BuildFeaturedStories(orgPath));
        stack = stack.WithView(BuildEventCalendar(orgPath));
        stack = stack.WithView(BuildPostShowcase(orgPath));

        return stack;
    }

    /// <summary>
    /// Featured Marketing Stories — Markdown children of the Story series hub at {orgPath}/Story.
    /// </summary>
    private static UiControl BuildFeaturedStories(string orgPath)
    {
        var heading = Controls.Html(
            $"<div style=\"display:flex;align-items:baseline;justify-content:space-between;gap:12px;padding-bottom:8px;border-bottom:1px solid var(--neutral-stroke-rest);\">" +
            $"<h2 style=\"margin:0;font-size:1.35rem;\">✦ Featured Stories</h2>" +
            $"<a href=\"/{orgPath}/Story\" style=\"color:var(--accent-foreground-rest);text-decoration:none;font-size:0.9rem;\">See all →</a>" +
            $"</div>");

        var grid = Controls.MeshSearch
            .WithHiddenQuery($"namespace:{orgPath}/Story scope:children nodeType:Markdown sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(3)
            .WithItemLimit(6)
            .WithMaxRows(2)
            .WithReactiveMode(true);

        return Controls.Stack
            .WithStyle("gap: 12px; width: 100%;")
            .WithView(heading)
            .WithView(grid);
    }

    /// <summary>
    /// Embed the existing EventCalendar Overview from {orgPath}/Events so the month grid
    /// shows inline on the organization page. Single source of truth — same widget the
    /// dedicated calendar page uses.
    /// </summary>
    private static UiControl BuildEventCalendar(string orgPath)
    {
        var eventsPath = $"{orgPath}/Events";

        var heading = Controls.Html(
            $"<div style=\"display:flex;align-items:baseline;justify-content:space-between;gap:12px;padding-bottom:8px;border-bottom:1px solid var(--neutral-stroke-rest);\">" +
            $"<h2 style=\"margin:0;font-size:1.35rem;\">📅 Upcoming Events</h2>" +
            $"<a href=\"/{eventsPath}\" style=\"color:var(--accent-foreground-rest);text-decoration:none;font-size:0.9rem;\">Open calendar →</a>" +
            $"</div>");

        var calendar = new LayoutAreaControl(eventsPath, new LayoutAreaReference("Overview"))
            .WithShowProgress(false)
            .WithStyle("width: 100%;");

        return Controls.Stack
            .WithStyle("gap: 12px; width: 100%;")
            .WithView(heading)
            .WithView(calendar);
    }

    /// <summary>
    /// Social Media post pipeline — all Posts under {orgPath}/SocialMedia rendered as
    /// LinkedIn-style preview cards (PostThumbnail layout area). Status pills on each card
    /// distinguish Draft / Scheduled / Published at a glance.
    /// </summary>
    private static UiControl BuildPostShowcase(string orgPath)
    {
        var socialPath = $"{orgPath}/SocialMedia";

        var heading = Controls.Html(
            $"<div style=\"display:flex;align-items:baseline;justify-content:space-between;gap:12px;padding-bottom:8px;border-bottom:1px solid var(--neutral-stroke-rest);\">" +
            $"<h2 style=\"margin:0;font-size:1.35rem;\">📱 Social Media</h2>" +
            $"<a href=\"/{socialPath}\" style=\"color:var(--accent-foreground-rest);text-decoration:none;font-size:0.9rem;\">See all →</a>" +
            $"</div>");

        var posts = Controls.MeshSearch
            .WithHiddenQuery($"namespace:{socialPath} nodeType:{orgPath}/Post scope:subtree sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemArea("Thumbnail")
            .WithMaxColumns(2)
            .WithItemLimit(12)
            .WithMaxRows(6)
            .WithReactiveMode(true)
            .WithCreateNodeType($"{orgPath}/Post")
            .WithCreateNamespace(socialPath);

        return Controls.Stack
            .WithStyle("gap: 12px; width: 100%;")
            .WithView(heading)
            .WithView(posts);
    }

    /// <summary>
    /// Threads created against this organization or its descendants.
    /// </summary>
    private static UiControl BuildLatestThreads(string orgPath)
    {
        return Controls.MeshSearch
            .WithTitle("Latest Threads")
            .WithHiddenQuery($"nodeType:Thread namespace:{orgPath}/*/_Thread sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemLimit(40)
            .WithMaxRows(2)
            .WithMaxColumns(4)
            .WithReactiveMode(true)
            .WithCreateNodeType("Thread")
            .WithCreateNamespace(orgPath);
    }

    /// <summary>
    /// Child content of the organization, grouped by node type. Mirrors the standard catalog view
    /// but with a create-page affordance so empty organizations invite content creation.
    /// </summary>
    private static UiControl BuildItems(string orgPath)
    {
        return Controls.MeshSearch
            .WithTitle("Content")
            .WithHiddenQuery($"namespace:{orgPath} is:main context:search scope:descendants sort:LastModified-desc")
            .WithShowSearchBox(true)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            .WithSectionCounts(true)
            .WithItemLimit(60)
            .WithMaxRows(3)
            .WithMaxColumns(4)
            .WithCollapsibleSections(true)
            .WithReactiveMode(true)
            .WithCreateHref($"/create?type=Markdown&namespace={Uri.EscapeDataString(orgPath)}");
    }

    /// <summary>
    /// Activity timeline scoped to this organization — recent edits, comments, threads.
    /// </summary>
    private static UiControl BuildActivityFeed(string orgPath)
    {
        return Controls.MeshSearch
            .WithTitle("Activity Feed")
            .WithHiddenQuery($"source:activity namespace:{orgPath} scope:subtree is:main sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(2)
            .WithItemLimit(40)
            .WithMaxRows(4)
            .WithReactiveMode(true);
    }

    /// <summary>
    /// Recently updated main content in the organization — compact sidebar column.
    /// </summary>
    private static UiControl BuildRecentUpdates(string orgPath)
    {
        return Controls.MeshSearch
            .WithTitle("Recently Updated")
            .WithHiddenQuery($"namespace:{orgPath} is:main scope:subtree sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(1)
            .WithItemLimit(20)
            .WithMaxRows(4)
            .WithReactiveMode(true);
    }

    private static bool IsSystemorph(string orgPath) =>
        string.Equals(orgPath, "Systemorph", StringComparison.OrdinalIgnoreCase);

    private static string? GetNodeLogo(MeshNode? node)
    {
        return MeshNodeThumbnailControl.GetImageUrlForNode(node);
    }

    private static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
        return name.Length >= 2 ? $"{char.ToUpper(name[0])}{char.ToUpper(name[1])}" : char.ToUpper(name[0]).ToString();
    }
}
