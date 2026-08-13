// Built-in export template — renders a Markdown node (and optional descendants)
// to DOCX. Same Inputs shape as ExportPdf.csx; differs only in the renderer.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Data;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Docx;
using MeshWeaver.Markdown.Export.Html;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

if (!Inputs.TryGetValue("sourcePath", out var sourcePathEl) || sourcePathEl.ValueKind != JsonValueKind.String)
    throw new InvalidOperationException("Inputs.sourcePath is required");
var sourcePath = sourcePathEl.GetString();

// 🚨 Deserialize with the MESH's serializer options — the same ones ExportDocumentHandler
// serialized these inputs with. The mesh writes camelCase; System.Text.Json's defaults are
// case-SENSITIVE PascalCase, so a bare Deserialize<T>() bound nothing and every option the user
// chose in the dialog was silently discarded.
var options = Inputs.TryGetValue("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Object
    ? (optionsEl.Deserialize<DocumentExportOptions>(Mesh.JsonSerializerOptions)
       ?? new DocumentExportOptions { Format = ExportFormat.Docx })
    : new DocumentExportOptions { Format = ExportFormat.Docx };

var brandPath = Inputs.TryGetValue("brandNodePath", out var b) && b.ValueKind == JsonValueKind.String
    ? b.GetString()
    : null;

var explicitTitle = Inputs.TryGetValue("title", out var t) && t.ValueKind == JsonValueKind.String
    ? t.GetString()
    : null;

Log.LogInformation("Loading source markdown {Path}", sourcePath);
var rootNode = await Mesh.GetMeshNode(sourcePath, TimeSpan.FromSeconds(15)).ToTask(Ct);
if (rootNode is null)
    throw new InvalidOperationException("Source node not found: " + sourcePath);

var title = explicitTitle ?? options.Title ?? rootNode.Name ?? rootNode.Id;

var jsonOptions = Mesh.JsonSerializerOptions;

Log.LogInformation("Resolving branding");
var brandingResolver = Mesh.ServiceProvider.GetRequiredService<BrandingResolver>();
var branding = await brandingResolver.Resolve(brandPath).FirstAsync().ToTask(Ct);

var chapters = new List<ExportChapter>
{
    new ExportChapter(title, ExportSource.MarkdownOf(rootNode, jsonOptions, Log), sourcePath)
};
if (options.IncludeChildren)
{
    Log.LogInformation("Collecting descendants");
    var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    var rootDepth = sourcePath.Count(c => c == '/');
    // One-shot snapshot off the LIVE query surface: filter on the emission SHAPE
    // (Initial), never on a count — a change feed can race the initial-snapshot path.
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

// Resolve embedded layout areas BEFORE the (synchronous) document build — see ExportPdf.csx for
// why this is a separate pass. Without it a `@@(…)` embed lands in Word as a visible notice
// instead of the view the author placed; before this change it landed as literal source text.
Log.LogInformation("Resolving embedded layout areas");
var resolvedAreas = await DocumentAreaResolution
    .Resolve(Mesh, chapters, new DocumentHtmlOptions(PortalBaseUrl(Mesh, options)), Log)
    .FirstAsync()
    .ToTask(Ct);

Log.LogInformation("Rendering DOCX");
var document = new DocumentBuilder(sourcePath, resolvedAreas)
    .Build(title, chapters, options, branding);
var bytes = new DocxDocumentRenderer().Render(document);
Log.LogInformation("Rendered {Bytes} bytes", bytes.Length);

return new RenderedDocument(
    ExportFormat.Docx,
    Sanitize(title) + ".docx",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    bytes);

// 🚨 There is no local ExtractMarkdown — the body extraction is ExportSource.MarkdownOf, in
// COMPILED framework code. See ExportPdf.csx for why (#1374): a `node.Content is MarkdownContent`
// test is false for a body stored as plain JSON, and the export silently loses it.

// The portal's public origin — a resolved area's links are dead without one once the file leaves
// this machine. Same key order as ExportPdf.csx and the HTML export.
static string PortalBaseUrl(IMessageHub mesh, DocumentExportOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.BaseUrl)) return options.BaseUrl;
    var configuration = mesh.ServiceProvider.GetService<IConfiguration>();
    return configuration?["Portal:BaseUrl"]
           ?? configuration?["PublicBaseUrl"]
           ?? configuration?["Email:WebhookBaseUrl"]
           ?? "";
}

static string Sanitize(string s)
{
    var invalid = Path.GetInvalidFileNameChars();
    var name = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    return string.IsNullOrEmpty(name) ? "Document" : name;
}
