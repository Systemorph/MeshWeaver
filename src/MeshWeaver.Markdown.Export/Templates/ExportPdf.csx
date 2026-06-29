// Built-in export template — renders a Markdown node (and optional descendants)
// to PDF. Triggered via ExecuteScriptRequest with Inputs:
//   sourcePath:     string  (required) — mesh path of the markdown source
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

Log.LogInformation("Loading source markdown {Path}", sourcePath);
var rootNode = await Mesh.GetMeshNode(sourcePath, TimeSpan.FromSeconds(15)).ToTask(Ct);
if (rootNode is null)
    throw new InvalidOperationException("Source node not found: " + sourcePath);

var title = explicitTitle ?? options.Title ?? rootNode.Name ?? rootNode.Id;

Log.LogInformation("Resolving branding");
var brandingResolver = Mesh.ServiceProvider.GetRequiredService<BrandingResolver>();
var branding = await brandingResolver.Resolve(brandPath).FirstAsync().ToTask(Ct);

var chapters = new List<(string, string)>
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

Log.LogInformation("Rendering PDF");
var document = new DocumentBuilder().Build(title, chapters, options, branding);
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

static string Sanitize(string s)
{
    var invalid = Path.GetInvalidFileNameChars();
    var name = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    return string.IsNullOrEmpty(name) ? "Document" : name;
}
