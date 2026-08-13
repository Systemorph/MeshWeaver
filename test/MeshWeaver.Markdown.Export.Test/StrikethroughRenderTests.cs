using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Docx;
using MeshWeaver.Markdown.Export.Pdf;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// #1373, asserted where the reader sees it: on the produced DOCX and the printed PDF.
///
/// <para>The document model is pinned in <c>DocumentBuilderTests</c>, but a model assertion cannot
/// close this one on its own — the complaint is that a word came out <b>bold</b> on the page, and
/// only the page can say it no longer does. Both renderers consume the same model, and both carried
/// the fault through: the PDF print wrapped the run in
/// <c>&lt;strong&gt;&lt;s&gt;…&lt;/s&gt;&lt;/strong&gt;</c>, and Word wrote <c>&lt;w:b/&gt;</c>
/// beside <c>&lt;w:strike/&gt;</c>.</para>
///
/// <para>Every document here carries a genuinely bold word next to the struck one, so each
/// assertion has its own control in the same artefact: "not bold" is only meaningful if bold is
/// visibly reachable by the same pipeline on the same page.</para>
/// </summary>
public class StrikethroughRenderTests : IDisposable
{
    private readonly IoPoolRegistry pools = new();

    private const string Struck = "Retired";
    private const string Emphasised = "Current";

    /// <summary>Distinct single words: each survives PDF text extraction as one contiguous run.</summary>
    private const string Markdown = $"""
        # Status

        The **{Emphasised}** plan replaces the ~~{Struck}~~ one.
        """;

    private static Model.Document Build() => new DocumentBuilder().Build(
        "Status",
        Markdown,
        new DocumentExportOptions { CoverPage = false, TableOfContents = false, PageBreakBeforeH1 = false },
        BrandingOptions.Default);

    /// <summary>
    /// Word says it outright: the struck run carries <c>w:strike</c> and must NOT carry <c>w:b</c>.
    /// </summary>
    [Fact]
    public void Docx_strikes_the_word_without_bolding_it()
    {
        var bytes = new DocxDocumentRenderer().Render(Build());

        using var stream = new MemoryStream(bytes);
        using var word = WordprocessingDocument.Open(stream, false);
        var body = word.MainDocumentPart?.Document?.Body;
        body.Should().NotBeNull("the rendered package must carry a document body");
        var runs = body!.Descendants<Run>().ToArray();

        // Read the properties off each run FIRST and assert them non-null. A `?.` chain here would
        // short-circuit on a run with no RunProperties and skip the assertion entirely — an
        // assertion that cannot fail is not an assertion.
        var struck = PropertiesOf(runs, Struck);
        struck.GetFirstChild<Strike>().Should().NotBeNull("the word is struck through");
        struck.GetFirstChild<Bold>().Should().BeNull(
            "`~~` is strikethrough only — it was reading as strong because the delimiter COUNT "
            + "was the whole test (#1373)");

        // The control, in the same document: real bold still arrives as bold, and is not struck.
        var bold = PropertiesOf(runs, Emphasised);
        bold.GetFirstChild<Bold>().Should().NotBeNull();
        bold.GetFirstChild<Strike>().Should().BeNull();
    }

    private static RunProperties PropertiesOf(IEnumerable<Run> runs, string text)
    {
        var run = runs.Single(r => r.InnerText == text);
        run.RunProperties.Should().NotBeNull($"the run for '{text}' must carry run properties");
        return run.RunProperties!;
    }

    /// <summary>
    /// And on the printed page. A PDF records the FONT each glyph was drawn with, so the browser's
    /// own answer to "did you print this bold?" is readable straight out of the file: the struck
    /// word must be drawn in the same weight as ordinary body text, while the genuinely bold word
    /// in the same sentence must not be — an independent control that makes a passing assertion
    /// mean something.
    ///
    /// <para><b>Never skipped</b>: with no browser the renderer must fail loudly, which is the
    /// contract <c>RendererOutputTests</c> pins and this test honours rather than quietly passing.</para>
    /// </summary>
    [Fact]
    public async Task Pdf_prints_the_word_struck_but_not_bold()
    {
        var browser = new HeadlessChromiumPdfRenderer(
            new PixelRenderingOptions { NoSandbox = OperatingSystem.IsLinux() }, pools);
        var renderer = new PdfDocumentRenderer(browser, pools);

        if (await browser.Probe().FirstAsync().ToTask() is null)
        {
            var render = async () => await renderer.Render(Build()).FirstAsync().ToTask();
            (await render.Should().ThrowAsync<PixelRendererUnavailableException>())
                .WithMessage("*headless Chromium*");
            return;
        }

        var bytes = await renderer.Render(Build()).FirstAsync().ToTask();

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var letters = pdf.GetPages().SelectMany(p => p.Letters).ToArray();

        var struckFonts = FontsOf(letters, Struck);
        var boldFonts = FontsOf(letters, Emphasised);

        struckFonts.Should().NotBeEmpty("the struck word must actually be printed");
        boldFonts.Should().NotBeEmpty("so must the bold one");

        boldFonts.Should().OnlyContain(f => IsBold(f),
            "the control: genuine `**bold**` still prints bold, so 'not bold' below is a real finding");
        struckFonts.Should().OnlyContain(f => !IsBold(f),
            $"`~~{Struck}~~` is strikethrough, and it was printing bold as well (#1373). "
            + $"Fonts seen: {string.Join(", ", struckFonts)}");
    }

    /// <summary>
    /// The font names the glyphs of <paramref name="word"/> were drawn with. Matched by walking the
    /// letters in order rather than by a text search, because that is the only reading that ties a
    /// font back to a specific word.
    /// </summary>
    private static string[] FontsOf(
        IReadOnlyList<UglyToad.PdfPig.Content.Letter> letters, string word)
    {
        for (var i = 0; i + word.Length <= letters.Count; i++)
        {
            var slice = letters.Skip(i).Take(word.Length).ToArray();
            if (string.Concat(slice.Select(l => l.Value)) != word) continue;
            return slice.Select(l => l.Font.Name).Distinct().ToArray();
        }
        return [];
    }

    /// <summary>
    /// Chromium embeds a subset per weight and names it after the face it came from — the weight is
    /// in the name (…-Bold, …,Bold, …-Semibold, …Black), never in a flag PdfPig exposes.
    /// </summary>
    private static bool IsBold(string fontName) =>
        fontName.Contains("bold", StringComparison.OrdinalIgnoreCase)
        || fontName.Contains("black", StringComparison.OrdinalIgnoreCase)
        || fontName.Contains("heavy", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => pools.Dispose();
}
