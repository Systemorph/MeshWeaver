using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using MeshWeaver.AI;
using MeshWeaver.Hosting;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace Memex.LocalMesh;

/// <summary>
/// The sidecar's <c>/api/mesh/*</c> surface — the SAME verbs the portal exposes through
/// <c>MeshApiEndpoints</c>, here ANONYMOUS (this host is same-origin and authenticates nobody).
///
/// <para>
/// Every JS shell this backend serves out of <c>wwwroot</c> reaches these over plain HTTP because
/// the gRPC message bus does not carry them: a mesh <b>query</b> is <c>IMeshQuery.Query</c>, a
/// service call with no request type to post; content-collection listing and upload move bytes that
/// are not mesh nodes; the markdown render is the one Markdig pipeline.
/// </para>
///
/// <para>
/// 🚨 An <c>/api/mesh/*</c> route this class does NOT map does not 404 — <c>MapFallbackToFile</c>
/// answers it with <c>index.html</c> and a <b>200</b>, so the caller fails inside <c>JSON.parse</c>
/// with nothing naming the missing endpoint. That is issue #1474: only <c>render-markdown</c> was
/// mapped, so the React Native file browser was broken with no diagnosable symptom.
/// <c>clients/grpc-web/src/restContract.test.ts</c> now asserts this map and the portal's cover
/// every verb the client SDK posts to.
/// </para>
///
/// <para>
/// Operations issue on a <see cref="SessionHubFactory"/> session hub — never the root
/// <c>mesh/{id}</c> hub, on which request-shaped work never answers.
/// </para>
/// </summary>
public static class LocalMeshApiEndpoints
{
    /// <summary>Address segment for this host's shared anonymous operations hub.</summary>
    private const string SessionPrefix = "local-api";

    /// <summary>Maps the sidecar's mesh REST verbs. Call before <c>MapFallbackToFile</c>.</summary>
    public static IEndpointRouteBuilder MapLocalMeshApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/mesh");

        // Server-side Markdig render — the ONE markdown parser (the portal exposes this via
        // MeshApiEndpoints/MeshOperations.RenderMarkdown, but that surface is MCP-auth-gated and
        // portal-coupled). RenderMarkdown is a thin wrapper over the pure MarkdownViewLogic.Render
        // pipeline, so the headless sidecar calls it directly — anonymous, no hub round-trip.
        // Clients (portal-next + React-Native) POST {markdown, nodePath} and hydrate the returned
        // HTML + codeSubmissions (splitRenderedHtml). This is what makes interactive markdown —
        // inline @@ embeds and runnable code cells — resolve on the RN app.
        group.MapPost("/render-markdown", async (
            RenderMarkdownBody body, IServiceProvider services, CancellationToken ct) =>
        {
            var result = MarkdownViewLogic.Render(body.Markdown ?? string.Empty, body.NodePath, body.NodePath);
            // The pure Markdig pass can't tell `@@node/path` (a node embed) from `@@node/area/id` — it has no
            // catalog, so it emits a POSITIONAL address/area/id split. Resolve each layout-area marker's raw-path
            // against the mesh's IPathResolver (the same longest-node-prefix resolution the portal does at runtime):
            // when the WHOLE raw-path is itself a node (empty remainder), rewrite to a node/default-area embed so the
            // client subscribes to the right node. Area/content/keyword embeds (non-empty remainder) keep the parser's
            // resolution untouched. (A CHILD node that isn't independently addressable — e.g. a Code cell — stays a
            // remainder and is left as-is; rendering those is a separate mesh-model concern.)
            var resolver = RootHub(services).ServiceProvider.GetService<IPathResolver>();
            var html = resolver is null ? result.Html : await ResolveLayoutAreaMarkers(result.Html, resolver, ct);
            return Results.Json(new
            {
                html,
                codeSubmissions = (result.CodeSubmissions ?? [])
                    .Select(sub => new { id = sub.Id, language = sub.Language, code = sub.Code }),
            });
        });

        // Full-node mesh query — the client's ONE query surface (there is no hub message for a
        // query). Backs MeshSearch / MeshNodeCollection / NodeExport / the ThreadChat selectors.
        group.MapPost("/query-nodes", (
            QueryNodesBody body, IServiceProvider services, CancellationToken ct) =>
            RunString(services, ct, ops => ops.QueryNodes(body.Query, body.Limit ?? 50)));

        // Content-collection directory listing — the read half of the file browser. Path is
        // "{node}/{collection}[/{dir}]".
        group.MapPost("/content/list", (
            PathBody body, IServiceProvider services, CancellationToken ct) =>
            RunString(services, ct, ops => ops.ContentList(body.Path)));

        // Binary upload — multipart, the write half of the file browser. DisableAntiforgery: this
        // host has no antiforgery pipeline and no session to forge against.
        group.MapPost("/upload", HandleUpload).DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> HandleUpload(
        HttpContext http, IServiceProvider services, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { error = "Content-Type must be multipart/form-data." });

        var form = await http.Request.ReadFormAsync(ct);
        var path = form["path"].FirstOrDefault();
        var file = form.Files.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "Form field 'path' is required." });
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "Form file 'file' is required." });

        using var ms = new MemoryStream();
        await using (var stream = file.OpenReadStream())
            await stream.CopyToAsync(ms, ct);

        var bytes = ms.ToArray();
        return await RunString(services, ct, ops => ops.Upload(path, bytes));
    }

    /// <summary>
    /// Runs one <see cref="MeshOperations"/> verb on the session hub and ships its result.
    /// MeshOperations returns either a JSON document or an <c>"Error: …"</c> sentinel string; both
    /// are valid <c>application/json</c> the client branches on (mirrors the MCP-tool contract).
    /// </summary>
    private static async Task<IResult> RunString(
        IServiceProvider services,
        CancellationToken ct,
        Func<MeshOperations, IObservable<string>> work)
    {
        var ops = new MeshOperations(SessionHub(services));
        var result = await work(ops).FirstAsync().ToTask(ct);
        return Results.Content(result, "application/json");
    }

    private static IMessageHub RootHub(IServiceProvider services) =>
        services.GetRequiredService<IMessageHub>();

    /// <summary>
    /// The shared anonymous operations hub. This host resolves no identity, so there is one session
    /// for the whole process — but it is still a <c>portal/…</c> hub, never the root mesh hub.
    /// </summary>
    private static IMessageHub SessionHub(IServiceProvider services)
    {
        var rootHub = RootHub(services);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LocalMeshApiEndpoints));
        return SessionHubFactory.Resolve(rootHub, SessionPrefix, SessionHubFactory.AnonymousSession, logger);
    }

    /// <summary>
    /// Rewrites layout-area markers whose raw-path is a WHOLE node (empty remainder) to a
    /// node/default-area embed, using the mesh's IPathResolver (longest-node-prefix). Leaves
    /// area/content/keyword markers as the pure Markdig pass emitted them.
    /// </summary>
    private static async Task<string> ResolveLayoutAreaMarkers(string html, IPathResolver resolver, CancellationToken ct)
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

    /// <summary>POST body for /api/mesh/render-markdown — mirrors MeshApiEndpoints.RenderMarkdownBody.</summary>
    public record RenderMarkdownBody(string? Markdown, string? NodePath);

    /// <summary>POST body for /api/mesh/query-nodes — mirrors MeshApiEndpoints.QueryNodesBody.</summary>
    public record QueryNodesBody(string Query, int? Limit = null);

    /// <summary>POST body for /api/mesh/content/list — mirrors MeshApiEndpoints.PathBody.</summary>
    public record PathBody(string Path);
}
