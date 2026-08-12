using System.Reactive.Linq;
using HtmlAgilityPack;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Email;

/// <summary>
/// Composes a markdown mesh node into ONE self-contained, email-client-safe HTML document.
///
/// <para>The order of the three passes matters and is the whole design:</para>
/// <list type="number">
/// <item><description><b>Render</b> the markdown through the framework's ONE server-side Markdig
/// pipeline — the same one the portal view binds — so embeds, links and images come out exactly
/// as the portal renders them.</description></item>
/// <item><description><b>Resolve</b> the layout-area anchors that pipeline leaves behind, by
/// reading each area's live control tree and serializing it to static markup. This must run
/// BEFORE sanitisation, because the anchors are identified by the <c>class</c> and <c>data-*</c>
/// attributes sanitisation strips.</description></item>
/// <item><description><b>Sanitise</b> the finished DOM once — drop what mail cannot render,
/// absolutise every URL — so both the markdown body and the generated area markup are covered by
/// the same rules and nothing can slip through by being generated later.</description></item>
/// </list>
/// </summary>
public static class EmailDocumentComposer
{
    /// <summary>
    /// Renders <paramref name="markdown"/> to a complete email-safe HTML document.
    /// </summary>
    /// <param name="hub">Hub used to open layout-area streams and resolve paths.</param>
    /// <param name="title">Document title (used for the <c>&lt;title&gt;</c> element).</param>
    /// <param name="markdown">The node's markdown source.</param>
    /// <param name="nodePath">
    /// The owning node path — the base for relative links AND the content collection for relative
    /// image hrefs, exactly as the live view passes it.
    /// </param>
    /// <param name="options">Email rendering options (base URL, card metrics, settle budget).</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A cold observable emitting the finished HTML once, then completing.</returns>
    public static IObservable<string> Compose(
        IMessageHub hub,
        string title,
        string markdown,
        string? nodePath,
        EmailHtmlOptions options,
        ILogger? logger = null)
        => Observable.Defer(() =>
        {
            var rendered = MarkdownViewLogic.Render(markdown ?? string.Empty, nodePath, nodePath);

            var document = new HtmlDocument();
            document.LoadHtml(rendered.Html ?? string.Empty);

            return EmailAreaResolver.Resolve(document, hub, options, logger)
                .Select(_ =>
                {
                    // Sizing runs on the finished DOM so a table produced by a resolved layout
                    // area is treated exactly like one written in the markdown.
                    var tables = EmailTableSizer.SizeTables(document.DocumentNode);
                    if (tables > 0)
                        logger?.LogInformation("Email export sized {Tables} table(s)", tables);

                    EmailHtmlSanitizer.Sanitize(document.DocumentNode, options);
                    return Wrap(title, document.DocumentNode.InnerHtml, options);
                });
        });

    /// <summary>
    /// Puts a covering note at the top of an already-composed document, so sending the document
    /// AS the mail body does not silently discard the sender's message.
    ///
    /// <para>Done on the DOM rather than by splicing strings: the intro has to land inside the
    /// content column (so it inherits the document's typography), which is a position no string
    /// concatenation can name reliably.</para>
    /// </summary>
    /// <param name="documentHtml">A document produced by <see cref="Compose"/>.</param>
    /// <param name="introHtml">Pre-escaped HTML for the note; ignored when blank.</param>
    public static string PrependIntro(string documentHtml, string? introHtml)
    {
        if (string.IsNullOrWhiteSpace(introHtml))
            return documentHtml;

        var document = new HtmlDocument();
        document.LoadHtml(documentHtml);

        // The content column is the wrapper div inside body; fall back to body itself if the
        // document did not come from Compose.
        var host = document.DocumentNode.SelectSingleNode("//body/div")
                   ?? document.DocumentNode.SelectSingleNode("//body");
        if (host is null)
            return documentHtml;

        var intro = HtmlNode.CreateNode(
            EmailNode.El("div").Style("margin:0 0 20px 0").Add(EmailNode.Raw(introHtml)).Render());
        host.PrependChild(intro);

        return document.DocumentNode.OuterHtml;
    }

    /// <summary>
    /// Wraps the body markup in the outer document: charset, title, a painted background and the
    /// bounded, centred content column carrying the base typography every child inherits.
    /// </summary>
    internal static string Wrap(string title, string bodyHtml, EmailHtmlOptions options)
    {
        var head = EmailNode.El("head",
            EmailNode.El("meta").With("charset", "utf-8"),
            EmailNode.El("meta")
                .With("name", "viewport")
                .With("content", "width=device-width, initial-scale=1"),
            EmailNode.El("title", EmailNode.Text(title)));

        var body = EmailNode.El("body")
            .Style(EmailStyles.Body)
            .Add(EmailNode.El("div")
                .Style(EmailStyles.Wrapper(options.ContentWidthPx))
                .Add(EmailNode.Raw(bodyHtml)));

        return "<!doctype html>" + EmailNode.El("html", head, body).Render();
    }
}
