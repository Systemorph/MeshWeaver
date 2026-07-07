using MeshWeaver.ContentCollections;
using MeshWeaver.Documentation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Sqlite;
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

var wwwroot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (File.Exists(Path.Combine(wwwroot, "index.html")))
    app.MapFallbackToFile("index.html"); // SPA fallback: any non-gRPC, non-file route → the packaged app
else
    app.MapGet("/", () => Results.Text(
        $"MeshWeaver local mesh — monolith runtime, SQLite at {dbPath}. gRPC on this endpoint " +
        $"(http/2 bidi + gRPC-web). No web app in wwwroot; point a client at http://localhost:{port}."));

app.Run();
