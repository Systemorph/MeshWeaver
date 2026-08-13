using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Document = MeshWeaver.Markdown.Export.Model.Document;

namespace MeshWeaver.Markdown.Export.Pdf;

/// <summary>
/// Renders a <see cref="Document"/> to PDF bytes: the model is composed into one self-contained
/// HTML document by <see cref="DocumentPrintComposer"/> and a headless browser prints it.
///
/// <para><b>Why a browser rather than a PDF document model.</b> The previous implementation drew
/// the document with QuestPDF, whose Community tier is free only below a revenue threshold — a
/// licence MeshWeaver cannot ship under (issue #1230). The replacement is the engine the portal
/// already runs for pixel-faithful deck export, so the product keeps ONE PDF back end instead of
/// two, and the page furniture is expressed in CSS Paged Media rather than in a fluent drawing
/// API. Cover page, table of contents, running header and footer, page numbering and the
/// page-break rules are all reproduced.</para>
///
/// <para><b>Contents page numbers: measure the print, then print the measurement.</b> Chromium
/// implements no part of <c>target-counter</c>, so nothing in the page can name the page a heading
/// landed on (#1309). A print CAN: every contents entry is a link annotation carrying the
/// destination page the browser itself chose. So a document with a contents list is printed twice —
/// once to measure, once to publish — and the second print is then RE-MEASURED and released only if
/// it still agrees. The measuring print already reserves the number column at a constant width, so
/// the digits cannot reflow anything and the two prints are the same layout by construction; the
/// verification is there because "by construction" is exactly the argument that produces silently
/// wrong page numbers. When either step cannot stand behind a number, the export falls back to the
/// contents list it has had since #1230 — links, no numbers — and says why in the log. A contents
/// list that lies about a page is worse than one that is silent about it.</para>
///
/// <para>Cold and reactive: composing, printing and reading back all happen on <b>Subscribe</b>.
/// The browser is a <c>Process</c> leaf bounded by <c>IIoPool</c>'s <c>Process</c> pool inside
/// <see cref="IPixelPdfRenderer"/>, and the read-back — parsing a couple of pages of PDF — is a CPU
/// leaf that goes through the same pool, so a burst of exports bounds its parsing as well as its
/// browsers. There is no <c>Task</c> in the signature and no <c>Observable.FromAsync</c> anywhere
/// on the path.</para>
/// </summary>
/// <param name="browser">The browser leaf that prints a self-contained HTML document.</param>
/// <param name="ioPools">Pool registry; the read-back runs on the same pool as the browser.</param>
/// <param name="logger">Records a refusal to number the contents list, with its reason.</param>
public sealed class PdfDocumentRenderer(
    IPixelPdfRenderer browser,
    IoPoolRegistry ioPools,
    ILogger<PdfDocumentRenderer>? logger = null)
{
    private IIoPool Pool => ioPools.Get(IoPoolNames.Process);

    /// <summary>
    /// Produces the PDF for <paramref name="document"/>.
    ///
    /// <para>Errors surface as <c>OnError</c> and nothing is swallowed: on a deployment with no
    /// browser this fails with <see cref="PixelRendererUnavailableException"/> rather than
    /// returning a document that silently lost its formatting.</para>
    /// </summary>
    public IObservable<byte[]> Render(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Defer keeps the composition cold too — nothing runs until someone subscribes.
        return Observable.Defer(() =>
        {
            var entries = document.Options.TableOfContents ? document.TocHeadings.Length : 0;
            var measuring = browser.Render(DocumentPrintComposer.Compose(document));

            // No contents list, nothing to number: ONE print, exactly as before. The second print
            // is the price of the numbers and is only paid by the documents that show them.
            return entries == 0 ? measuring : measuring.SelectMany(measured => Publish(document, measured, entries));
        });
    }

    /// <summary>
    /// Reads the contents pages out of the measuring print and, when they are trustworthy, prints
    /// the document again with them — releasing that second print only if re-reading it agrees.
    /// </summary>
    private IObservable<byte[]> Publish(Document document, byte[] measured, int entries) =>
        Pool.InvokeBlocking(_ => TocPageNumbers.Resolve(measured, entries))
            .SelectMany(lookup =>
            {
                if (!lookup.Resolved)
                {
                    logger?.LogWarning(
                        "Contents page numbers were not printed for '{Title}': {Reason}. The contents "
                        + "list still links to each section.", document.Title, lookup.Refusal);
                    return Observable.Return(measured);
                }

                return browser.Render(DocumentPrintComposer.Compose(document, lookup.Pages))
                    .SelectMany(published => Verify(document, measured, published, lookup.Pages, entries));
            });

    /// <summary>
    /// The gate on publication: the numbered print must still put every heading on the page its
    /// contents entry claims.
    ///
    /// <para>This is what makes the two-pass honest rather than hopeful. The numbers were measured
    /// on a DIFFERENT print, and the only thing that makes them true of this one is that the two
    /// paginate identically — so that is asserted against the printed artefact, not assumed from
    /// the stylesheet. A mismatch releases the measuring print (which claims nothing) and names the
    /// disagreement.</para>
    /// </summary>
    private IObservable<byte[]> Verify(
        Document document,
        byte[] measured,
        byte[] published,
        ImmutableArray<int> printedPages,
        int entries) =>
        Pool.InvokeBlocking(_ => TocPageNumbers.Resolve(published, entries))
            .Select(check =>
            {
                if (check.Resolved && check.Pages.SequenceEqual(printedPages))
                    return published;

                logger?.LogWarning(
                    "Contents page numbers were withdrawn from '{Title}': the print carrying them "
                    + "paginates differently from the one they were measured on ({Reason}). Printed "
                    + "{Printed}, re-read {Reread}.",
                    document.Title,
                    check.Refusal ?? "pagination shifted",
                    string.Join(", ", printedPages),
                    check.Resolved ? string.Join(", ", check.Pages) : "nothing");
                return measured;
            });
}
