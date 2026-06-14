using System.Text;
using DocSharp.Docx;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Default <see cref="ITextExtractor"/>. Dispatches by file extension and runs the actual
/// decode/parse as a CPU leaf through <see cref="IIoPool.InvokeBlocking{T}"/> — never on the
/// calling hub scheduler, bounded by the pool. Unknown / binary formats extract to empty (logged).
/// </summary>
public sealed class TextExtractor : ITextExtractor
{
    private readonly IIoPool _ioPool;
    private readonly ILogger<TextExtractor> _logger;

    /// <summary>
    /// Creates a text extractor. <paramref name="ioPool"/> defaults to
    /// <see cref="IoPool.Unbounded"/> (ThreadPool offload, no cap) when constructed outside DI,
    /// e.g. in tests.
    /// </summary>
    public TextExtractor(IIoPool? ioPool = null, ILogger<TextExtractor>? logger = null)
    {
        _ioPool = ioPool ?? IoPool.Unbounded;
        _logger = logger ?? NullLogger<TextExtractor>.Instance;
    }

    public IObservable<string> ExtractText(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        // The whole parse is a synchronous, CPU-bound leaf — PdfPig, DocSharp and StreamReader all
        // block. InvokeBlocking runs it on the limited-concurrency scheduler, off the hub thread.
        return _ioPool.InvokeBlocking(_ => Extract(extension, fileName, bytes));
    }

    private string Extract(string extension, string fileName, byte[] bytes)
    {
        switch (extension)
        {
            case ".pdf":
                return ExtractPdf(bytes);
            case ".docx":
                return ExtractDocx(bytes);
            case ".md":
            case ".markdown":
            case ".txt":
            case ".csv":
            case ".json":
            case ".html":
            case ".htm":
            case ".xml":
                return DecodeUtf8(bytes);
            default:
                _logger.LogInformation(
                    "No text extractor for extension '{Extension}' (file '{FileName}'); extracting to empty.",
                    extension, fileName);
                return string.Empty;
        }
    }

    private static string ExtractPdf(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(page.Text);
        }

        return sb.ToString();
    }

    private static string ExtractDocx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var converter = new DocxToMarkdownConverter();
        return converter.ConvertToString(stream);
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        // detectEncodingFromByteOrderMarks: true honours a BOM if present, else UTF-8.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
