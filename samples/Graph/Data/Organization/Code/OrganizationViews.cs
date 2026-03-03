// <meshweaver>
// Id: OrganizationViews
// DisplayName: Organization Views
// </meshweaver>

using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

/// <summary>
/// Custom views for Organization nodes.
/// </summary>
public static class OrganizationViews
{
    /// <summary>
    /// GitHub-style organization header view with standard children section.
    /// Shows logo, name, description, verified badge, contact info, then delegates to standard view for children.
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

        return orgStream.CombineLatest(nodeStream, (org, node) =>
        {
            if (org == null && node == null)
                return Controls.Markdown("*Loading...*") as UiControl;

            return BuildOrganizationView(host, org, node, hubPath);
        });
    }

    private static UiControl BuildOrganizationView(
        LayoutAreaHost host,
        Organization? org,
        MeshNode? node,
        string hubPath)
    {
        var name = org?.Name ?? node?.Name ?? "Organization";
        var description = org?.Description;
        var logo = org?.Logo ?? GetNodeLogo(node);
        var website = org?.Website;
        var location = org?.Location;
        var email = org?.Email;
        var isVerified = org?.IsVerified ?? false;

        var container = Controls.Stack
            .WithStyle("padding: 24px 0; width: 100%;");

        // Main header row: logo + info + menu (menu on far right)
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 24px; align-items: flex-start; width: 100%; max-width: 1280px; margin: 0 auto; padding: 0 24px;");

        // Logo (large, rounded square like GitHub)
        if (!string.IsNullOrEmpty(logo))
        {
            headerRow = headerRow.WithView(Controls.Html(
                $"<img src=\"{logo}\" alt=\"\" style=\"width: 100px; height: 100px; border-radius: 12px; object-fit: cover; background: var(--neutral-layer-2);\" />"));
        }
        else
        {
            // Placeholder image with initials
            var initials = GetInitials(name);
            headerRow = headerRow.WithView(Controls.Html(
                $"<div style=\"width: 100px; height: 100px; border-radius: 12px; background: var(--accent-fill-rest); display: flex; align-items: center; justify-content: center; color: white; font-size: 2.5rem; font-weight: 600;\">" +
                $"{System.Web.HttpUtility.HtmlEncode(initials)}</div>"));
        }

        // Info column (flex: 1 to take remaining space)
        var infoColumn = Controls.Stack.WithStyle("gap: 8px; flex: 1;");

        // Organization name (large)
        infoColumn = infoColumn.WithView(Controls.Html(
            $"<h1 style=\"margin: 0; font-size: 2rem; font-weight: 600;\">{System.Web.HttpUtility.HtmlEncode(name)}</h1>"));

        // Description/tagline (rendered as markdown for rich formatting)
        if (!string.IsNullOrEmpty(description))
        {
            infoColumn = infoColumn.WithView(
                Controls.Markdown(description).WithStyle("color: var(--neutral-foreground-hint); font-size: 1rem;"));
        }

        // Verified badge
        if (isVerified)
        {
            infoColumn = infoColumn.WithView(Controls.Html(
                "<span style=\"display: inline-flex; align-items: center; gap: 4px; padding: 2px 8px; border-radius: 12px; border: 1px solid #3fb950; color: #3fb950; font-size: 0.75rem; font-weight: 500;\">" +
                "<svg width=\"12\" height=\"12\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M13.78 4.22a.75.75 0 010 1.06l-7.25 7.25a.75.75 0 01-1.06 0L2.22 9.28a.75.75 0 111.06-1.06L6 10.94l6.72-6.72a.75.75 0 011.06 0z\"/></svg>" +
                "Verified</span>"));
        }

        // Stats row: location, website, email
        var statsRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 24px; margin-top: 16px; flex-wrap: wrap;");

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
            "<hr style=\"border: none; border-top: 1px solid var(--neutral-stroke-rest); margin: 24px 0;\" />"));

        // Markdown body from index.md — PreRenderedHtml is set by MarkdownFileParser
        // for any .md file; MarkdownView handles mermaid, code blocks, math, UCR links
        if (!string.IsNullOrWhiteSpace(node?.PreRenderedHtml))
        {
            container = container.WithView(
                new MarkdownControl("") { Html = node.PreRenderedHtml }
                    .WithStyle("max-width: 1280px; margin: 0 auto; padding: 0 24px 48px 24px;"));
        }

        // Use LayoutAreaControl to render the standard Catalog view for children
        container = container.WithView(
            LayoutAreaControl.Children(host.Hub));

        return container;
    }

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
