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
}
