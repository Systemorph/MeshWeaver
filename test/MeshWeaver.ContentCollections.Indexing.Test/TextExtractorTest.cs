using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

public class TextExtractorTest
{
    private readonly ITextExtractor _extractor = new TextExtractor(IoPool.Unbounded);

    private async Task<string> Extract(string fileName, byte[] bytes) =>
        await _extractor.ExtractText(fileName, bytes).FirstAsync().ToTask();

    private async Task<ExtractedDocument> ExtractDoc(string fileName, byte[] bytes) =>
        await _extractor.ExtractDocument(fileName, bytes).FirstAsync().ToTask();

    [Fact]
    public async Task Txt_DecodesUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog.");
        var text = await Extract("notes.txt", bytes);
        text.Should().Contain("quick brown fox");
    }

    [Fact]
    public async Task Md_DecodesUtf8WithMarkup()
    {
        var bytes = Encoding.UTF8.GetBytes("# Heading\n\nSome **bold** body text about embeddings.");
        var text = await Extract("doc.md", bytes);
        text.Should().Contain("Heading");
        text.Should().Contain("embeddings");
    }

    [Fact]
    public async Task Utf8Bom_IsStripped()
    {
        var preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
        var body = Encoding.UTF8.GetBytes("hello bom");
        var bytes = preamble.Concat(body).ToArray();
        var text = await Extract("bom.txt", bytes);
        text.Should().Be("hello bom");
    }

    [Fact]
    public async Task Pdf_ExtractsPageText()
    {
        var bytes = PdfTestFixtures.OnePagePdf(
            "Indexing pipeline overview",
            "Content files become chunks become embeddings.");

        var text = await Extract("overview.pdf", bytes);

        text.Should().Contain("Indexing pipeline overview");
        text.Should().Contain("embeddings");
    }

    [Fact]
    public async Task UnknownExtension_ExtractsEmpty()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var text = await Extract("blob.bin", bytes);
        text.Should().BeEmpty();
    }

    // ── ExtractDocument: positional spans ────────────────────────────────────

    [Fact]
    public async Task ExtractDocument_Pdf_CarriesPageOneSpansInsideTheUnitBox()
    {
        var bytes = PdfTestFixtures.OnePagePdf("Revenue grew across regions this quarter");
        var doc = await ExtractDoc("report.pdf", bytes);

        doc.Text.Should().Contain("Revenue");
        doc.Spans.Should().NotBeEmpty();
        // Every span is on page 1 and every normalized coordinate is within the unit page box.
        doc.Spans.Should().OnlyContain(s => s.Page == 1);
        doc.Spans.Should().OnlyContain(s =>
            s.Box.X >= 0 && s.Box.X <= 1 && s.Box.Y >= 0 && s.Box.Y <= 1 &&
            s.Box.Width > 0 && s.Box.Width <= 1 && s.Box.Height > 0 && s.Box.Height <= 1);

        // The line sits at x=50/595 (left margin) and y=800/842 (near the top) — so the first word's box
        // is on the left and near the top (small Y with the top-left origin).
        var first = doc.Spans[0];
        first.Box.X.Should().BeLessThan(0.2);
        first.Box.Y.Should().BeLessThan(0.15);
        // The span text is a slice of the document text at the span's offset.
        doc.Text.Substring(first.Start, first.Length).Should().Be("Revenue");
    }

    [Fact]
    public async Task ExtractDocument_Pdf_MultiPage_SpansCarryDistinctPages()
    {
        var bytes = PdfTestFixtures.MultiPagePdf("First page alpha", "Second page bravo");
        var doc = await ExtractDoc("two.pdf", bytes);

        doc.Spans.Select(s => s.Page).Distinct().OrderBy(p => p).Should().Equal(1, 2);
        // A page-2 word maps to page 2.
        doc.Spans.Should().Contain(s => s.Page == 2 && doc.Text.Substring(s.Start, s.Length) == "bravo");
    }

    [Fact]
    public async Task ExtractDocument_Txt_HasTextButNoSpans()
    {
        var doc = await ExtractDoc("notes.txt", Encoding.UTF8.GetBytes("plain text has no layout"));
        doc.Text.Should().Contain("plain text");
        doc.Spans.Should().BeEmpty();
    }
}
