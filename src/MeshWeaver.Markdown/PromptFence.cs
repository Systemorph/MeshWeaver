using System.Text;

namespace MeshWeaver.Markdown;

/// <summary>
/// The ```` ```prompt ```` fence (#2511) — the vocabulary shared between the markdown renderer that
/// EMITS the composer marker and the layout area that SERVES it, so neither restates the other's
/// constants.
///
/// <para>A course page authors a suggested AI prompt as a fenced block. Rather than mint a new
/// marker, the fence lowers onto the layout-area marker every client already hydrates
/// (<see cref="LayoutAreaMarkdownRenderer.LayoutArea"/>), pointing at <see cref="AreaName"/> on the
/// page's OWN node hub — so the composer reaches Blazor, React and React Native with nothing new to
/// teach any of them. See <c>Doc/Architecture/MarkdownFenceExtensions</c>.</para>
/// </summary>
public static class PromptFence
{
    /// <summary>
    /// The fence info string that requests a prompt composer (```` ```prompt ````). A WIRE
    /// IDENTIFIER — authored in markdown, never translated.
    /// </summary>
    public const string Language = "prompt";

    /// <summary>
    /// The layout area, registered on every node hub, that serves the composer for a prompt fence
    /// (<c>MeshNodeLayoutAreas.PromptArea</c>). A WIRE IDENTIFIER — it addresses an area, never
    /// translated.
    /// </summary>
    public const string AreaName = "Prompt";

    /// <summary>
    /// Encodes the authored prompt for travel as the composer area's REFERENCE ID.
    ///
    /// <para>🚨 Base64url, never the raw text. An area id is not an opaque blob: it is concatenated
    /// into hrefs (<c>LayoutAreaReference.ToHref</c>), and everything after a <c>?</c> in it is
    /// parsed as reference PARAMETERS (<c>LayoutAreaReference.ParseParameters</c> splits on
    /// <c>?</c> and <c>&amp;</c>). A prompt is prose — it contains <c>/</c>, <c>?</c>, <c>&amp;</c>
    /// and newlines as a matter of course, so carried raw it would be split, re-encoded or
    /// truncated somewhere along the way. The same reasoning, and the same encoding, as
    /// <c>LayoutAreaReference.GetMeshNodeDataContext</c>.</para>
    /// </summary>
    /// <param name="draft">The authored prompt, or null/empty for a composer with no draft.</param>
    /// <returns>The area id: base64url of the draft, or the empty string when there is none.</returns>
    public static string EncodeDraft(string? draft)
        => string.IsNullOrEmpty(draft)
            ? string.Empty
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(draft))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Decodes the authored prompt back out of a composer area's reference id (the inverse of
    /// <see cref="EncodeDraft"/>). Returns null for an absent, empty or malformed id — a composer
    /// with no draft is a perfectly good composer, so a bad id degrades to "no draft" rather than
    /// throwing on a render path.
    /// </summary>
    /// <param name="areaId">The layout-area reference id.</param>
    /// <returns>The authored prompt, or null.</returns>
    public static string? DecodeDraft(string? areaId)
    {
        if (string.IsNullOrEmpty(areaId))
            return null;

        var padded = areaId.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return string.IsNullOrEmpty(decoded) ? null : decoded;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
