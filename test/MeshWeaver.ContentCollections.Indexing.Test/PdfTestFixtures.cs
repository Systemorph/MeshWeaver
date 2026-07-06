using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// Generates tiny single-page PDFs at test time with PdfPig's <see cref="PdfDocumentBuilder"/>,
/// so the extractor tests run against a real PDF byte stream without checking a binary into the repo.
/// </summary>
internal static class PdfTestFixtures
{
    /// <summary>Builds a one-page PDF containing each line of <paramref name="lines"/> as PDF text.</summary>
    public static byte[] OnePagePdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842); // A4 in points.

        var y = 800;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(50, y), font);
            y -= 20;
        }

        return builder.Build();
    }

    /// <summary>Builds a multi-page PDF; each element of <paramref name="pages"/> is one page's single line.</summary>
    public static byte[] MultiPagePdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var line in pages)
        {
            var page = builder.AddPage(595, 842);
            page.AddText(line, 12, new PdfPoint(50, 800), font);
        }

        return builder.Build();
    }
}
