// <meshweaver>
// Id: SocialMediaPostLayoutAreas
// DisplayName: Social Media Post Views
// </meshweaver>

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Web;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Services;

public static class SocialMediaPostLayoutAreas
{
    public const string CalendarArea = "Calendar";
    public const string DetailArea = "Detail";

    public static LayoutDefinition AddSocialMediaPostLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(CalendarArea, Calendar)
            .WithView(DetailArea, Detail);

    private static Dictionary<string, MeshNode> ApplyChanges(
        Dictionary<string, MeshNode> current, QueryResultChange<MeshNode> change)
    {
        var result = change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset
            ? new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MeshNode>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var item in change.Items)
        {
            if (change.ChangeType == QueryChangeType.Removed) result.Remove(item.Path);
            else result[item.Path] = item;
        }
        return result;
    }

    private static string? GetProp(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return null;
        if (json.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        var pascal = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
        return json.TryGetProperty(pascal, out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
    }

    private static int GetInt(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return 0;
        var name = prop;
        if (!json.TryGetProperty(name, out var p))
        {
            name = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
            if (!json.TryGetProperty(name, out p)) return 0;
        }
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : 0;
    }

    private static DateTimeOffset? GetDate(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return null;
        var name = prop;
        if (!json.TryGetProperty(name, out var p))
        {
            name = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
            if (!json.TryGetProperty(name, out p)) return null;
        }
        return p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var dt) ? dt : null;
    }

    private const string FilterMy = "my";
    private const string FilterAll = "all";

    public static IObservable<UiControl?> Calendar(LayoutAreaHost host, RenderingContext _)
    {
        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var hubAddress = host.Hub.Address;
        var currentEmail = host.Hub.ServiceProvider.GetService<AccessService>()?.Context?.Email ?? "";

        var idStr = host.Reference.Id?.ToString() ?? "";
        var monthPart = idStr.Split('?')[0];
        var month = TryParseMonth(monthPart, out var parsed) ? parsed : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var filter = host.Reference.GetParameterValue("profile") ?? FilterMy;

        var posts = meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:SocialMedia/Post"))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);
        var profiles = meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:SocialMedia/Profile"))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return posts.CombineLatest(profiles, (postDict, profileDict) =>
            (UiControl?)BuildCalendar(hubAddress, month, filter, currentEmail, postDict.Values.ToImmutableList(), profileDict.Values.ToImmutableList()));
    }

    private static UiControl BuildCalendar(
        object hubAddress, DateTime month, string filter, string currentEmail,
        ImmutableList<MeshNode> allPosts, ImmutableList<MeshNode> allProfiles)
    {
        var profilesByPath = allProfiles.ToImmutableDictionary(p => p.Path, p => p, StringComparer.OrdinalIgnoreCase);
        var myProfilePaths = allProfiles
            .Where(p => string.Equals(GetProp(p, "owner"), currentEmail, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Path)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        bool MatchesFilter(MeshNode post)
        {
            var profilePath = GetProp(post, "profilePath") ?? "";
            return filter switch
            {
                FilterAll => true,
                FilterMy => myProfilePaths.Contains(profilePath),
                _ => string.Equals(profilePath, filter, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(profilePath.Split('/').Last(), filter, StringComparison.OrdinalIgnoreCase)
            };
        }

        var monthPosts = allPosts
            .Where(p => GetDate(p, "scheduledAt") is { } d && d.Year == month.Year && d.Month == month.Month)
            .Where(MatchesFilter)
            .OrderBy(p => GetDate(p, "scheduledAt"))
            .ToImmutableList();

        var prev = month.AddMonths(-1);
        var next = month.AddMonths(1);
        var prevHref = BuildHref(hubAddress, prev, filter);
        var nextHref = BuildHref(hubAddress, next, filter);

        var toolbar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 12px; flex-wrap: wrap; padding: 8px 0;")
            .WithView(Controls.Button("\u2039")
                .WithAppearance(Appearance.Outline)
                .WithNavigateToHref(prevHref))
            .WithView(Controls.Html($"<h2 style=\"margin:0;min-width:180px;text-align:center;\">{HttpUtility.HtmlEncode(month.ToString("MMMM yyyy", CultureInfo.InvariantCulture))}</h2>"))
            .WithView(Controls.Button("\u203a")
                .WithAppearance(Appearance.Outline)
                .WithNavigateToHref(nextHref))
            .WithView(Controls.Html("<div style=\"width:24px;\"></div>"))
            .WithView(FilterButton(hubAddress, month, "My profiles", FilterMy, filter))
            .WithView(FilterButton(hubAddress, month, "All", FilterAll, filter));

        foreach (var profile in allProfiles.OrderBy(p => p.Name))
        {
            var label = profile.Name ?? profile.Path;
            toolbar = toolbar.WithView(FilterButton(hubAddress, month, label, profile.Path, filter));
        }

        var grid = Controls.Html(BuildMonthGridHtml(month, monthPosts, profilesByPath));

        var emptyHint = monthPosts.Count == 0
            ? Controls.Markdown($"*No posts scheduled in {month:MMMM yyyy} for this filter.*")
            : null;

        var stack = Controls.Stack
            .WithStyle("padding: 16px; gap: 12px;")
            .WithView(toolbar)
            .WithView(grid);
        if (emptyHint != null)
            stack = stack.WithView(emptyHint);
        return stack;
    }

    private static ButtonControl FilterButton(object hubAddress, DateTime month, string label, string filterValue, string activeFilter)
    {
        var isActive = string.Equals(filterValue, activeFilter, StringComparison.OrdinalIgnoreCase);
        var btn = Controls.Button(label)
            .WithAppearance(isActive ? Appearance.Accent : Appearance.Stealth)
            .WithNavigateToHref(BuildHref(hubAddress, month, filterValue));
        return btn;
    }

    private static string BuildHref(object hubAddress, DateTime month, string filter)
    {
        var id = $"{month:yyyy-MM}";
        if (!string.Equals(filter, FilterMy, StringComparison.OrdinalIgnoreCase))
            id += $"?profile={Uri.EscapeDataString(filter)}";
        return new LayoutAreaReference(CalendarArea) { Id = id }.ToHref(hubAddress.ToString()!);
    }

    private static string BuildMonthGridHtml(
        DateTime month,
        ImmutableList<MeshNode> monthPosts,
        ImmutableDictionary<string, MeshNode> profilesByPath)
    {
        var firstOfMonth = new DateTime(month.Year, month.Month, 1);
        // Monday = 1, Sunday = 0; we want week to start Monday
        var dayOffset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        var gridStart = firstOfMonth.AddDays(-dayOffset);
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);

        var postsByDay = monthPosts
            .GroupBy(p => GetDate(p, "scheduledAt")!.Value.Date)
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableList());

        var sb = new StringBuilder();
        sb.Append("<div style=\"display:grid;grid-template-columns:repeat(7,1fr);gap:4px;font-family:var(--body-font);\">");

        // Day-of-week header
        string[] dayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        foreach (var d in dayNames)
            sb.Append($"<div style=\"font-weight:600;color:#666;padding:4px 8px;font-size:12px;text-transform:uppercase;\">{d}</div>");

        var today = DateTime.Today;
        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            var isCurrentMonth = date.Month == month.Month && date.Year == month.Year;
            var isToday = date == today;
            var bg = isCurrentMonth ? "#ffffff" : "#f7f7f7";
            var fg = isCurrentMonth ? "#222" : "#aaa";
            var border = isToday ? "2px solid var(--accent-fill-rest, #0a66c2)" : "1px solid #e5e5e5";

            sb.Append($"<div style=\"min-height:96px;background:{bg};border:{border};border-radius:6px;padding:6px;display:flex;flex-direction:column;gap:4px;\">");
            sb.Append($"<div style=\"color:{fg};font-weight:{(isToday ? "700" : "500")};font-size:13px;\">{date.Day}</div>");

            if (postsByDay.TryGetValue(date, out var dayPosts))
            {
                foreach (var post in dayPosts.Take(3))
                {
                    var title = post.Name ?? GetProp(post, "title") ?? "(untitled)";
                    var profilePath = GetProp(post, "profilePath") ?? "";
                    var profile = profilesByPath.GetValueOrDefault(profilePath);
                    var platformId = GetProp(post, "platform") ?? GetProp(profile ?? new MeshNode(""), "platform") ?? "LinkedIn";
                    var platform = Platform.GetById(platformId);
                    var isPublished = GetDate(post, "publishedAt") is not null;
                    var icon = isPublished ? "\u2705" : "\ud83d\udcc5";
                    var href = "/" + post.Path;
                    sb.Append($"<a href=\"{HttpUtility.HtmlAttributeEncode(href)}\" title=\"{HttpUtility.HtmlAttributeEncode(title)}\" style=\"display:block;padding:3px 6px;background:{platform.Color};color:white;border-radius:3px;font-size:11px;text-decoration:none;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;\">{icon} {HttpUtility.HtmlEncode(Truncate(title, 22))}</a>");
                }
                if (dayPosts.Count > 3)
                    sb.Append($"<div style=\"font-size:11px;color:#666;\">+{dayPosts.Count - 3} more</div>");
            }
            sb.Append("</div>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "\u2026";

    private static bool TryParseMonth(string? s, out DateTime month)
    {
        month = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return DateTime.TryParseExact(s, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
    }

    public static IObservable<UiControl?> Detail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();

        var nodeStream = host.Workspace.GetStream<MeshNode>()!
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath));
        var profiles = meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("namespace:SocialMedia/Profile"))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return nodeStream.CombineLatest(profiles, (node, profileDict) =>
        {
            if (node is null) return (UiControl?)Controls.Markdown("*Post not found.*");

            var title = node.Name ?? GetProp(node, "title") ?? "(untitled)";
            var body = GetProp(node, "body");
            var profilePath = GetProp(node, "profilePath") ?? "";
            var profile = profileDict.GetValueOrDefault(profilePath);
            var profileName = profile?.Name ?? profilePath.Split('/').Last();
            var platformId = GetProp(node, "platform") ?? (profile is not null ? GetProp(profile, "platform") : null) ?? "LinkedIn";
            var platform = Platform.GetById(platformId);
            var scheduled = GetDate(node, "scheduledAt");
            var published = GetDate(node, "publishedAt");
            var impressions = GetInt(node, "impressions");
            var likes = GetInt(node, "likes");
            var comments = GetInt(node, "comments");
            var media = GetProp(node, "mediaUrl");
            var status = published.HasValue ? "Published" : (scheduled.HasValue && scheduled.Value > DateTimeOffset.Now ? "Scheduled" : "Draft");
            var statusColor = published.HasValue ? "#2e7d32" : "#ed6c02";

            var headerHtml = $$"""
                <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap;padding:8px 0;">
                  <span style="background:{{platform.Color}};color:white;padding:4px 10px;border-radius:12px;font-size:12px;font-weight:600;">{{platform.Emoji}} {{HttpUtility.HtmlEncode(platform.Name)}}</span>
                  <a href="/{{HttpUtility.HtmlAttributeEncode(profilePath)}}" style="color:#0a66c2;text-decoration:none;">@{{HttpUtility.HtmlEncode(profileName)}}</a>
                  <span style="background:{{statusColor}};color:white;padding:4px 10px;border-radius:12px;font-size:12px;font-weight:600;">{{status}}</span>
                </div>
                """;

            var datesHtml = $$"""
                <table style="border-collapse:collapse;margin:8px 0;font-size:14px;">
                  <tr><td style="color:#666;padding:2px 12px 2px 0;">Scheduled</td><td>{{HttpUtility.HtmlEncode(scheduled?.ToString("yyyy-MM-dd HH:mm") ?? "—")}}</td></tr>
                  <tr><td style="color:#666;padding:2px 12px 2px 0;">Published</td><td>{{HttpUtility.HtmlEncode(published?.ToString("yyyy-MM-dd HH:mm") ?? "—")}}</td></tr>
                </table>
                """;

            var statsHtml = $$"""
                <div style="display:flex;gap:24px;padding:12px;background:#f5f7fa;border-radius:6px;margin:8px 0;">
                  <div><div style="font-size:11px;color:#666;text-transform:uppercase;">Impressions</div><div style="font-size:20px;font-weight:600;">{{impressions:N0}}</div></div>
                  <div><div style="font-size:11px;color:#666;text-transform:uppercase;">Likes</div><div style="font-size:20px;font-weight:600;">{{likes:N0}}</div></div>
                  <div><div style="font-size:11px;color:#666;text-transform:uppercase;">Comments</div><div style="font-size:20px;font-weight:600;">{{comments:N0}}</div></div>
                </div>
                """;

            var mediaHtml = "";
            if (!string.IsNullOrEmpty(media))
            {
                var lower = media.ToLowerInvariant();
                if (lower.EndsWith(".mp4") || lower.EndsWith(".webm") || lower.EndsWith(".mov"))
                    mediaHtml = $"<video src=\"{HttpUtility.HtmlAttributeEncode(media)}\" controls style=\"max-width:100%;border-radius:6px;margin:8px 0;\"></video>";
                else
                    mediaHtml = $"<img src=\"{HttpUtility.HtmlAttributeEncode(media)}\" alt=\"media\" style=\"max-width:100%;border-radius:6px;margin:8px 0;\" />";
            }

            var headerStack = Controls.Stack.WithStyle("padding: 16px; gap: 4px;")
                .WithView(Controls.Html($"<h1 style=\"margin:0;\">{HttpUtility.HtmlEncode(title)}</h1>"))
                .WithView(Controls.Html(headerHtml))
                .WithView(Controls.Html(datesHtml))
                .WithView(Controls.Html(statsHtml));
            if (!string.IsNullOrEmpty(mediaHtml))
                headerStack = headerStack.WithView(Controls.Html(mediaHtml));
            if (!string.IsNullOrWhiteSpace(body))
                headerStack = headerStack.WithView(Controls.Markdown(body));
            headerStack = headerStack.WithView(Controls.Html($"<a href=\"/SocialMedia/Posts\" style=\"color:#0a66c2;text-decoration:none;\">\u2190 Back to calendar</a>"));
            return (UiControl?)headerStack;
        });
    }
}
