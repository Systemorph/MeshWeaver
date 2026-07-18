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
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

if (!Inputs.TryGetValue("sourcePath", out var sourcePathEl) || sourcePathEl.ValueKind != JsonValueKind.String)
    throw new InvalidOperationException("Inputs.sourcePath is required");
var sourcePath = sourcePathEl.GetString();

var options = Inputs.TryGetValue("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Object
    ? (optionsEl.Deserialize<DocumentExportOptions>() ?? new DocumentExportOptions { Format = ExportFormat.Pdf })
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
    chapters = slideNodes
        .Select(s => (s.Name ?? s.Id, ExtractSlideMarkdown(s, jsonOptions)))
        .ToList();
    if (chapters.Count == 0)
        chapters.Add((title, "*This deck has no slides yet.*"));

    // 16:9 slides read best in landscape, and every slide starts on its own page.
    effectiveOptions = options with { Landscape = true, PageBreakBetweenChildren = true };
}
else
{
    // ── Markdown → PDF: the node's own body plus optional descendant chapters ───
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
