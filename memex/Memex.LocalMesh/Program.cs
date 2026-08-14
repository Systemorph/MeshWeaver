using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Memex.LocalMesh;
using MeshWeaver.ContentCollections;
using MeshWeaver.Documentation;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Messaging;
using MeshWeaver.Speech;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

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

// Bake speech-to-text into the sidecar: default the Whisper endpoint to a local whisper.cpp server
// (deploy/whisper → http://localhost:8080), enabled — so the packaged shells have voice input out of the
// box. Env / appsettings (Speech:Endpoint, Speech:Enabled, Speech:Language) still override, since the
// value we set here is read-then-defaulted from the already-loaded configuration.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Speech:Endpoint"] = builder.Configuration["Speech:Endpoint"] ?? "http://localhost:8080",
    ["Speech:Enabled"] = builder.Configuration["Speech:Enabled"] ?? "true",
});
builder.Services.AddSpeechTranscription(builder.Configuration);

var app = builder.Build();

// Serve the packaged web client (the React-Native app exported to web, baked into wwwroot) from the SAME
// origin as the gRPC endpoint. Same origin ⇒ the browser makes no cross-origin request ⇒ no CORS at all.
// The whole thing is encapsulated in this one backend: open http://localhost:<port> and the app talks to
// its own origin for the mesh.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMeshWeaverGrpcWeb();     // browser / React-Native gRPC-web (Connect + Deliver)
app.MapMeshWeaverGrpc();        // the mesh gRPC service (Open + Connect + Deliver)

// 🚨 PUBLIC BUILD ASSETS ONLY — /static/{mount}/{file} (issue #587).
//
// The React-Native client's doc headers reference /static/NodeTypeIcons/box.svg; the full Blazor
// portal serves those from the mesh's StaticAssetMounts, and this headless sidecar mirrors it.
// A mount reads its bytes straight out of a shipped assembly's manifest — no content service, no
// hub, no identity. That is the whole contract: this endpoint performs NO access check, so nothing
// access-controlled may be reachable through it.
//
// It used to resolve ANY registered content collection by name, which made every partition's
// uploads world-readable here (the sidecar resolves no identity at all). Mesh content is served by
// the portal's access-controlled /api/content route instead; this sidecar does not serve it.
app.MapGet("/static/{**path}", (HttpContext ctx, string path) =>
{
    var mounts = app.Services.GetRequiredService<IMessageHub>().ServiceProvider
        .GetServices<StaticAssetMount>();
    // Decode FIRST, then validate: `%2E%2E` survives the server's URL normalization and only
    // becomes `..` here (StaticAssetMount.Open guards the decoded path).
    var decoded = string.Join('/', path.Split('/').Select(Uri.UnescapeDataString));
    var slash = decoded.IndexOf('/');
    if (slash <= 0) return Results.NotFound();
    var segment = decoded[..slash];
    var filePath = decoded[(slash + 1)..];
    var mount = mounts.FirstOrDefault(m => string.Equals(m.Segment, segment, StringComparison.OrdinalIgnoreCase));
    var stream = mount?.Open(filePath);
    if (stream is null) return Results.NotFound();
    var contentType = filePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml"
        : filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
        : filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg"
        : "application/octet-stream";
    return Results.Stream(stream, contentType);
});

// The mesh REST verbs the JS shells this host serves reach over plain HTTP — render-markdown,
// query-nodes, content/list, upload. They live in LocalMeshApiEndpoints so a test can assert the
// route table without booting the mesh: an /api/mesh/* route that is NOT mapped falls through to
// MapFallbackToFile below and answers index.html with a 200, which the client can only report as a
// JSON parse error (issue #1474).
app.MapLocalMeshApi();

// Speech-to-text (POST /api/speech/transcribe) — the SAME client contract the portal exposes
// (Memex.Portal.Shared/Api/SpeechEndpoints), here anonymous on the local sidecar. Every shell served by
// this backend (RN, the macOS/Windows desktop apps, the web app) POSTs multipart { file, language? } and
// gets back {"text","language"}. The transcriber forwards to the configured whisper.cpp server on the HTTP
// IIoPool; a missing/disabled endpoint returns 503 (mic UI stays hidden), a Whisper fault surfaces as 502.
app.MapPost("/api/speech/transcribe", async (HttpContext http, ISpeechTranscriber transcriber, CancellationToken ct) =>
{
    if (!transcriber.IsConfigured)
        return Results.Json(new { error = "Speech transcription is not configured (no Whisper endpoint, or disabled)." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!http.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Content-Type must be multipart/form-data." });

    var form = await http.Request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Multipart file part 'file' is required." });
    if (file.Length > 25L * 1024 * 1024) // bound the in-memory buffer (a minute of WAV ≈ a few MB)
        return Results.Json(new { error = $"Audio too large: {file.Length} bytes (max {25L * 1024 * 1024})." },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    using var ms = new MemoryStream();
    await using (var stream = file.OpenReadStream())
        await stream.CopyToAsync(ms, ct);

    var options = new SpeechTranscriptionOptions
    {
        Language = form["language"].FirstOrDefault() is { Length: > 0 } lang ? lang : null,
    };
    if (!string.IsNullOrWhiteSpace(file.ContentType)) options = options with { ContentType = file.ContentType };
    if (!string.IsNullOrWhiteSpace(file.FileName)) options = options with { FileName = file.FileName };

    try
    {
        var transcript = await transcriber.Transcribe(ms.ToArray(), options).FirstAsync().ToTask(ct);
        return Results.Json(new { text = transcript.Text, language = transcript.Language });
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Transcription failed: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
    }
});

var wwwroot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (File.Exists(Path.Combine(wwwroot, "index.html")))
    app.MapFallbackToFile("index.html"); // SPA fallback: any non-gRPC, non-file route → the packaged app
else
    app.MapGet("/", () => Results.Text(
        $"MeshWeaver local mesh — monolith runtime, SQLite at {dbPath}. gRPC on this endpoint " +
        $"(http/2 bidi + gRPC-web). No web app in wwwroot; point a client at http://localhost:{port}."));

app.Run();

/// <summary>
/// Exposed so a test project can reference this host and assert its route table (the mesh REST verbs
/// in <see cref="Memex.LocalMesh.LocalMeshApiEndpoints"/>) without booting the mesh.
/// </summary>
public partial class Program;
