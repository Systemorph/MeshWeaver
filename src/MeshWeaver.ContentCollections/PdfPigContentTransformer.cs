using System.Collections.Immutable;
using System.Text;
using UglyToad.PdfPig;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Transforms .pdf documents to plain text using UglyToad.PdfPig, one page's text per line group.
/// Registered as IContentTransformer via DI. Without this, a PDF read through the content-collection
/// file path falls through to the raw StreamReader and returns the binary <c>%PDF-…FlateDecode…</c>
/// bytes decoded as UTF-8 (the reported "get returned the full PDF" bug).
/// </summary>
public class PdfPigContentTransformer : IContentTransformer
{
    private static readonly ImmutableHashSet<string> Extensions =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, ".pdf");

    /// <summary>The file extensions this transformer handles (<c>.pdf</c>).</summary>
    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    /// <summary>Extracts the page text from a <c>.pdf</c> document stream.</summary>
    /// <param name="input">The document stream to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The concatenated page text.</returns>
    public async Task<string> TransformToMarkdownAsync(Stream input, CancellationToken ct = default)
    {
        // PdfPig needs random access (xref table) — buffer first so a non-seekable source
        // stream (e.g. an Azure blob read stream) parses correctly. Open the seekable buffer
        // directly (rewound) rather than copying it to a byte[] — lower peak memory on big PDFs.
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using var document = PdfDocument.Open(buffer);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(page.Text);
        }

        return sb.ToString();
    }
}
