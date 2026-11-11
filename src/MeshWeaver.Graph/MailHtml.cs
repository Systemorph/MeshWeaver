using System.Collections.Immutable;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;

namespace MeshWeaver.Graph;

/// <summary>A mail's HTML as the reading pane frames it: the complete sanitised document and how many remote images it holds back.</summary>
/// <param name="Document">The complete document for <c>SandboxedHtmlControl</c> — sanitised, with its own Content-Security-Policy.</param>
/// <param name="BlockedRemoteImages">How many remote images (and CSS backgrounds) were left out because the viewer has not asked for them.</param>
public sealed record MailHtmlDocument(string Document, int BlockedRemoteImages);

/// <summary>
/// Mail HTML is HOSTILE input. This turns it into a document the inbox's reading pane can frame
/// (<c>SandboxedHtmlControl</c>), on the SERVER, before anything reaches a browser:
/// <list type="bullet">
/// <item>An allow-list parser (HtmlSanitizer on AngleSharp) keeps layout tags, attributes and CSS; script,
/// event handlers, forms and form controls, frames, objects, embeds, <c>meta</c>, <c>base</c>, <c>link</c> and
/// <c>svg</c> never survive.</item>
/// <item>URLs: a link keeps <c>http</c>, <c>https</c> or <c>mailto</c> and opens in a new tab with
/// <c>rel="noopener noreferrer"</c>. A resource (an image, a CSS <c>url(...)</c>) keeps a <c>cid:</c> reference
/// only as the message's own inline part, a <c>data:</c> URL only as a raster image, and a remote address only
/// when the viewer asked to show images — otherwise it is dropped and counted (a remote image is a tracking
/// pixel). <c>javascript:</c>, <c>vbscript:</c> and every other scheme are dropped.</item>
/// <item>The document carries a Content-Security-Policy that allows no script, no connection and no remote
/// resource beyond what the viewer allowed — a second line behind the sanitiser, a third behind the sandbox.</item>
/// <item>The frame's page is LIGHT whatever the portal's theme, as a mail client shows mail: mail is authored
/// for a white page, and inverting its own colours is how dark-mode readers make it unreadable.</item>
/// </list>
/// Pure; a new sanitiser per call (its events close over the call's own state).
/// </summary>
public static class MailHtml
{
    /// <summary>Tags the sanitiser's defaults allow that a mail must never carry: forms and their controls.</summary>
    private static readonly ImmutableArray<string> RemovedTags =
        ["form", "input", "button", "select", "option", "optgroup", "textarea", "datalist", "keygen", "output", "menuitem", "html", "head", "body"];

    /// <summary>Attributes that only a form uses.</summary>
    private static readonly ImmutableArray<string> RemovedAttributes = ["action", "formaction", "method", "enctype", "accept-charset", "tabindex"];

    /// <summary>The raster image types a <c>data:</c> URL may carry (never <c>image/svg+xml</c>, which is a document).</summary>
    private static readonly Regex RasterDataUrl = new(@"^data:image/(png|gif|jpe?g|webp|bmp);base64,[A-Za-z0-9+/=\s]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="html"/> as a framed, sanitised document.
    /// </summary>
    /// <param name="html">The mail's HTML body, as the provider returned it.</param>
    /// <param name="inlineImages">The message's own inline parts by content id (without angle brackets), each a <c>data:</c> URL.</param>
    /// <param name="allowRemoteImages">Whether the viewer asked to show remote images for this message.</param>
    /// <returns>The document and the number of remote images held back.</returns>
    public static MailHtmlDocument Render(string html, IReadOnlyDictionary<string, string>? inlineImages, bool allowRemoteImages)
    {
        var inline = inlineImages ?? ImmutableDictionary<string, string>.Empty;
        var blocked = 0;
        var sanitizer = new HtmlSanitizer();
        foreach (var tag in RemovedTags)
            sanitizer.AllowedTags.Remove(tag);
        // Mail layout lives in <style> blocks; their CSS is sanitised like a style attribute's.
        sanitizer.AllowedTags.Add("style");
        foreach (var attribute in RemovedAttributes)
            sanitizer.AllowedAttributes.Remove(attribute);
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("cid");
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.SanitizedUrl is not { } url)
                return;
            var isLink = e.Tag.LocalName is "a" or "area"
                         && string.Equals(e.Tag.GetAttribute("href")?.Trim(), e.OriginalUrl.Trim(), StringComparison.Ordinal);
            var (kept, wasRemote) = FilterUrl(url, isLink, inline, allowRemoteImages);
            if (wasRemote)
                blocked++;
            e.SanitizedUrl = kept;
        };
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement element)
                return;
            if (element.LocalName is "a" or "area" && element.HasAttribute("href"))
            {
                element.SetAttribute("target", "_blank");
                element.SetAttribute("rel", "noopener noreferrer");
            }
        };
        var body = sanitizer.Sanitize(html);
        return new MailHtmlDocument(Wrap(body, allowRemoteImages), blocked);
    }

    /// <summary>
    /// What one URL becomes: kept as it is, replaced (a <c>cid:</c> by the message's inline part), or dropped
    /// (null). <c>WasRemote</c> is true for a remote resource dropped because the viewer has not asked for
    /// images — the count the pane offers to show. Pure.
    /// </summary>
    /// <param name="url">The URL, after the sanitiser's own scheme check.</param>
    /// <param name="isLink">Whether it is a link's <c>href</c> (navigated by a click) rather than a resource (loaded on display).</param>
    /// <param name="inline">The message's inline parts by content id.</param>
    /// <param name="allowRemoteImages">Whether remote resources may load.</param>
    internal static (string? Url, bool WasRemote) FilterUrl(string url, bool isLink, IReadOnlyDictionary<string, string> inline, bool allowRemoteImages)
    {
        var trimmed = url.Trim();
        var colon = trimmed.IndexOf(':');
        var scheme = colon > 0 ? trimmed[..colon].ToLowerInvariant() : string.Empty;
        if (isLink)
            return (scheme is "http" or "https" or "mailto" ? trimmed : null, false);
        return scheme switch
        {
            "cid" => (InlinePart(trimmed[(colon + 1)..], inline), false),
            "data" => (RasterDataUrl.IsMatch(trimmed) ? trimmed : null, false),
            "http" or "https" => allowRemoteImages ? (trimmed, false) : (null, true),
            // A relative or scheme-less resource has nothing to load from inside an opaque frame.
            _ => (null, false),
        };
    }

    /// <summary>The inline part a <c>cid:</c> reference names, as a raster <c>data:</c> URL, or null. Pure.</summary>
    private static string? InlinePart(string contentId, IReadOnlyDictionary<string, string> inline)
    {
        // Percent-decoding only (cid: is a URL scheme, RFC 2392): form decoding would turn a '+' of a generated id into a space.
        string id;
        try
        {
            id = Uri.UnescapeDataString(contentId).Trim().Trim('<', '>');
        }
        catch (UriFormatException)
        {
            return null;
        }
        return inline.TryGetValue(id, out var part) && RasterDataUrl.IsMatch(part) ? part : null;
    }

    /// <summary>The Content-Security-Policy of the framed document. Pure.</summary>
    /// <param name="allowRemoteImages">Whether remote images may load.</param>
    internal static string ContentSecurityPolicy(bool allowRemoteImages)
        => "default-src 'none'; style-src 'unsafe-inline'; font-src data:; img-src data:"
           + (allowRemoteImages ? " https: http:" : string.Empty)
           + "; form-action 'none'; base-uri 'none'";

    /// <summary>The sanitised body in the frame's own document: its policy, links to a new tab, a light page. Pure.</summary>
    private static string Wrap(string body, bool allowRemoteImages)
        => "<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
           + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{ContentSecurityPolicy(allowRemoteImages)}\">"
           + "<meta name=\"color-scheme\" content=\"light\">"
           + "<base target=\"_blank\">"
           + "<style>html{color-scheme:light;}body{margin:0;padding:16px;background:#ffffff;color:#1f1f1f;"
           + "font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;"
           + "line-height:1.45;overflow-wrap:anywhere;}img{max-width:100%;height:auto;}table{max-width:100%;}</style>"
           + "</head><body>" + body + "</body></html>";
}
