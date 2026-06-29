using System.Collections.Immutable;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout;

/// <summary>
/// Describes a named layout area: its route URL, display metadata (title, description, image,
/// group, order), cross-references, and optional thumbnail. Area definitions populate the layout
/// area catalog and drive navigation menus.
/// </summary>
/// <param name="Area">The area name, used as the route segment and key in LayoutDefinition.AreaDefinitions.</param>
/// <param name="Url">The full URL at which this area is rendered, e.g. <c>{hub}/{Area}</c>.</param>
public record LayoutAreaDefinition(string Area, string Url)
{
    /// <summary>
    /// Human-readable display title for the area; defaults to a word-ified form of the area name.
    /// </summary>
    public string? Title { get; init; } = Area.Wordify();

    /// <summary>Returns a copy with <paramref name="title"/> as the display title.</summary>
    /// <param name="title">The new title to display in navigation menus and the catalog.</param>
    public LayoutAreaDefinition WithTitle(string title)
        => this with { Title = title };

    /// <summary>URL of the representative image shown in the layout area catalog card; defaults to <c>"LayoutAreaDefinition.png"</c>.</summary>
    public string? ImageUrl { get; init; } = "LayoutAreaDefinition.png";
    /// <summary>Returns a copy with <paramref name="imageUrl"/> as the catalog card image URL.</summary>
    /// <param name="imageUrl">The new image URL.</param>
    public LayoutAreaDefinition WithImageUrl(string imageUrl) =>
        this with { ImageUrl = imageUrl };

    /// <summary>Short description of the area's purpose, shown in the catalog and tooltips.</summary>
    public string? Description { get; set; }
    /// <summary>Returns a copy with <paramref name="description"/> as the area description.</summary>
    /// <param name="description">The new description text.</param>
    public LayoutAreaDefinition WithDescription(string description) =>
        this with { Description = description };

    /// <summary>Cross-reference strings (e.g. tag or category identifiers) that link this area to related content.</summary>
    public ImmutableList<string> CRefs { get; init; } = [];
    /// <summary>Returns a copy with the additional <paramref name="reference"/> strings appended to CRefs.</summary>
    /// <param name="reference">One or more cross-reference strings to add (nulls are ignored).</param>
    public LayoutAreaDefinition WithReferences(params IEnumerable<string> reference) =>
        this with { CRefs = CRefs.AddRange(reference.Where(x => x != null)) };

    /// <summary>Optional group name that clusters this area with others in navigation menus.</summary>
    public string? Group { get; init; }
    /// <summary>When <c>true</c>, this area is excluded from navigation menus and the catalog.</summary>
    public bool? IsInvisible { get; init; }
    /// <summary>Sort order within the navigation menu group; lower values appear first. Defaults to 0.</summary>
    public int? Order { get; init; } = 0;
    /// <summary>Returns a copy with <paramref name="group"/> as the navigation menu group.</summary>
    /// <param name="group">The group name under which this area is listed.</param>
    public LayoutAreaDefinition WithGroup(string group)
        => this with { Group = group };

    // Thumbnail metadata (optional; populated by a generation pipeline)
    /// <summary>URL of the auto-generated thumbnail image for this area, if available.</summary>
    public string? ThumbnailUrl { get; init; }
    /// <summary>Perceptual hash of the thumbnail used to detect staleness.</summary>
    public string? ThumbnailHash { get; init; }
    /// <summary>Timestamp when the thumbnail was last generated; <c>null</c> if no thumbnail has been generated.</summary>
    public DateTimeOffset? ThumbnailGeneratedAt { get; init; }
    /// <summary>Returns a copy with thumbnail metadata set.</summary>
    /// <param name="url">The thumbnail image URL.</param>
    /// <param name="hash">Perceptual hash of the thumbnail; null if unknown.</param>
    /// <param name="generatedAt">Timestamp the thumbnail was generated; null if unknown.</param>
    public LayoutAreaDefinition WithThumbnail(string url, string? hash = null, DateTimeOffset? generatedAt = null)
        => this with { ThumbnailUrl = url, ThumbnailHash = hash, ThumbnailGeneratedAt = generatedAt };
}
