namespace MeshWeaver.Graph;

/// <summary>
/// The Open Graph metadata of one page, as read from its HTML head — the same tags every
/// MeshWeaver portal emits for its public pages (<c>SeoHead</c>: <c>og:title</c>,
/// <c>og:description</c>, <c>og:image</c>, <c>og:site_name</c>), and that any well-behaved
/// external site provides for link unfurling.
/// </summary>
/// <param name="Url">The page URL the preview was read from (the card's navigation target).</param>
/// <param name="Title">The page's <c>og:title</c>, falling back to its <c>&lt;title&gt;</c>.</param>
/// <param name="Description">The page's <c>og:description</c>, falling back to
/// <c>&lt;meta name="description"&gt;</c>.</param>
/// <param name="Image">The page's <c>og:image</c>, resolved to an absolute URL.</param>
/// <param name="SiteName">The page's <c>og:site_name</c>.</param>
/// <param name="Fetched">Whether the page was actually fetched and parsed. <c>false</c> marks the
/// fallback produced for an unfetchable or failed target — the card then renders from the URL
/// alone (host as title), still navigating to the target.</param>
public sealed record OpenGraphPreview(
    string Url,
    string? Title,
    string? Description,
    string? Image,
    string? SiteName,
    bool Fetched)
{
    /// <summary>The fallback preview for a target that could not be fetched: no metadata, the
    /// card renders from the URL alone.</summary>
    public static OpenGraphPreview Unavailable(string url) =>
        new(url, null, null, null, null, false);
}
