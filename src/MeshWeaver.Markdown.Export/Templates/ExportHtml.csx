// Built-in export template — renders a Markdown node (and optional descendants) to a
// self-contained, EMAIL-CLIENT-SAFE HTML document.
//
// The one thing this does that Pdf/Docx cannot: it RESOLVES EMBEDDED LAYOUT AREAS. The other two
// templates feed the node's raw markdown into a bare Markdig pipeline that has never heard of the
// `@@(...)` embed syntax, so an embed lands in the output as literal source text. Here the
// framework's own pipeline renders the markdown (emitting the same layout-area anchors the portal
// emits) and EmailDocumentComposer then reads each area's live control tree off its
// synchronization stream and serializes it to static, table-based markup.
//
// Triggered via ExecuteScriptRequest with Inputs:
//   sourcePath:     string  (required) — mesh path of the markdown source
//   title:          string  (optional) — document title; defaults to node.Name
//   options:        object  (optional) — DocumentExportOptions JSON (IncludeChildren, MaxDepth, …)
// Returns: RenderedDocument (Format, FileName, MimeType, Content) — written to
//   ActivityLog.ReturnValue on terminal status; subscribers deserialize it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Email;
using MeshWeaver.Markdown.Export.Messaging;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

if (!Inputs.TryGetValue("sourcePath", out var sourcePathEl) || sourcePathEl.ValueKind != JsonValueKind.String)
    throw new InvalidOperationException("Inputs.sourcePath is required");
var sourcePath = sourcePathEl.GetString();

// Deserialize with the MESH's serializer options — the mesh writes camelCase and System.Text.Json
// defaults to case-SENSITIVE PascalCase, so a bare Deserialize<T>() would bind nothing and
// silently discard every option the user chose (the exact bug the Pdf template documents).
var options = Inputs.TryGetValue("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Object
    ? (optionsEl.Deserialize<DocumentExportOptions>(Mesh.JsonSerializerOptions)
       ?? new DocumentExportOptions { Format = ExportFormat.Html })
    : new DocumentExportOptions { Format = ExportFormat.Html };

var explicitTitle = Inputs.TryGetValue("title", out var t) && t.ValueKind == JsonValueKind.String
    ? t.GetString()
    : null;

Log.LogInformation("Loading source {Path}", sourcePath);
var rootNode = await Mesh.GetMeshNode(sourcePath, TimeSpan.FromSeconds(15)).ToTask(Ct);
if (rootNode is null)
    throw new InvalidOperationException("Source node not found: " + sourcePath);

var jsonOptions = Mesh.JsonSerializerOptions;
var isDeck = rootNode.NodeType == DeckNodeType.NodeType;
var title = explicitTitle
            ?? options.Title
            ?? (isDeck ? rootNode.ContentAs<DeckContent>(jsonOptions)?.Title : null)
            ?? rootNode.Name
            ?? rootNode.Id;

// One markdown body for the whole export: the node's own, plus each requested descendant as a
// section. Composing ONCE (rather than per chapter) means the area resolution and the sanitising
// pass each run a single time over the finished document.
var markdown = new StringBuilder(ExtractMarkdown(rootNode));

if (isDeck)
{
    // A deck's body lives on its SLIDES, not on the deck node — without this the export would
    // compose an empty document. Slide selection uses the SAME resolution the live Overview /
    // Present binding uses, so the email reads in the deck's own order.
    markdown.Clear();
    var (paths, query) = DeckLayoutAreas.ResolveDeckSelection(rootNode, sourcePath, jsonOptions);
    var slides = new List<MeshNode>();
    if (paths.Count > 0)
    {
        foreach (var slidePath in paths)
        {
            var slide = await Mesh.GetMeshNode(slidePath, TimeSpan.FromSeconds(15)).ToTask(Ct);
            if (slide is not null) slides.Add(slide);
        }
    }
    else if (!string.IsNullOrWhiteSpace(query))
    {
        var deckQueryService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var matched = new List<MeshNode>();
        var slideEnumerator = deckQueryService.QueryAsync<MeshNode>(query).GetAsyncEnumerator(Ct);
        try
        {
            while (await slideEnumerator.MoveNextAsync())
            {
                var n = slideEnumerator.Current;
                if (string.Equals(n.Path, sourcePath, StringComparison.Ordinal)) continue;
                if (n.Segments.Skip(1).Any(s => s.StartsWith('_'))) continue;
                matched.Add(n);
            }
        }
        finally
        {
            await slideEnumerator.DisposeAsync();
        }
        slides = matched
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .ToList();
    }

    Log.LogInformation("Deck email export: {Count} slides", slides.Count);
    foreach (var slide in slides)
    {
        var slideMarkdown = slide.ContentAs<SlideContent>(jsonOptions)?.Content ?? ExtractMarkdown(slide);
        if (string.IsNullOrWhiteSpace(slideMarkdown)) continue;
        if (markdown.Length > 0) markdown.AppendLine().AppendLine();
        markdown.Append(slideMarkdown);
    }
}
else if (options.IncludeChildren)
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
            var body = ExtractMarkdown(desc);
            if (string.IsNullOrWhiteSpace(body)) continue;
            markdown.AppendLine().AppendLine();
            markdown.Append("## ").AppendLine(desc.Name ?? desc.Id);
            markdown.AppendLine().Append(body);
        }
    }
    finally
    {
        await enumerator.DisposeAsync();
    }
}

// The portal's public origin — every link and image in the mail is rewritten against it, because
// a mail client has no page origin and a relative href is simply dead when clicked from an inbox.
// Same key order the invitation mailer uses, so the two never disagree about the portal's address.
var configuration = Mesh.ServiceProvider.GetService<IConfiguration>();
var baseUrl = !string.IsNullOrWhiteSpace(options.BaseUrl)
    ? options.BaseUrl
    : configuration?["Portal:BaseUrl"]
      ?? configuration?["PublicBaseUrl"]
      ?? configuration?["Email:WebhookBaseUrl"]
      ?? "";
if (string.IsNullOrWhiteSpace(baseUrl))
    Log.LogWarning(
        "No portal base URL configured (Portal:BaseUrl / PublicBaseUrl / Email:WebhookBaseUrl). "
        + "Relative links and images will stay relative and will NOT resolve from a mail client.");

var emailOptions = new EmailHtmlOptions(baseUrl);

Log.LogInformation("Composing email HTML for {Path} (baseUrl={BaseUrl})", sourcePath, baseUrl);
var html = await EmailDocumentComposer
    .Compose(Mesh, title, markdown.ToString(), sourcePath, emailOptions, Log)
    .FirstAsync()
    .ToTask(Ct);

var bytes = Encoding.UTF8.GetBytes(html);
Log.LogInformation("Rendered {Bytes} bytes of email-safe HTML", bytes.Length);

return new RenderedDocument(
    ExportFormat.Html,
    Sanitize(title) + ".html",
    "text/html",
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
