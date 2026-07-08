using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using MeshWeaver.ContentCollections;
using MeshWeaver.Documentation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Headless local mesh host: the in-process ("monolith") mesh backed by SQLite, exposed over gRPC. This is
// the JS-world counterpart of the MAUI client's in-process mesh — MAUI embeds the mesh in the C# app; a
// React Native / web client is JavaScript, so the mesh runs here as a local sidecar and the client reaches
// it over the gRPC bridge (bidi Open for Node/.NET, the gRPC-web Connect+Deliver split for browser/RN).
// NOT the Blazor portal (Memex.Portal.Monolith): no AspNetCore UI, just the mesh + gRPC.

var builder = WebApplication.CreateBuilder(args);

// One cleartext port serving both gRPC transports: HTTP/2 (h2c) for the bidi Open, HTTP/1.1 for gRPC-web.
// Local sidecar → no TLS; the client points at http://localhost:<port>.
var port = builder.Configuration.GetValue("Grpc:Port", 5250);
builder.WebHost.ConfigureKestrel(k =>
    k.ListenLocalhost(port, l => l.Protocols = HttpProtocols.Http1AndHttp2));

// SQLite file under the OS local-app-data (same shape as the MAUI client's memex-local.db).
var dbPath = builder.Configuration["Sqlite:Path"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Memex", "memex-local.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.UseMeshWeaver(
    AddressExtensions.CreateMeshAddress("local"),
    mesh => mesh
        .AddPartitionedSqlitePersistence($"Data Source={dbPath}")
        .AddGraph()          // node types + graph
        .AddKernel()         // C# kernel (Roslyn, MeshWeaver.Kernel.Hub) — lets doc code samples Run on the sidecar
        .AddDocumentation()  // the embedded "Doc" partition — real layout areas the client can render
        .AddGrpcHub()        // py/node stream-routed address types + the gRPC services
        .UseMonolithMesh()); // in-process single-silo runtime (NOT Orleans)

var app = builder.Build();

// Serve the packaged web client (the React-Native app exported to web, baked into wwwroot) from the SAME
// origin as the gRPC endpoint. Same origin ⇒ the browser makes no cross-origin request ⇒ no CORS at all.
// The whole thing is encapsulated in this one backend: open http://localhost:<port> and the app talks to
// its own origin for the mesh.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMeshWeaverGrpcWeb();     // browser / React-Native gRPC-web (Connect + Deliver)
app.MapMeshWeaverGrpc();        // the mesh gRPC service (Open + Connect + Deliver)

// Serve mesh content collections at /static/{collection}/{filePath} — e.g. the NodeType icons
// (/static/NodeTypeIcons/box.svg) the doc headers <img> reference. The full Blazor portal maps this
// (BlazorHostingExtensions.MapStaticContent); the headless sidecar didn't, so those icons 404'd and the
// React-Native client couldn't render them. AddGraph() already registers the NodeTypeIcons collection
// (embedded resources) on the mesh hub — this just exposes the "known collection" pattern over HTTP.
app.MapGet("/static/{**path}", async (HttpContext ctx, string path) =>
{
    var slash = path.IndexOf('/');
    if (slash <= 0) return Results.NotFound();
    // Match BlazorHostingExtensions: collection names encode '/' as '~', and file-path segments are
    // percent-encoded - decode both so names/files with spaces or UTF-8 resolve instead of false-404ing.
    var collection = ContentCollectionsExtensions.DecodeCollectionName(path[..slash]);   // "~" -> "/"
    var filePath = string.Join('/', path[(slash + 1)..].Split('/').Select(Uri.UnescapeDataString));
    var content = app.Services.GetRequiredService<IMessageHub>().ServiceProvider.GetService<IContentService>();
    if (content is null) return Results.NotFound();
    var stream = await content.GetContentAsync(collection, filePath, ctx.RequestAborted);
    if (stream is null) return Results.NotFound();
    var contentType = filePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml"
        : filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
        : filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
        : "application/octet-stream";
    return Results.Stream(stream, contentType);
});

// Server-side Markdig render (POST /api/mesh/render-markdown) — the ONE markdown parser (the web portal
// exposes this via MeshApiEndpoints/MeshOperations.RenderMarkdown, but that surface is MCP-auth-gated and
// portal-coupled). RenderMarkdown is a thin wrapper over the pure MarkdownViewLogic.Render pipeline, so the
// headless sidecar calls it directly — anonymous, no hub round-trip. Clients (portal-next + React-Native)
// POST {markdown, nodePath} and hydrate the returned HTML + codeSubmissions (splitRenderedHtml). This is
// what makes interactive markdown — inline @@ embeds and runnable code cells — resolve on the RN app.
app.MapPost("/api/mesh/render-markdown", async (RenderMarkdownBody body, CancellationToken ct) =>
{
    var result = MarkdownViewLogic.Render(body.Markdown ?? string.Empty, body.NodePath, body.NodePath);
    // The pure Markdig pass can't tell `@@node/path` (a node embed) from `@@node/area/id` — it has no
    // catalog, so it emits a POSITIONAL address/area/id split. Resolve each layout-area marker's raw-path
    // against the mesh's IPathResolver (the same longest-node-prefix resolution the portal does at runtime):
    // when the WHOLE raw-path is itself a node (empty remainder), rewrite to a node/default-area embed so the
    // client subscribes to the right node. Area/content/keyword embeds (non-empty remainder) keep the parser's
    // resolution untouched. (A CHILD node that isn't independently addressable — e.g. a Code cell — stays a
    // remainder and is left as-is; rendering those is a separate mesh-model concern.)
    var resolver = app.Services.GetRequiredService<IMessageHub>().ServiceProvider.GetService<IPathResolver>();
    var html = resolver is null ? result.Html : await ResolveLayoutAreaMarkers(result.Html, resolver, ct);
    return Results.Json(new
    {
        html,
        codeSubmissions = (result.CodeSubmissions ?? [])
            .Select(sub => new { id = sub.Id, language = sub.Language, code = sub.Code }),
    });
});

// Rewrite layout-area markers whose raw-path is a WHOLE node (empty remainder) to a node/default-area embed,
// using the mesh's IPathResolver (longest-node-prefix). Leaves area/content/keyword markers as the pure
// Markdig pass emitted them. This makes a regular `@@node` embed resolve to the right node on the client.
static async Task<string> ResolveLayoutAreaMarkers(string html, IPathResolver resolver, CancellationToken ct)
{
    var matches = Regex.Matches(html, @"<div class='layout-area'[^>]*?data-raw-path='([^']*)'[^>]*?></div>");
    if (matches.Count == 0)
        return html;
    var sb = new StringBuilder();
    var last = 0;
    foreach (Match m in matches)
    {
        sb.Append(html, last, m.Index - last);
        var rawPath = HttpUtility.HtmlDecode(m.Groups[1].Value);
        AddressResolution? res = null;
        try { res = await resolver.ResolvePath(rawPath).FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask(ct); }
        catch { /* unresolved / timed out — keep the parser's marker */ }
        if (res is not null && !string.IsNullOrEmpty(res.Prefix) && string.IsNullOrEmpty(res.Remainder))
            sb.Append($"<div class='layout-area' data-raw-path='{HttpUtility.HtmlAttributeEncode(rawPath)}' data-address='{HttpUtility.HtmlAttributeEncode(res.Prefix)}' data-area='' data-area-id=''></div>");
        else
            sb.Append(m.Value);
        last = m.Index + m.Length;
    }
    sb.Append(html, last, html.Length - last);
    return sb.ToString();
}

var wwwroot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (File.Exists(Path.Combine(wwwroot, "index.html")))
    app.MapFallbackToFile("index.html"); // SPA fallback: any non-gRPC, non-file route → the packaged app
else
    app.MapGet("/", () => Results.Text(
        $"MeshWeaver local mesh — monolith runtime, SQLite at {dbPath}. gRPC on this endpoint " +
        $"(http/2 bidi + gRPC-web). No web app in wwwroot; point a client at http://localhost:{port}."));

app.Run();

/// <summary>POST body for /api/mesh/render-markdown — mirrors MeshApiEndpoints.RenderMarkdownBody.</summary>
internal sealed record RenderMarkdownBody(string? Markdown, string? NodePath);
