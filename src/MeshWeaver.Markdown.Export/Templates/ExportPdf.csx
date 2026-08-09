// Built-in export template — renders a Markdown node (and optional descendants), OR a
// Deck node (one chapter per slide, in the deck's manifest/query order), to PDF.
// Triggered via ExecuteScriptRequest with Inputs:
//   sourcePath:     string  (required) — mesh path of the markdown / deck source
//   title:          string  (optional) — document title; defaults to node.Name
//   brandNodePath:  string  (optional) — CorporateIdentity / Organization path
//   options:        object  (optional) — DocumentExportOptions JSON (IncludeChildren, MaxDepth, …)
// Returns: RenderedDocument (Format, FileName, MimeType, Content) — written to
//   ActivityLog.ReturnValue on terminal status; subscribers deserialize it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Markdown.Export.Pdf;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

if (!Inputs.TryGetValue("sourcePath", out var sourcePathEl) || sourcePathEl.ValueKind != JsonValueKind.String)
    throw new InvalidOperationException("Inputs.sourcePath is required");
var sourcePath = sourcePathEl.GetString();

// 🚨 Deserialize with the MESH's serializer options — the same ones ExportDocumentHandler
// serialized these inputs with. The mesh writes camelCase; System.Text.Json's defaults are
// case-SENSITIVE PascalCase, so a bare Deserialize<T>() bound nothing, returned an all-default
// object, and every option the user chose in the dialog (cover page, table of contents, page
// breaks, include children, header/footer — and now fidelity) was silently discarded.
var options = Inputs.TryGetValue("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Object
    ? (optionsEl.Deserialize<DocumentExportOptions>(Mesh.JsonSerializerOptions)
       ?? new DocumentExportOptions { Format = ExportFormat.Pdf })
    : new DocumentExportOptions { Format = ExportFormat.Pdf };

var brandPath = Inputs.TryGetValue("brandNodePath", out var b) && b.ValueKind == JsonValueKind.String
    ? b.GetString()
    : null;

var explicitTitle = Inputs.TryGetValue("title", out var t) && t.ValueKind == JsonValueKind.String
    ? t.GetString()
    : null;

Log.LogInformation("Loading source {Path}", sourcePath);
var rootNode = await Mesh.GetMeshNode(sourcePath, TimeSpan.FromSeconds(15)).ToTask(Ct);
if (rootNode is null)
    throw new InvalidOperationException("Source node not found: " + sourcePath);

var jsonOptions = Mesh.JsonSerializerOptions;

List<(string, string)> chapters;
DocumentExportOptions effectiveOptions;
string title;

if (rootNode.NodeType == DeckNodeType.NodeType)
{
    // ── Deck → PDF: one chapter per slide, in the deck's own order ──────────────
    var deck = rootNode.ContentAs<DeckContent>(jsonOptions);
    title = explicitTitle ?? options.Title ?? deck?.Title ?? rootNode.Name ?? rootNode.Id;

    // Resolve the deck's slide SELECTION with the SAME logic the live Overview/Present
    // binding uses — the manifest (ordered paths) or, when empty, the deck's query / the
    // default Slide subtree. One source of truth for a deck's order.
    var (paths, query) = DeckLayoutAreas.ResolveDeckSelection(rootNode, sourcePath, jsonOptions);

    var slideNodes = new List<MeshNode>();
    if (paths.Count > 0)
    {
        foreach (var slidePath in paths)
        {
            var slide = await Mesh.GetMeshNode(slidePath, TimeSpan.FromSeconds(15)).ToTask(Ct);
            if (slide is not null)
                slideNodes.Add(slide);
        }
    }
    else if (!string.IsNullOrWhiteSpace(query))
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var matched = new List<MeshNode>();
        var enumerator = meshService.QueryAsync<MeshNode>(query).GetAsyncEnumerator(Ct);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                var n = enumerator.Current;
                // Drop the deck root itself and any '_'-prefixed governance node — same
                // filtering the live query binding applies (see DeckLayoutAreas.ObserveQuerySlides).
                if (string.Equals(n.Path, sourcePath, StringComparison.Ordinal)) continue;
                if (n.Segments.Skip(1).Any(s => s.StartsWith('_'))) continue;
                matched.Add(n);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
        slideNodes = matched
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .ToList();
    }

    Log.LogInformation("Deck export: {Count} slides", slideNodes.Count);

    // ── Pixel-faithful branch ───────────────────────────────────────────────────
    // A deck whose meaning lives in CSS (gradient/image backgrounds, a raw-HTML body,
    // CSS layout, transforms) cannot survive the document-model renderer — it has no
    // notion of any of that. Compose the slides into ONE self-contained HTML document
    // carrying the same stage CSS the live Slide view uses, and let a headless browser
    // print it. Everything below the composer is ordinary reactive C#; the browser is
    // the only leaf, and it is bounded by the Process I/O pool.
    if (options.Fidelity == ExportFidelity.Pixel)
    {
        var renderer = Mesh.ServiceProvider.GetRequiredService<IPixelPdfRenderer>();
        var executable = await renderer.Probe().FirstAsync().ToTask(Ct);
        if (executable is null)
            throw new InvalidOperationException(
                "Pixel-faithful export needs a headless Chromium on the server, and this deployment has none. "
                + "Export with content fidelity instead, or configure MarkdownExport → PixelRendering → "
                + "ExecutablePath (or set the CHROME_BIN environment variable).");

        Log.LogInformation("Pixel-faithful deck export using {Executable}", executable);

        // The slide's own path is BOTH the markdown pipeline's node path (relative links) and its
        // content collection (relative image hrefs) — exactly what the live MarkdownView passes.
        var printSlides = slideNodes
            .Select(s => new PrintSlide(s.ContentAs<SlideContent>(jsonOptions), s.Path, s.Path))
            .ToList();
        if (printSlides.Count == 0)
            printSlides.Add(new PrintSlide(new SlideContent { Content = "*This deck has no slides yet.*" }));

        var html = SlidePrintComposer.Compose(title, printSlides);

        // Images live behind the access-controlled api/content route, which a file:// document
        // cannot fetch. Resolve them here — under the exporting user's identity — and inline them
        // so the print document is genuinely self-contained.
        html = await InlineSlideAssets(html, Mesh, Log, Ct);

        var pixelBytes = await renderer.Render(html).FirstAsync().ToTask(Ct);
        Log.LogInformation("Rendered {Bytes} bytes (pixel-faithful)", pixelBytes.Length);

        return new RenderedDocument(
            ExportFormat.Pdf,
            Sanitize(title) + ".pdf",
            "application/pdf",
            pixelBytes);
    }

    chapters = slideNodes
        .Select(s => (s.Name ?? s.Id, ExtractSlideMarkdown(s, jsonOptions)))
        .ToList();
    if (chapters.Count == 0)
        chapters.Add((title, "*This deck has no slides yet.*"));

    // 16:9 slides read best in landscape, and every slide starts on its own page — and ONLY
    // slide boundaries break pages. A slide whose body opens with a heading must not be split
    // in two by the heading page-break rules; for a deck the slide IS the page. (These two were
    // dead until the options actually reached this script, so a deck that opened its slides with
    // '#' silently gained a blank page per slide the moment they started working.)
    effectiveOptions = options with
    {
        Landscape = true,
        PageBreakBetweenChildren = true,
        PageBreakBeforeH1 = false,
        PageBreakBeforeH2 = false,
    };
}
else
{
    // ── Markdown → PDF: the node's own body plus optional descendant chapters ───
    if (options.Fidelity == ExportFidelity.Pixel)
        Log.LogWarning(
            "Pixel-faithful fidelity applies to Deck → PDF only; {Path} is a {NodeType}. "
            + "Exporting content-faithful.", sourcePath, rootNode.NodeType);
    title = explicitTitle ?? options.Title ?? rootNode.Name ?? rootNode.Id;
    effectiveOptions = options;
    chapters = new List<(string, string)>
    {
        (title, ExtractMarkdown(rootNode))
    };
    if (options.IncludeChildren)
    {
        Log.LogInformation("Collecting descendants");
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var rootDepth = sourcePath.Count(c => c == '/');
        var enumerator = meshService
            .QueryAsync<MeshNode>("path:" + sourcePath + " scope:descendants")
            .GetAsyncEnumerator(Ct);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                var desc = enumerator.Current;
                if (options.MaxDepth > 0)
                {
                    var depth = desc.Path.Count(c => c == '/') - rootDepth;
                    if (depth > options.MaxDepth) continue;
                }
                var md = ExtractMarkdown(desc);
                if (!string.IsNullOrWhiteSpace(md))
                    chapters.Add((desc.Name ?? desc.Id, md));
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
        Log.LogInformation("Collected {Count} chapters", chapters.Count);
    }
}

Log.LogInformation("Resolving branding");
var brandingResolver = Mesh.ServiceProvider.GetRequiredService<BrandingResolver>();
var branding = await brandingResolver.Resolve(brandPath).FirstAsync().ToTask(Ct);

Log.LogInformation("Rendering PDF");
var document = new DocumentBuilder().Build(title, chapters, effectiveOptions, branding);
var bytes = new PdfDocumentRenderer().Render(document);
Log.LogInformation("Rendered {Bytes} bytes", bytes.Length);

return new RenderedDocument(
    ExportFormat.Pdf,
    Sanitize(title) + ".pdf",
    "application/pdf",
    bytes);

// Resolves every `api/content/{collection}/{path}` reference the composed print document carries
// and rewrites it to a data: URI, so the headless browser — which prints from a local file with no
// network and no session — still sees the deck's images. An asset that cannot be read is left as a
// link and logged: a missing picture must not fail the whole export, it renders broken exactly as
// it would on screen.
static async Task<string> InlineSlideAssets(
    string html, IMessageHub mesh, ILogger log, CancellationToken ct)
{
    var references = SlidePrintComposer.CollectAssetReferences(html);
    if (references.Length == 0)
        return html;

    var contentService = mesh.ServiceProvider.GetService<IContentService>();
    if (contentService is null)
    {
        log.LogWarning("No content service available; {Count} slide asset(s) stay as links", references.Length);
        return html;
    }

    var inlined = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var reference in references)
    {
        var parsed = SlidePrintComposer.ParseAssetReference(reference);
        if (parsed is null)
            continue;

        try
        {
            var stream = await contentService
                .GetContent(parsed.Value.Collection, parsed.Value.Path)
                .FirstAsync()
                .ToTask(ct);
            if (stream is null)
            {
                log.LogWarning("Slide asset {Reference} not found; leaving it as a link", reference);
                continue;
            }

            using (stream)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                inlined[reference] = SlidePrintComposer.ToDataUri(parsed.Value.Path, buffer.ToArray());
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not inline slide asset {Reference}; leaving it as a link", reference);
        }
    }

    log.LogInformation("Inlined {Inlined}/{Total} slide assets", inlined.Count, references.Length);
    return SlidePrintComposer.InlineAssets(html, inlined);
}

static string ExtractMarkdown(MeshNode node)
{
    if (node.Content is MarkdownContent mc) return mc.Content ?? "";
    if (node.Content is string s) return s;
    return "";
}

static string ExtractSlideMarkdown(MeshNode node, JsonSerializerOptions options)
{
    var slide = node.ContentAs<SlideContent>(options);
    if (slide?.Content is { } content) return content;
    return ExtractMarkdown(node);
}

static string Sanitize(string s)
{
    var invalid = Path.GetInvalidFileNameChars();
    var name = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    return string.IsNullOrEmpty(name) ? "Document" : name;
}
