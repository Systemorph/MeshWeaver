using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;

namespace MeshWeaver.Markdown.Export.Email;

/// <summary>
/// Turns a LIVE layout area's control tree into static, email-safe markup.
///
/// <para>This is the piece the export pipeline was missing. A layout area is a reactive control
/// tree that only ever became HTML inside a browser: the markdown pipeline emits an empty
/// <c>&lt;div class='layout-area'&gt;</c> anchor and the Blazor circuit subscribes to the area and
/// renders into it. Nothing server-side ever produced markup for one — which is exactly why PDF
/// and DOCX exports lose every embed. Here the control tree is read off the same synchronization
/// stream the browser uses and serialized directly to table-based HTML, with no browser
/// involved.</para>
///
/// <para>The mapping is deliberately small and email-shaped rather than a general control
/// renderer: a container becomes a table or a run of blocks, a node card becomes a bordered
/// cell, text becomes a paragraph. A control with no email meaning contributes nothing rather
/// than a broken approximation.</para>
/// </summary>
public static class EmailControlRenderer
{
    /// <summary>
    /// Guards against a self-referential area graph. Nesting deeper than this in a document
    /// meant for email is a cycle in practice, not a real layout.
    /// </summary>
    private const int MaxDepth = 6;

    /// <summary>
    /// Renders the control at <paramref name="area"/> (and its child areas) to an email HTML node.
    /// Emits exactly once and completes; never faults — an area that cannot be read contributes
    /// <see cref="EmailNode.Empty"/> so one bad embed can never fail a whole document export.
    /// </summary>
    public static IObservable<EmailNode> Render(
        ISynchronizationStream<JsonElement> stream,
        string area,
        EmailHtmlOptions options)
        => RenderArea(stream, area, options, depth: 0).Select(r => r.Node);

    private static IObservable<Rendered> RenderArea(
        ISynchronizationStream<JsonElement> stream,
        string area,
        EmailHtmlOptions options,
        int depth)
    {
        if (depth > MaxDepth)
            return Observable.Return(Rendered.None);

        return Settle(stream.GetControlStream(area), options)
            .SelectMany(control => RenderControl(stream, control, options, depth))
            .Catch((Exception _) => Observable.Return(Rendered.None));
    }

    /// <summary>
    /// Takes ONE snapshot of a live area.
    ///
    /// <para>A layout area has no completion signal — it is a stream that keeps emitting as its
    /// data lands. <c>OgCard</c>, for instance, emits a grid of placeholder cards immediately and
    /// then replaces each one as its node stream or Open Graph fetch returns. Taking the first
    /// emission would therefore export the PLACEHOLDERS. So "done" is defined as quiescent:
    /// the tree is snapshotted once it has stopped changing for
    /// <see cref="EmailHtmlOptions.SettleWindow"/>, or — for an area that never goes quiet — as
    /// whatever it last held at the <see cref="EmailHtmlOptions.Timeout"/> deadline, whichever
    /// comes first. An area that never emits at all yields null and renders as nothing.</para>
    /// </summary>
    private static IObservable<UiControl?> Settle(
        IObservable<UiControl?> source, EmailHtmlOptions options)
        => source
            .Publish(shared => shared
                .Throttle(options.SettleWindow)
                .Amb(shared.Sample(Observable.Timer(options.Timeout))))
            .Take(1)
            .Timeout(options.Timeout + TimeSpan.FromSeconds(1), Observable.Return<UiControl?>(null));

    private static IObservable<Rendered> RenderControl(
        ISynchronizationStream<JsonElement> stream,
        UiControl? control,
        EmailHtmlOptions options,
        int depth)
        => control switch
        {
            null => Observable.Return(Rendered.None),

            MeshNodeCardControl card =>
                Observable.Return(new Rendered(card, RenderCard(card, options), IsCard: true)),

            MarkdownControl markdown =>
                Observable.Return(new Rendered(
                    markdown,
                    EmailNode.Raw(Markdig.Markdown.ToHtml(AsText(markdown.Markdown))))),

            HtmlControl html =>
                Observable.Return(new Rendered(html, EmailNode.Raw(AsText(html.Data)))),

            LabelControl label =>
                Observable.Return(new Rendered(
                    label,
                    EmailNode.El("p").Style("margin:0 0 10px 0").Add(EmailNode.Text(AsText(label.Data))))),

            IContainerControl container =>
                RenderContainer(stream, container, options, depth),

            // Every other control is interactive chrome (buttons, editors, pickers, menus) or a
            // view with no static equivalent. A document exported for email is a READING
            // artefact: dropping those is correct, not a gap.
            _ => Observable.Return(Rendered.None)
        };

    private static IObservable<Rendered> RenderContainer(
        ISynchronizationStream<JsonElement> stream,
        IContainerControl container,
        EmailHtmlOptions options,
        int depth)
    {
        var areas = container.Areas
            .Select(a => a.Area?.ToString())
            .Where(a => !string.IsNullOrEmpty(a))
            .ToArray();

        if (areas.Length == 0)
            return Observable.Return(Rendered.None);

        var children = areas
            .Select(a => RenderArea(stream, a!, options, depth + 1))
            .ToArray();

        return Observable.CombineLatest(children)
            .Take(1)
            .Select(rendered =>
            {
                var present = rendered.Where(r => r.Node != EmailNode.Empty).ToArray();
                if (present.Length == 0)
                    return Rendered.None;

                // A run of cards is the one container shape email has to lay out in COLUMNS, and
                // the only portable way to do that is a table (Word supports neither flex nor
                // grid). Detected from the children themselves rather than by sniffing the
                // container's CSS, so it holds however the area chose to style itself.
                return present.All(r => r.IsCard)
                    ? new Rendered(null, CardTable(present.Select(r => r.Node), options))
                    : new Rendered(null, EmailNode.Fragment(present.Select(r => r.Node)));
            });
    }

    /// <summary>
    /// Lays cards out in a fixed-column table. <c>table-layout:fixed</c> plus an explicit
    /// per-cell width makes the columns divide evenly regardless of content, and short rows are
    /// padded with empty cells so the last row stays aligned with the ones above it.
    /// </summary>
    private static EmailNode CardTable(IEnumerable<EmailNode> cards, EmailHtmlOptions options)
    {
        var columns = Math.Max(1, options.CardColumns);
        var cellWidth = $"{100 / columns}%";
        var all = cards.ToArray();

        var rows = all
            .Chunk(columns)
            .Select(chunk =>
            {
                var cells = chunk
                    .Select(card => EmailNode.El("td")
                        .With("width", cellWidth)
                        .With("valign", "top")
                        .Style(EmailStyles.CardCell)
                        .Add(card))
                    .ToList();

                // Pad the final row so its columns keep the same widths as every other row.
                while (cells.Count < columns)
                    cells.Add(EmailNode.El("td").With("width", cellWidth));

                return (EmailNode)EmailNode.El("tr").Add(cells);
            });

        return Table().Style(EmailStyles.GridTable).Add(rows);
    }

    /// <summary>
    /// One link-preview card: a bordered table of FIXED height carrying the title, a clipped
    /// description and the target URL — all three the same link.
    /// </summary>
    private static EmailNode RenderCard(MeshNodeCardControl card, EmailHtmlOptions options)
    {
        var href = ResolveHref(card, options);
        var title = string.IsNullOrWhiteSpace(card.Title)
            ? DisplayUrl(href, options)
            : card.Title!;

        var body = EmailNode.El("td")
            .With("valign", "top")
            .With("height", options.CardHeightPx)
            .Style(EmailStyles.CardBody(options.CardHeightPx))
            .Add(EmailNode.El("a").With("href", href).Style(EmailStyles.CardTitle)
                .Add(EmailNode.Text(title)));

        var description = Clip(card.Description, options.CardDescriptionMaxChars);
        if (!string.IsNullOrEmpty(description))
            body = body.Add(EmailNode.El("div").Style(EmailStyles.CardDescription)
                .Add(EmailNode.Text(description)));

        body = body.Add(EmailNode.El("div").Style("margin-top:7px")
            .Add(EmailNode.El("a").With("href", href).Style(EmailStyles.CardLink)
                .Add(EmailNode.Text(DisplayUrl(href, options) + " ›"))));

        return Table()
            .Style(EmailStyles.CardFrame(options.CardHeightPx))
            .Add(EmailNode.El("tr").Add(body));
    }

    /// <summary>
    /// A presentation table with the belt-and-braces attributes Word honours more reliably than
    /// the equivalent CSS.
    /// </summary>
    private static EmailElement Table() =>
        EmailNode.El("table")
            .With("role", "presentation")
            .With("width", "100%")
            .With("cellpadding", "0")
            .With("cellspacing", "0")
            .With("border", "0");

    /// <summary>
    /// The card's link target: an explicit (external) href wins, otherwise the node path resolved
    /// against the portal origin. Always absolute — a relative link is dead in an inbox.
    /// </summary>
    private static string ResolveHref(MeshNodeCardControl card, EmailHtmlOptions options)
    {
        if (!string.IsNullOrWhiteSpace(card.Href))
            return EmailHtmlSanitizer.Absolutize(card.Href, options.NormalizedBaseUrl);
        return EmailHtmlSanitizer.Absolutize(card.NodePath?.TrimStart('/'), options.NormalizedBaseUrl);
    }

    /// <summary>The link line's text: the URL without its scheme, which reads cleaner in a card.</summary>
    private static string DisplayUrl(string url, EmailHtmlOptions options)
    {
        var text = url.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrEmpty(text) ? options.NormalizedBaseUrl : text;
    }

    /// <summary>
    /// Collapses whitespace and clips to a word boundary, so no description can overflow the card's
    /// fixed height (which is what keeps the rows aligned).
    /// </summary>
    internal static string Clip(string? value, int maxChars)
    {
        var text = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= maxChars)
            return text;

        var cut = text[..maxChars];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > 0)
            cut = cut[..lastSpace];
        return cut + "…";
    }

    private static string AsText(object? value) => value?.ToString() ?? string.Empty;

    /// <summary>
    /// A rendered child: its markup plus the two facts the parent needs to lay it out — whether
    /// it produced anything, and whether it is a card (so a run of them becomes a column table).
    /// </summary>
    private readonly record struct Rendered(UiControl? Control, EmailNode Node, bool IsCard = false)
    {
        public static Rendered None { get; } = new(null, EmailNode.Empty);
    }
}
