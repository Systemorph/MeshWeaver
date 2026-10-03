// <meshweaver>
// Id: SocialMediaProfileLayoutAreas
// DisplayName: Social Media Profile Views
// </meshweaver>

using System;
using System.Linq;
using MeshWeaver.Layout.Composition;

/// <summary>
/// The Profile's view — a TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): built
/// from the node's PATH, emitted whole on the first render, and every value a pointer into the
/// node's <see cref="SocialMediaProfile"/> that the GUI resolves through the node stream. Nothing
/// here reads the node on the hub — and so nothing here has to dig a value out of a
/// <c>JsonElement</c> either.
/// </summary>
public static class SocialMediaProfileLayoutAreas
{
    /// <summary>The area name of the profile's detail view.</summary>
    public const string DetailArea = "Detail";

    /// <summary>Registers the profile views.</summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddSocialMediaProfileLayoutAreas(this LayoutDefinition layout) =>
        layout.WithView(DetailArea, Detail);

    /// <summary>The profile: name, platform, owner, link and bio.</summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="_">The rendering context.</param>
    /// <returns>The detail template.</returns>
    public static UiControl Detail(LayoutAreaHost host, RenderingContext _)
        => DetailTemplate(host.Hub.Address.ToString());

    /// <summary>The profile at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The profile node.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl DetailTemplate(string nodePath)
    {
        var content = LayoutAreaReference.GetMeshNodeDataContext(nodePath);
        return Controls.Stack
            .WithStyle("padding: 16px; gap: 8px;")
            .WithView(Controls.H2(new JsonPointerReference("name")) with
            {
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false)
            })
            .WithView(Controls.Badge(new JsonPointerReference("platform")) with { DataContext = content })
            .WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 8px;")
                .WithView(Controls.Label("Owner:").WithStyle("color: var(--neutral-foreground-hint);"))
                .WithView(Controls.Label(new JsonPointerReference("owner")) with { DataContext = content }))
            // A bare URL renders as a link in markdown.
            .WithView(Controls.Markdown(new JsonPointerReference("profileUrl")) with { DataContext = content })
            .WithView(Controls.Markdown(new JsonPointerReference("bio")) with { DataContext = content });
    }
}

/// <summary>
/// Display metadata for a social platform — id, label, emoji, brand color. Replaces the
/// previously-missing <c>Platform</c> dimension type the sample referenced (the compile break).
/// </summary>
public sealed record Platform(string Id, string Name, string Emoji, string Color)
{
    public static readonly Platform[] All =
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
