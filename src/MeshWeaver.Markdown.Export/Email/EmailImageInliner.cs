using System.Collections.Immutable;
using System.Reactive.Linq;
using HtmlAgilityPack;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Markdown.Export.Email;

/// <summary>
/// The rewritten body plus the inline parts it now references.
/// </summary>
/// <param name="Html">The body with every embedded picture pointing at a <c>cid:</c> part.</param>
/// <param name="Attachments">The inline parts, each carrying the matching content ID.</param>
/// <param name="Remote">
/// Absolute <c>http(s)</c> image URLs left as links. Reported rather than silently accepted,
/// because most clients hide them behind "Download pictures".
/// </param>
public sealed record EmailImageInlining(
    string Html,
    ImmutableArray<EmailAttachment> Attachments,
    ImmutableArray<string> Remote);

/// <summary>
/// Turns the pictures embedded in an email body into <b>content-ID inline parts</b>.
///
/// <para>A picture reaches the reader only if it travels inside the message. The two things a
/// document naturally contains both fail in real inboxes: a <c>data:</c> URI is stripped by
/// classic Outlook for Windows, and a remote <c>https</c> image sits behind "Download pictures"
/// in most clients' default configuration. Converting them to <c>cid:</c> parts fixes both — the
/// bytes ship with the mail, so it renders immediately, needs no fetch and survives forwarding.</para>
///
/// <para>Mesh content references are resolved by the pass that already exists for the print
/// pipeline (<see cref="SlideAssetInliner"/>, which reads them under the caller's identity through
/// the content service), so this class only has to do the last hop: data URI ⇒ inline part.</para>
/// </summary>
public static class EmailImageInliner
{
    /// <summary>
    /// Rewrites <paramref name="html"/> so every embeddable image is a <c>cid:</c> reference and
    /// returns the parts to attach alongside it.
    /// </summary>
    public static IObservable<EmailImageInlining> Inline(string html, IMessageHub hub)
        // Step 1 resolves mesh content references (api/content/…) to data URIs — the existing,
        // access-checked read. Step 2 converts every data URI, whether it came from there or was
        // authored inline, into a content-ID part.
        => SlideAssetInliner.Inline(html ?? string.Empty, hub)
            .Select(resolved => ToContentIdParts(resolved.Html));

    /// <summary>
    /// The data-URI ⇒ content-ID hop on its own, for a body whose mesh content references are
    /// already resolved (and for tests, which need it without a mesh).
    /// </summary>
    public static EmailImageInlining ToContentIdParts(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html ?? string.Empty);

        var images = document.DocumentNode.Descendants("img").ToList();
        if (images.Count == 0)
            return new EmailImageInlining(html ?? string.Empty, [], []);

        var attachments = ImmutableArray.CreateBuilder<EmailAttachment>();
        var remote = ImmutableArray.CreateBuilder<string>();
        var index = 0;

        foreach (var image in images)
        {
            var source = image.GetAttributeValue("src", string.Empty);
            if (string.IsNullOrWhiteSpace(source))
                continue;

            if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    remote.Add(source);
                continue;
            }

            if (!TryDecodeDataUri(source, out var mimeType, out var bytes))
                continue;

            // A content ID must be unique within the message and stable between the body and the
            // part — mint one per picture and use it in both places.
            var contentId = $"img{index++}-{Guid.NewGuid().AsString()}";
            attachments.Add(new EmailAttachment(
                FileName: $"{contentId}{ExtensionFor(mimeType)}",
                MimeType: mimeType,
                Content: bytes,
                ContentId: contentId));

            image.SetAttributeValue("src", $"cid:{contentId}");
        }

        return new EmailImageInlining(
            document.DocumentNode.OuterHtml,
            attachments.ToImmutable(),
            remote.ToImmutable());
    }

    /// <summary>
    /// Decodes <c>data:{mime};base64,{payload}</c>. Only base64 payloads are taken: a percent-
    /// encoded data URI is vanishingly rare for a picture and not worth a second decoder that
    /// could mis-handle bytes.
    /// </summary>
    internal static bool TryDecodeDataUri(string uri, out string mimeType, out byte[] bytes)
    {
        mimeType = "application/octet-stream";
        bytes = [];

        var comma = uri.IndexOf(',');
        if (comma < 0)
            return false;

        var header = uri[5..comma];        // strip "data:"
        var payload = uri[(comma + 1)..];
        if (!header.Contains("base64", StringComparison.OrdinalIgnoreCase))
            return false;

        var semicolon = header.IndexOf(';');
        var declared = semicolon < 0 ? header : header[..semicolon];
        if (!string.IsNullOrWhiteSpace(declared))
            mimeType = declared;

        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length > 0;
    }

    private static string ExtensionFor(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".bin"
    };
}
