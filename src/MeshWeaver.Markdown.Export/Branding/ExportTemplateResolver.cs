using System.IO.Compression;
using System.Reactive.Linq;
using System.Xml.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Branding;

/// <summary>
/// Loads an Export Template (a .docx on disk / in a content collection) and extracts
/// the bits that document renderers care about: the full template bytes for DOCX
/// cloning, plus a small "appearance" payload (logo + font family) that the PDF
/// renderer consumes so both outputs share the same look.
/// </summary>
public class ExportTemplateResolver(IMessageHub hub, ILogger<ExportTemplateResolver> logger)
{
    /// <summary>Resolved template payload, all fields may be null when the template does not contain them.</summary>
    public record ExportTemplate(byte[] DocxBytes, LogoImage? Logo, string? FontFamily);

    /// <summary>
    /// Loads the template bytes and inspects the inner OpenXML package to pull out the
    /// first embedded raster image (used as a logo) and the major font name.
    /// Emits null when the path does not resolve. The content read runs on the collection's
    /// I/O pool; the (pure CPU) package inspection runs on its emission — pure composition,
    /// no async state machine.
    /// </summary>
    public IObservable<ExportTemplate?> Load(string? templatePath)
    {
        if (string.IsNullOrWhiteSpace(templatePath))
            return Observable.Return<ExportTemplate?>(null);

        return LoadBytes(templatePath!)
            .Select(bytes => bytes is null ? null : InspectBytes(bytes, templatePath, logger));
    }

    /// <summary>
    /// Pure function: given raw .docx bytes, produce the <see cref="ExportTemplate"/>.
    /// Exposed for unit tests — production code goes through <see cref="Load"/>.
    /// </summary>
    public static ExportTemplate InspectBytes(byte[] bytes, string? sourceLabel = null, ILogger? logger = null)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

            return new ExportTemplate(bytes, ExtractLogo(zip), ExtractMajorFont(zip));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Template '{Path}' is not a valid .docx package; passing bytes through without logo/font extraction", sourceLabel ?? "<bytes>");
            return new ExportTemplate(bytes, null, null);
        }
    }

    private static LogoImage? ExtractLogo(ZipArchive zip)
    {
        // Prefer PNG/JPEG over SVG for broad downstream compatibility (QuestPDF renders both,
        // but DOCX header media inside the template already ships the image — we only surface
        // it here for the PDF header badge).
        var preferred = new[] { ".png", ".jpg", ".jpeg" };
        foreach (var ext in preferred)
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            using var s = entry.Open();
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            return new LogoImage(copy.ToArray(), ext == ".png" ? "image/png" : "image/jpeg");
        }

        var svg = zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
        if (svg is not null)
        {
            using var s = svg.Open();
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            return new LogoImage(copy.ToArray(), "image/svg+xml");
        }

        return null;
    }

    private static string? ExtractMajorFont(ZipArchive zip)
    {
        // theme1.xml carries the major/minor font scheme — that's the visual identity.
        // Fallback to the first entry in fontTable.xml when theme is absent.
        var theme = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("word/theme/theme1.xml", StringComparison.OrdinalIgnoreCase));
        if (theme is not null)
        {
            using var s = theme.Open();
            var doc = XDocument.Load(s);
            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            var majorLatin = doc.Descendants(a + "majorFont")
                .Elements(a + "latin")
                .FirstOrDefault()
                ?.Attribute("typeface")?.Value;
            if (!string.IsNullOrWhiteSpace(majorLatin))
                return majorLatin;
        }

        var fontTable = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("word/fontTable.xml", StringComparison.OrdinalIgnoreCase));
        if (fontTable is not null)
        {
            using var s = fontTable.Open();
            var doc = XDocument.Load(s);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var first = doc.Root?.Elements(w + "font").FirstOrDefault()?.Attribute(w + "name")?.Value;
            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }

        return null;
    }

    private IObservable<byte[]?> LoadBytes(string path)
    {
        const string staticPrefix = "/static/storage/content/";
        var rel = path.StartsWith("content:", StringComparison.OrdinalIgnoreCase)
            ? path["content:".Length..]
            : path.StartsWith(staticPrefix, StringComparison.OrdinalIgnoreCase)
                ? path[staticPrefix.Length..]
                : null;
        if (rel is null)
        {
            logger.LogInformation("Template path '{Path}' is not a content path; skipping", path);
            return Observable.Return<byte[]?>(null);
        }

        var (collection, subPath) = SplitCollection(rel);
        var contentSvc = hub.ServiceProvider.GetService<IContentService>();
        if (contentSvc is null)
            return Observable.Return<byte[]?>(null);
        return contentSvc.GetCollection(collection)
            .SelectMany(coll => coll is null
                ? Observable.Return<byte[]?>(null)
                : coll.GetContentBytes(subPath))
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Failed to load template bytes from '{Path}'; continuing without template", path);
                return Observable.Return<byte[]?>(null);
            });
    }

    private static (string Collection, string Path) SplitCollection(string rel)
    {
        rel = rel.Replace('\\', '/').TrimStart('/');
        var slash = rel.IndexOf('/');
        return slash < 0 ? (rel, "") : (rel[..slash], rel[(slash + 1)..]);
    }
}
