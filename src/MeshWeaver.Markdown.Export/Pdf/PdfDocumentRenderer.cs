using System.Reactive.Linq;
using MeshWeaver.Markdown.Export.Pixel;
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
/// API. Every feature the document model provided is reproduced — cover page, table of contents,
/// running header and footer, page numbering, page-break rules — with one named exception: the
/// contents list carries links but no page numbers, because Chromium implements no way to read a
/// target's page from CSS. See <c>Doc/Architecture/PixelFaithfulExport</c>.</para>
///
/// <para>Cold and reactive: composing and printing both happen on <b>Subscribe</b>, and the
/// browser itself is a <c>Process</c> leaf bounded by <c>IIoPool</c>'s <c>Process</c> pool inside
/// <see cref="IPixelPdfRenderer"/>. There is no <c>Task</c> in the signature and no
/// <c>Observable.FromAsync</c> anywhere on the path.</para>
/// </summary>
/// <param name="browser">The browser leaf that prints a self-contained HTML document.</param>
public sealed class PdfDocumentRenderer(IPixelPdfRenderer browser)
{
    /// <summary>
    /// Produces the PDF for <paramref name="document"/>.
    ///
    /// <para>Errors surface as <c>OnError</c> and nothing is swallowed: on a deployment with no
    /// browser this fails with <see cref="PixelRendererUnavailableException"/> rather than
    /// returning a document that silently lost its formatting.</para>
    /// </summary>
    public IObservable<byte[]> Render(Document document) =>
        // Defer keeps the composition cold too — nothing runs until someone subscribes.
        Observable.Defer(() => browser.Render(DocumentPrintComposer.Compose(document)));
}
