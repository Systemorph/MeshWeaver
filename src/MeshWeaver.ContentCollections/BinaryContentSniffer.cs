using System.Collections.Immutable;
using System.Text;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Decides whether a content file can be returned as text (issue #7064). A file read as text with no
/// transformer used to be decoded raw, whatever it held — a <c>.pptx</c> (a ZIP container) reached an
/// agent's conversation as unprintable bytes and the next model request was refused for its size.
/// Two checks, either of which classifies a file as binary: a KNOWN binary extension (decided without
/// reading the file), or a leading SAMPLE that holds NUL bytes or is neither valid UTF-8 nor plain
/// single-byte text. Pure functions over immutable lookups — no state.
/// </summary>
public static class BinaryContentSniffer
{
    /// <summary>How many leading bytes the content check inspects.</summary>
    public const int SampleBytes = 8 * 1024;

    /// <summary>
    /// Binary formats with no text form of their own, keyed by extension → (media type, description).
    /// A registered <see cref="IContentTransformer"/> for one of these still wins (it is consulted
    /// first); without one the file is never decoded. <c>.docx</c>/<c>.xlsx</c>/<c>.xls</c>/<c>.pdf</c>
    /// are listed because their transformers live in an optional module — a host without it must
    /// refuse them by name, not decode them. Read-only constant lookup.
    /// </summary>
    public static readonly ImmutableDictionary<string, (string MediaType, string Description)> KnownBinaryTypes =
        new Dictionary<string, (string, string)>
        {
            [".pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", "PowerPoint presentation"),
            [".pptm"] = ("application/vnd.ms-powerpoint.presentation.macroEnabled.12", "PowerPoint presentation"),
            [".potx"] = ("application/vnd.openxmlformats-officedocument.presentationml.template", "PowerPoint template"),
            [".ppsx"] = ("application/vnd.openxmlformats-officedocument.presentationml.slideshow", "PowerPoint slide show"),
            [".ppt"] = ("application/vnd.ms-powerpoint", "PowerPoint presentation (legacy)"),
            [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Word document"),
            [".docm"] = ("application/vnd.ms-word.document.macroEnabled.12", "Word document"),
            [".doc"] = ("application/msword", "Word document (legacy)"),
            [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Excel workbook"),
            [".xlsm"] = ("application/vnd.ms-excel.sheet.macroEnabled.12", "Excel workbook"),
            [".xlsb"] = ("application/vnd.ms-excel.sheet.binary.macroEnabled.12", "Excel workbook"),
            [".xls"] = ("application/vnd.ms-excel", "Excel workbook (legacy)"),
            [".odt"] = ("application/vnd.oasis.opendocument.text", "OpenDocument text"),
            [".ods"] = ("application/vnd.oasis.opendocument.spreadsheet", "OpenDocument spreadsheet"),
            [".odp"] = ("application/vnd.oasis.opendocument.presentation", "OpenDocument presentation"),
            [".pdf"] = ("application/pdf", "PDF document"),
            [".zip"] = ("application/zip", "ZIP archive"),
            [".gz"] = ("application/gzip", "gzip archive"),
            [".tgz"] = ("application/gzip", "gzip archive"),
            [".tar"] = ("application/x-tar", "tar archive"),
            [".7z"] = ("application/x-7z-compressed", "7-Zip archive"),
            [".rar"] = ("application/vnd.rar", "RAR archive"),
            [".bz2"] = ("application/x-bzip2", "bzip2 archive"),
            [".xz"] = ("application/x-xz", "xz archive"),
            [".exe"] = ("application/vnd.microsoft.portable-executable", "executable"),
            [".dll"] = ("application/vnd.microsoft.portable-executable", "library"),
            [".so"] = ("application/octet-stream", "library"),
            [".dylib"] = ("application/octet-stream", "library"),
            [".bin"] = ("application/octet-stream", "binary file"),
            [".jar"] = ("application/java-archive", "Java archive"),
            [".wasm"] = ("application/wasm", "WebAssembly module"),
            [".mp3"] = ("audio/mpeg", "audio"),
            [".wav"] = ("audio/wav", "audio"),
            [".flac"] = ("audio/flac", "audio"),
            [".ogg"] = ("audio/ogg", "audio"),
            [".m4a"] = ("audio/mp4", "audio"),
            [".aac"] = ("audio/aac", "audio"),
            [".mp4"] = ("video/mp4", "video"),
            [".mov"] = ("video/quicktime", "video"),
            [".avi"] = ("video/x-msvideo", "video"),
            [".mkv"] = ("video/x-matroska", "video"),
            [".webm"] = ("video/webm", "video"),
            [".ico"] = ("image/x-icon", "icon"),
            [".heic"] = ("image/heic", "image"),
            [".psd"] = ("image/vnd.adobe.photoshop", "Photoshop image"),
            [".ttf"] = ("font/ttf", "font"),
            [".otf"] = ("font/otf", "font"),
            [".woff"] = ("font/woff", "font"),
            [".woff2"] = ("font/woff2", "font"),
            [".sqlite"] = ("application/vnd.sqlite3", "SQLite database"),
            [".parquet"] = ("application/vnd.apache.parquet", "Parquet file"),
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The media type and description named in a refusal for <paramref name="extension"/>: the
    /// known entry, or <c>application/octet-stream</c> / "binary file" for one detected by content.
    /// </summary>
    /// <param name="extension">The file extension, with its leading dot.</param>
    public static (string MediaType, string Description) Describe(string extension)
        => KnownBinaryTypes.TryGetValue(extension, out var known)
            ? known
            : ("application/octet-stream", "binary file");

    /// <summary>
    /// Classifies a leading sample of a file. Returns the encoding to decode it with when it is text,
    /// or a reason (and a <c>null</c> encoding) when it is binary.
    /// </summary>
    /// <param name="sample">The buffer holding the sample.</param>
    /// <param name="count">How many bytes of <paramref name="sample"/> are valid.</param>
    /// <param name="sampleIsWholeFile">True when the sample is the entire file, so a multi-byte
    /// sequence cut at its end is invalid rather than merely incomplete.</param>
    public static (Encoding? Encoding, string? BinaryReason) Classify(byte[] sample, int count, bool sampleIsWholeFile)
    {
        var span = sample.AsSpan(0, Math.Min(count, sample.Length));
        if (span.Length == 0)
            return (new UTF8Encoding(false), null);

        // A byte-order mark decides the encoding outright (UTF-16 text legitimately carries NULs).
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return (new UTF8Encoding(false), null);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return (Encoding.Unicode, null);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return (Encoding.BigEndianUnicode, null);

        if (span.IndexOf((byte)0) >= 0)
            return (null, "it contains NUL bytes");

        if (IsValidUtf8(span, sampleIsWholeFile))
            return (new UTF8Encoding(false), null);

        // Not UTF-8. Legacy single-byte text (a Latin-1 CSV) has no control characters beyond
        // whitespace; binary data is full of them.
        foreach (var b in span)
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C))
                return (null, "it is not valid UTF-8 text");
        return (Encoding.Latin1, null);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> span, bool flush)
    {
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        try
        {
            decoder.GetCharCount(span, flush);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>
/// The outcome of a bounded text read of a content file (<see cref="ContentCollection.GetBoundedText"/>).
/// Either <see cref="Text"/> is set — possibly cut at the byte bound, which <see cref="Truncated"/>
/// states — or <see cref="BinaryReason"/> names why the file is binary and no text was decoded.
/// </summary>
/// <param name="Text">The decoded text, or <c>null</c> when the file is binary.</param>
/// <param name="TotalBytes">The file's size in bytes.</param>
/// <param name="ReturnedBytes">How many of the file's bytes <see cref="Text"/> was decoded from.</param>
/// <param name="Truncated">True when the file is larger than the bound and <see cref="Text"/> is its head only.</param>
/// <param name="BinaryReason">Why the file was classified as binary, or <c>null</c> when it is text.</param>
public sealed record BoundedTextRead(string? Text, long TotalBytes, long ReturnedBytes, bool Truncated, string? BinaryReason)
{
    /// <summary>True when the file was classified as binary and no text was decoded.</summary>
    public bool IsBinary => BinaryReason is not null;
}
