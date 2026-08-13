using System.Collections.Immutable;
using System.Globalization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
// MeshWeaver has an AnnotationType of its own (tracked-change kinds) that a global using brings
// into scope here; alias the PDF one so the two can never be confused.
using PdfAnnotationType = UglyToad.PdfPig.Annotations.AnnotationType;

namespace MeshWeaver.Markdown.Export.Pdf;

/// <summary>
/// The page each contents entry points at, <b>read back out of a printed PDF</b> rather than
/// computed.
///
/// <para><b>Why measure the PDF instead of the page.</b> Printing `Chapter 3 …… 12` from CSS needs
/// <c>target-counter()</c>, and Chromium implements no part of it (issue #1309). Measuring in the
/// page with script — heading <c>offsetTop</c> divided by a content height — cannot be made right:
/// the print box is not the viewport, and <c>break-after: avoid</c>, <c>orphans</c>/<c>widows</c>,
/// repeated table headers and unbreakable figures all move a heading to a page arithmetic does not
/// predict. The PDF, by contrast, already contains the answer: every contents entry is a link
/// annotation whose <c>GoTo</c> destination names the page the browser actually put the heading on.
/// That is the browser's own pagination, not a model of it.</para>
///
/// <para><b>Identification is positional, and every assumption it makes is checked.</b> A contents
/// entry is a block-level <c>&lt;a&gt;</c>, so Chromium emits exactly one annotation per entry even
/// when the title wraps; the contents list is the only thing between the cover and the body, so its
/// entries are the FIRST internal links in the document. Reading stops at the page that completes
/// the count — usually page two — so a 500-page export parses three pages, not five hundred.</para>
///
/// <para>Three structural facts are then asserted, and any one of them failing REFUSES rather than
/// guesses: the count must come out exactly, destinations must be non-decreasing (entries are in
/// document order, so their targets are too), and every destination must lie after the last page
/// carrying a contents entry (headings live in the body, which follows the list). A contents list
/// that quietly points at the wrong page is worse than one that prints no page at all — so a
/// refusal is a first-class outcome here, carrying the reason for the log.</para>
/// </summary>
public static class TocPageNumbers
{
    /// <summary>
    /// Resolves the destination page of each of the first <paramref name="entryCount"/> internal
    /// links in <paramref name="pdf"/>.
    /// </summary>
    /// <param name="pdf">A printed PDF — normally one this renderer just produced.</param>
    /// <param name="entryCount">How many contents entries the document was composed with.</param>
    public static TocPageLookup Resolve(byte[] pdf, int entryCount)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (entryCount <= 0)
            return TocPageLookup.Refused("the document has no contents entries");

        using var document = PdfDocument.Open(pdf);

        var pages = ImmutableArray.CreateBuilder<int>(entryCount);
        var lastSourcePage = 0;

        foreach (var page in document.GetPages())
        {
            // Document order within a page: top-down, then left-to-right. A contents list is a
            // plain vertical stack of blocks, so this IS its reading order.
            var links = page.ExperimentalAccess.GetAnnotations()
                .Where(a => a.Type == PdfAnnotationType.Link)
                .Select(a => (Annotation: a, Destination: DestinationPage(a)))
                .Where(l => l.Destination > 0)
                .OrderByDescending(l => l.Annotation.Rectangle.Top)
                .ThenBy(l => l.Annotation.Rectangle.Left)
                .ToArray();

            if (links.Length == 0)
                continue;

            if (pages.Count + links.Length > entryCount)
                return TocPageLookup.Refused(
                    $"page {page.Number.ToString(CultureInfo.InvariantCulture)} carries more internal "
                    + "links than the contents list has remaining entries, so the entries cannot be "
                    + "matched to them positionally");

            foreach (var link in links)
                pages.Add(link.Destination);
            lastSourcePage = page.Number;

            if (pages.Count == entryCount)
                break;
        }

        if (pages.Count != entryCount)
            return TocPageLookup.Refused(
                $"found {pages.Count.ToString(CultureInfo.InvariantCulture)} internal links for "
                + $"{entryCount.ToString(CultureInfo.InvariantCulture)} contents entries");

        var resolved = pages.MoveToImmutable();

        for (var i = 1; i < resolved.Length; i++)
            if (resolved[i] < resolved[i - 1])
                return TocPageLookup.Refused(
                    "contents destinations are not in document order "
                    + $"({string.Join(", ", resolved)})");

        if (resolved[0] <= lastSourcePage)
            return TocPageLookup.Refused(
                $"the first contents destination (page {resolved[0].ToString(CultureInfo.InvariantCulture)}) "
                + $"is not after the contents list itself (page {lastSourcePage.ToString(CultureInfo.InvariantCulture)})");

        if (resolved[^1] > document.NumberOfPages)
            return TocPageLookup.Refused(
                $"a contents destination (page {resolved[^1].ToString(CultureInfo.InvariantCulture)}) "
                + $"is past the end of the document ({document.NumberOfPages.ToString(CultureInfo.InvariantCulture)} pages)");

        return TocPageLookup.Of(resolved);
    }

    /// <summary>
    /// The one-based page an annotation jumps to <b>within this document</b>, or <c>0</c> when it
    /// is not such a jump.
    ///
    /// <para>Deliberately <c>GoToAction</c> and not its base <c>AbstractGoToAction</c>: the
    /// remote and embedded variants (<c>GoToR</c>, <c>GoToE</c>) carry a page number too, but it
    /// numbers a page in ANOTHER file. An external <c>URI</c> link — an ordinary markdown link in
    /// the body — has no destination at all. Neither can enter the positional match.</para>
    /// </summary>
    private static int DestinationPage(Annotation annotation) =>
        annotation.Action is GoToAction { Destination: { } destination }
            ? destination.PageNumber
            : 0;
}

/// <summary>
/// Either the page number for every contents entry, or the reason they could not be established.
///
/// <para>A refusal is not an error: the export still produces a PDF, with the contents list it has
/// always had since #1230 — links, no numbers. What it must never do is print a number nobody
/// checked, so the reason travels to the caller for the log rather than being swallowed.</para>
/// </summary>
public readonly record struct TocPageLookup
{
    private TocPageLookup(ImmutableArray<int> pages, string? refusal)
    {
        Pages = pages;
        Refusal = refusal;
    }

    /// <summary>The one-based page of each contents entry, in entry order. Empty when refused.</summary>
    public ImmutableArray<int> Pages { get; }

    /// <summary>Why the numbers could not be established, or <c>null</c> when they were.</summary>
    public string? Refusal { get; }

    /// <summary>True when <see cref="Pages"/> carries a checked number for every entry.</summary>
    public bool Resolved => Refusal is null && !Pages.IsDefaultOrEmpty;

    internal static TocPageLookup Of(ImmutableArray<int> pages) => new(pages, null);

    internal static TocPageLookup Refused(string reason) => new(ImmutableArray<int>.Empty, reason);
}
