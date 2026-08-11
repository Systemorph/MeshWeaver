namespace MeshWeaver.Graph;

/// <summary>
/// The Open Graph metadata of one page, as read from its HTML head — the same tags every
/// MeshWeaver portal emits for its public pages (<c>SeoHead</c>: <c>og:title</c>,
/// <c>og:description</c>, <c>og:image</c>, <c>og:site_name</c>), and that any well-behaved
/// external site provides for link unfurling — plus the page's declared ICON, which is what a
/// compact card actually renders (see <see cref="CardIcon"/>).
/// </summary>
/// <param name="Url">The page URL the preview was read from (the card's navigation target).</param>
/// <param name="Title">The page's <c>og:title</c>, falling back to its <c>&lt;title&gt;</c>.</param>
/// <param name="Description">The page's <c>og:description</c>, falling back to
/// <c>&lt;meta name="description"&gt;</c>.</param>
/// <param name="Image">The page's <c>og:image</c> — the wide POSTER graphic, resolved to an
/// absolute URL. Only a last-resort card visual (see <see cref="CardIcon"/>).</param>
/// <param name="SiteName">The page's <c>og:site_name</c>.</param>
/// <param name="Fetched">Whether the page was actually fetched and parsed. <c>false</c> marks the
/// fallback produced for an unfetchable or failed target — the card then renders from the URL
/// alone (host as title), still navigating to the target.</param>
/// <param name="Icon">The page's declared icon (<c>&lt;link rel="icon"&gt;</c> /
/// <c>apple-touch-icon</c>), resolved to an absolute URL — the sharpest one it declares.</param>
/// <param name="DeclaresOpenGraph">Whether the page declared an <c>og:title</c> — the one tag
/// every page that means to be link-previewed carries. See <see cref="IsResolved"/>: this is what
/// separates a real preview from a transient degraded response.</param>
public sealed record OpenGraphPreview(
    string Url,
    string? Title,
    string? Description,
    string? Image,
    string? SiteName,
    bool Fetched,
    string? Icon = null,
    bool DeclaresOpenGraph = false)
{
    /// <summary>The conventional origin-root icon path every browser has probed since forever —
    /// the last-resort guess for a page that declares no icon link.</summary>
    private const string ConventionalFaviconPath = "/favicon.ico";

    /// <summary>
    /// The visual the card renders: the page's ICON, not its poster.
    ///
    /// <para>The card's image box is a fixed 48 px square with <c>object-fit: cover</c>. A 1200×630
    /// <c>og:image</c> poster cropped into it yields a meaningless sliver of a banner, whereas an
    /// icon is purpose-built for exactly that size — which is why the icon wins outright.</para>
    ///
    /// <para>Order: <b>declared icon</b> → <b><c>og:image</c></b> → <b>conventional
    /// <c>/favicon.ico</c></b>. Everything the page DECLARED is preferred over the guess, so the
    /// card never trades a guaranteed image for a possible 404; the guess is reached only when the
    /// page declared no visual at all. A target that could not be fetched
    /// (<see cref="Fetched"/> <c>== false</c>) guesses nothing — its origin is unproven, so the
    /// card keeps its clean initials placeholder rather than showing a broken image.</para>
    /// </summary>
    public string? CardIcon => Icon ?? Image ?? (Fetched ? ConventionalFavicon(Url) : null);

    /// <summary>
    /// Whether this preview is worth REMEMBERING — the gate on the per-URL promise cache.
    ///
    /// <para>🚨 A 200 response is not the same as a usable answer. A portal mid-restart, a login
    /// wall, or any SPA catch-all route serves its shell page with HTTP <b>200</b> and no
    /// <c>og:*</c> tags at all — a successful fetch carrying nothing. Because it did not throw, the
    /// exception-eviction path never fired, so that shell got cached and PINNED for the lifetime of
    /// the process: one card stuck showing the catch-all's <c>&lt;title&gt;</c> ("Memex Portal")
    /// with no description, while its neighbours rendered perfectly. Only a preview that actually
    /// declared Open Graph metadata may be cached; anything else is evicted and re-fetched on the
    /// next view, which is exactly right because those responses are the transient ones.</para>
    ///
    /// <para>The gate keys on <see cref="DeclaresOpenGraph"/>, NOT on <see cref="Title"/> — the
    /// catch-all page HAS a <c>&lt;title&gt;</c>, which is precisely why it slipped through — and
    /// NOT on <see cref="Icon"/>/<see cref="CardIcon"/>: a favicon is found for practically any
    /// page, including a degraded one, so an icon is never evidence that the metadata is good.</para>
    ///
    /// <para>Cost, accepted deliberately: a genuinely Open-Graph-less page is re-fetched once per
    /// view rather than cached. That is the honest trade — never pin a wrong answer for the
    /// process lifetime to save a request.</para>
    /// </summary>
    public bool IsResolved => Fetched && DeclaresOpenGraph;

    /// <summary>The fallback preview for a target that could not be fetched: no metadata, the
    /// card renders from the URL alone.</summary>
    public static OpenGraphPreview Unavailable(string url) =>
        new(url, null, null, null, null, false);

    /// <summary>The origin-root <c>/favicon.ico</c> of a page URL, or null when the URL is not
    /// absolute.</summary>
    private static string? ConventionalFavicon(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new Uri(uri, ConventionalFaviconPath).ToString()
            : null;
}
