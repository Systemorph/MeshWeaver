// <meshweaver>
// Id: SocialMediaProfileLayoutAreas
// DisplayName: Social Media Profile Views
// </meshweaver>

using System.Text.Json;
using System.Web;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Services;

public static class SocialMediaProfileLayoutAreas
{
    public static LayoutDefinition AddSocialMediaProfileLayoutAreas(this LayoutDefinition layout) =>
        layout.WithView("Detail", Detail);

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
        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();

        return host.Workspace.GetStream<MeshNode>()!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                if (node is null) return (UiControl?)Controls.Markdown("*Profile not found.*");

                var name = node.Name ?? GetProp(node, "name") ?? "Profile";
                var platformId = GetProp(node, "platform") ?? "LinkedIn";
                var platform = Platform.GetById(platformId);
                var owner = GetProp(node, "owner") ?? "";
                var profileUrl = GetProp(node, "profileUrl");
                var avatarUrl = GetProp(node, "avatarUrl");
                var bio = GetProp(node, "bio");

                var avatar = !string.IsNullOrEmpty(avatarUrl)
                    ? $"<img src=\"{HttpUtility.HtmlAttributeEncode(avatarUrl)}\" alt=\"avatar\" style=\"width:96px;height:96px;border-radius:50%;object-fit:cover;\" />"
                    : $"<div style=\"width:96px;height:96px;border-radius:50%;background:{platform.Color};color:white;display:flex;align-items:center;justify-content:center;font-size:36px;\">{platform.Emoji}</div>";

                var link = !string.IsNullOrEmpty(profileUrl)
                    ? $"<a href=\"{HttpUtility.HtmlAttributeEncode(profileUrl)}\" target=\"_blank\" rel=\"noopener\">Open profile \u2197</a>"
                    : "<span style=\"color:#888;\">No profile URL</span>";

                var html = $$"""
                    <div style="display:flex;gap:24px;align-items:center;padding:16px;">
                      {{avatar}}
                      <div>
                        <h2 style="margin:0 0 4px 0;">{{HttpUtility.HtmlEncode(name)}}</h2>
                        <div style="color:{{platform.Color}};font-weight:600;margin-bottom:4px;">{{platform.Emoji}} {{HttpUtility.HtmlEncode(platform.Name)}}</div>
                        <div style="color:#666;margin-bottom:8px;">Owner: {{HttpUtility.HtmlEncode(owner)}}</div>
                        <div>{{link}}</div>
                      </div>
                    </div>
                    """;

                var stack = Controls.Stack.WithStyle("padding: 16px;")
                    .WithView(Controls.Html(html));
                if (!string.IsNullOrEmpty(bio))
                    stack = stack.WithView(Controls.Markdown(bio));
                return (UiControl?)stack;
            });
    }
}
