// <meshweaver>
// Id: SocialMediaProfileLayoutAreas
// DisplayName: Social Media Profile Views
// </meshweaver>

using System;
using System.Linq;
using System.Text.Json;
using System.Web;
using MeshWeaver.Layout.Composition;

public static class SocialMediaProfileLayoutAreas
{
    public const string DetailArea = "Detail";

    public static LayoutDefinition AddSocialMediaProfileLayoutAreas(this LayoutDefinition layout) =>
        layout.WithView(DetailArea, Detail);

    private static string? GetProp(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return null;
        if (json.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        var pascal = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
        return json.TryGetProperty(pascal, out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
    }

    public static IObservable<UiControl?> Detail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.Workspace.GetStream<MeshNode>()!
            .Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath))
            .Select(node =>
            {
                if (node is null)
                    return (UiControl?)Controls.Markdown("*Profile not found.*");

                var name = node.Name ?? GetProp(node, "name") ?? "Profile";
                var platformId = GetProp(node, "platform") ?? "LinkedIn";
                var platform = Platform.GetById(platformId);
                var owner = GetProp(node, "owner") ?? "";
                var profileUrl = GetProp(node, "profileUrl");
                var bio = GetProp(node, "bio");

                var link = !string.IsNullOrEmpty(profileUrl)
                    ? $"<a href=\"{HttpUtility.HtmlAttributeEncode(profileUrl)}\" target=\"_blank\" rel=\"noopener\">Open profile \u2197</a>"
                    : "<span style=\"color:#888;\">No profile URL</span>";

                var html = $$"""
                    <div style="display:flex;gap:24px;align-items:center;padding:16px;">
                      <div style="width:96px;height:96px;border-radius:50%;background:{{platform.Color}};color:white;display:flex;align-items:center;justify-content:center;font-size:36px;">{{platform.Emoji}}</div>
                      <div>
                        <h2 style="margin:0 0 4px 0;">{{HttpUtility.HtmlEncode(name)}}</h2>
                        <div style="color:{{platform.Color}};font-weight:600;margin-bottom:4px;">{{platform.Emoji}} {{HttpUtility.HtmlEncode(platform.Name)}}</div>
                        <div style="color:#666;margin-bottom:8px;">Owner: {{HttpUtility.HtmlEncode(owner)}}</div>
                        <div>{{link}}</div>
                      </div>
                    </div>
                    """;

                var stack = Controls.Stack
                    .WithStyle("padding: 16px;")
                    .WithView(Controls.Html(html));
                if (!string.IsNullOrWhiteSpace(bio))
                    stack = stack.WithView(Controls.Markdown(bio));
                return (UiControl?)stack;
            });
    }
}

/// <summary>
/// Display metadata for a social platform — id, label, emoji, brand color. Replaces the
/// previously-missing <c>Platform</c> dimension type the sample referenced (the compile break).
/// </summary>
public sealed record Platform(string Id, string Name, string Emoji, string Color)
{
    private static readonly Platform[] All =
    {
        new("LinkedIn", "LinkedIn", "💼", "#0A66C2"),
        new("Twitter", "Twitter / X", "🐦", "#1DA1F2"),
        new("GitHub", "GitHub", "🐙", "#181717"),
        new("YouTube", "YouTube", "▶", "#FF0000"),
        new("Instagram", "Instagram", "📷", "#E4405F"),
    };

    public static Platform GetById(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? new(id ?? "Unknown", id ?? "Unknown", "🌐", "#888888");
}
