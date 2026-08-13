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
using MeshWeaver.Markdown.Export.Html;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Markdown.Export.Pdf;
using MeshWeaver.Markdown.Export.Pixel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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

List<ExportChapter> chapters;
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
        // One-shot snapshot off the LIVE query surface: filter on the emission SHAPE
        // (Initial), never on a count — a change feed can race the initial-snapshot path.
        var slideMatches = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => c.Items)
            .FirstAsync()
            .ToTask(Ct);
        // Drop the deck root itself and any '_'-prefixed governance node — same
        // filtering the live query binding applies (see DeckLayoutAreas.ObserveQuerySlides).
        var matched = slideMatches
            .Where(n => !string.Equals(n.Path, sourcePath, StringComparison.Ordinal))
            .Where(n => !n.Segments.Skip(1).Any(s => s.StartsWith('_')));
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

        // A slide can embed a LIVE layout area with `@@(…)`. The markdown pipeline emits an EMPTY
        // anchor div for it and leaves the filling to a browser session — but this print document
        // is loaded from file:// under a CSP of `default-src 'none'` with the browser's resolver
        // pointed at nothing, so it can never fetch the area. Unresolved, the embed prints as a
        // silent blank. Resolve it here, server-side, BEFORE asset inlining so any content the area
        // itself references gets inlined too.
        //
        // Only round-trip the DOM when there is actually something to resolve: a deck with no
        // embeds must come out of this byte-identical to what the composer produced.
        if (html.Contains(LayoutAreaMarkdownRenderer.LayoutArea, StringComparison.Ordinal))
        {
            var printDocument = new HtmlAgilityPack.HtmlDocument();
            printDocument.LoadHtml(html);
            var areaResult = await LayoutAreaResolver
                .Resolve(printDocument, Mesh, new DocumentHtmlOptions(PortalBaseUrl(Mesh, options)), Log)
                .FirstAsync()
                .ToTask(Ct);
            Log.LogInformation("Pixel export resolved {Resolved} layout-area embed(s), {Unresolved} unavailable",
                areaResult.Resolved, areaResult.Unresolved);
            html = printDocument.DocumentNode.OuterHtml;
        }

        // Images live behind the access-controlled api/content route, which a file:// document
        // cannot fetch. Resolve them here — under the exporting user's identity — and inline them
        // so the print document is genuinely self-contained. An asset that cannot be read is left
        // as a link and named in the log: under the print document's CSP that is a BLANK image, so
        // it must never pass unremarked.
        var inlining = await SlideAssetInliner.Inline(html, Mesh).FirstAsync().ToTask(Ct);
        html = inlining.Html;
        foreach (var unresolved in inlining.Unresolved)
            Log.LogWarning("Slide asset {Reference} could not be inlined ({Reason}); it will print "
                           + "as a blank image", unresolved.Reference, unresolved.Reason);
        Log.LogInformation("Inlined {Inlined}/{Total} slide assets",
            inlining.Inlined.Length, inlining.Inlined.Length + inlining.Unresolved.Length);

        var pixelBytes = await renderer.Render(html).FirstAsync().ToTask(Ct);
        Log.LogInformation("Rendered {Bytes} bytes (pixel-faithful)", pixelBytes.Length);

        return new RenderedDocument(
            ExportFormat.Pdf,
            Sanitize(title) + ".pdf",
            "application/pdf",
            pixelBytes);
    }

    chapters = slideNodes
        .Select(s => new ExportChapter(s.Name ?? s.Id, ExtractSlideMarkdown(s, jsonOptions), s.Path))
        .ToList();
    if (chapters.Count == 0)
        chapters.Add(new ExportChapter(title, "*This deck has no slides yet.*", sourcePath));

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
chapters = new List<ExportChapter>
    {
        new ExportChapter(title, ExportSource.MarkdownOf(rootNode, jsonOptions, Log), sourcePath)
    };
    if (options.IncludeChildren)
    {
        Log.LogInformation("Collecting descendants");
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var rootDepth = sourcePath.Count(c => c == '/');
        var descendants = await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery("path:" + sourcePath + " scope:descendants"))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => c.Items)
            .FirstAsync()
            .ToTask(Ct);
        foreach (var desc in descendants)
        {
            if (options.MaxDepth > 0)
            {
                var depth = desc.Path.Count(c => c == '/') - rootDepth;
                if (depth > options.MaxDepth) continue;
            }
            var md = ExportSource.MarkdownOf(desc, jsonOptions, Log);
            if (!string.IsNullOrWhiteSpace(md))
                chapters.Add(new ExportChapter(desc.Name ?? desc.Id, md, desc.Path));
        }
        Log.LogInformation("Collected {Count} chapters", chapters.Count);
    }
}

Log.LogInformation("Resolving branding");
var brandingResolver = Mesh.ServiceProvider.GetRequiredService<BrandingResolver>();
var branding = await brandingResolver.Resolve(brandPath).FirstAsync().ToTask(Ct);

// Resolve embedded layout areas BEFORE the (synchronous) document build. Reading an area is
// reactive and cross-hub; the builder walk is not. Without this pass the export's Markdig pipeline
// still PARSES `@@(…)` but has nothing to put there, and the embed degrades to a visible notice
// instead of the view the author placed.
Log.LogInformation("Resolving embedded layout areas");
var resolvedAreas = await DocumentAreaResolution
    .Resolve(Mesh, chapters, new DocumentHtmlOptions(PortalBaseUrl(Mesh, options)), Log)
    .FirstAsync()
    .ToTask(Ct);

Log.LogInformation("Rendering PDF");
var document = new DocumentBuilder(sourcePath, resolvedAreas)
    .Build(title, chapters, effectiveOptions, branding);
// The content-faithful renderer composes the document model into print HTML and prints it with
// the same headless browser the pixel path uses (#1230 replaced the QuestPDF document model).
// Cold observable — the work happens on subscription, which is what awaiting the first emission
// does here; the browser itself runs on the Process I/O pool, never on this thread's scheduler.
var bytes = await Mesh.ServiceProvider.GetRequiredService<PdfDocumentRenderer>()
    .Render(document)
    .FirstAsync()
    .ToTask(Ct);
Log.LogInformation("Rendered {Bytes} bytes", bytes.Length);

return new RenderedDocument(
    ExportFormat.Pdf,
    Sanitize(title) + ".pdf",
    "application/pdf",
    bytes);

// 🚨 There is no local ExtractMarkdown any more — the body extraction is
// ExportSource.MarkdownOf, in COMPILED framework code. `node.Content is MarkdownContent` was the
// trap-door: a body stored as plain JSON carries no $type, so it stays a raw JsonElement even on
// its own hub, the test is false, and the export prints a cover page, a contents list and NO body,
// silently (#1374). Three .csx copies of that test is how it survived — nothing in this file is
// type-checked by dotnet build, so only a compiled, unit-tested helper can be trusted to stay fixed.

// The portal's public origin. A resolved layout area can carry links, and a link with no origin is
// dead the moment the PDF leaves this machine — the reader has no page to resolve it against.
// Same key order the invitation mailer and the HTML export use, so the three never disagree.
static string PortalBaseUrl(IMessageHub mesh, DocumentExportOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.BaseUrl)) return options.BaseUrl;
    var configuration = mesh.ServiceProvider.GetService<IConfiguration>();
    return configuration?["Portal:BaseUrl"]
           ?? configuration?["PublicBaseUrl"]
           ?? configuration?["Email:WebhookBaseUrl"]
           ?? "";
}

static string ExtractSlideMarkdown(MeshNode node, JsonSerializerOptions options)
{
    var slide = node.ContentAs<SlideContent>(options);
    if (slide?.Content is { } content) return content;
    return ExportSource.MarkdownOf(node, options);
}

static string Sanitize(string s)
{
    var invalid = Path.GetInvalidFileNameChars();
    var name = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    return string.IsNullOrEmpty(name) ? "Document" : name;
}
