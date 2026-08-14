using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Blazor.Infrastructure; // PortalApplication
using MeshWeaver.Mcp;
using MeshWeaver.Mesh.Security;         // WellKnownUsers
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;             // AccessService
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// REST surface for the mesh — a transport-mirror of <c>McpMeshPlugin</c>.
///
/// <para>
/// Every endpoint is a thin wrapper over <see cref="MeshOperations"/> (the same
/// shared core that backs the MCP tools), so REST and MCP cannot drift: a change
/// to a verb's semantics happens once, in <c>MeshOperations</c>, and both
/// transports inherit it.
/// </para>
///
/// <para>
/// <b>Auth</b>: mutating verbs are gated by <c>McpAuthenticationExtensions.PolicyName</c> —
/// same <c>Authorization: Bearer mw_…</c> token format as <c>/mcp</c>, validated by
/// <c>ApiTokenAuthenticationHandler</c>. The READ-ONLY verbs additionally accept the
/// portal's own session cookie (<c>McpAuthenticationExtensions.ReadPolicyName</c>) so a
/// server-side renderer holding the user's cookie never has to mint an API token — every
/// mint writes two permanent mesh nodes, which made ordinary page traffic grow a user's
/// partition without bound (issue #1477).
/// </para>
///
/// <para>
/// <b>Session hub</b>: each request resolves a per-caller hosted hub via
/// <see cref="SessionHubResolver"/> (shared with the MCP plugin), so REST callers
/// get the same routing semantics that MCP already has — kernel dispatch, workspace
/// isolation, response routing back to the caller's stream.
/// </para>
///
/// <para>
/// <b>Shape</b>: RPC-mirror — <c>POST /api/mesh/&lt;verb&gt;</c> with JSON body, 1:1
/// with MCP tool names. Multipart for binary upload.
/// </para>
/// </summary>
public static class MeshApiEndpoints
{
    public const string RoutePrefix = "/api/mesh";

    /// <summary>
    /// Maps the <c>/api/mesh/*</c> endpoint group. Call after <c>UseAuthentication</c> /
    /// <c>UseAuthorization</c>, alongside <c>MapMeshMcp</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapMeshApi(this IEndpointRouteBuilder endpoints)
    {
        // Bearer-only — EVERY verb that mutates, compiles, executes or uploads. A session
        // cookie must never be able to drive one of these (see ReadPolicyName's remarks).
        var group = endpoints.MapGroup(RoutePrefix)
            .RequireAuthorization(Memex.Portal.Shared.Authentication.McpAuthenticationExtensions.PolicyName);

        // Cookie-OR-Bearer — the READ-ONLY subset the portal-next server renderer needs to
        // paint a page for an already-signed-in visitor. 🚨 Only pure reads belong here;
        // moving a verb into this group is a security decision, not a convenience.
        var reads = endpoints.MapGroup(RoutePrefix)
            .RequireAuthorization(Memex.Portal.Shared.Authentication.McpAuthenticationExtensions.ReadPolicyName);

        reads.MapPost("/get", (HttpContext http, IMessageHub rootHub, GetBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Get(body.Path)));

        // Who is calling — the caller's resolved mesh identity ({userId, name, email}), or a
        // null userId when the request resolved to no real user. Replaces "mint a token and
        // read the home partition out of its nodePath" as the SSR's way of learning whose
        // dashboard to render: a read, answered from the identity the request already carries.
        reads.MapPost("/whoami", HandleWhoAmI);

        group.MapPost("/search", (HttpContext http, IMessageHub rootHub, SearchBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Search(body.Query, body.BasePath)));

        // Plugin registry — memex as the distribution point. `/catalog` lists installable plugins
        // (partitions that ship NodeTypes); `/catalog/download` returns a plugin's definition
        // (Space + NodeTypes + Source/Test Code + docs) as {name, nodeCount, nodes:[…]} for a
        // consumer to import via `/update` — no GitHub creds on the consumer, the credential stays
        // here, encapsulated in the registry (npm/NuGet-style).
        group.MapPost("/catalog", (HttpContext http, IMessageHub rootHub, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Catalog()));

        group.MapPost("/catalog/download", (HttpContext http, IMessageHub rootHub, CatalogDownloadBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.CatalogDownload(body.Plugin)));

        group.MapPost("/create", (HttpContext http, IMessageHub rootHub, CreateBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Create(body.Node)));

        group.MapPost("/update", (HttpContext http, IMessageHub rootHub, UpdateBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Update(body.Nodes)));

        group.MapPost("/patch", (HttpContext http, IMessageHub rootHub, PatchBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Patch(body.Path, body.Fields)));

        group.MapPost("/delete", (HttpContext http, IMessageHub rootHub, DeleteBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Delete(body.Paths)));

        group.MapPost("/move", (HttpContext http, IMessageHub rootHub, MoveBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Move(body.SourcePath, body.TargetPath)));

        group.MapPost("/copy", (HttpContext http, IMessageHub rootHub, CopyBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Copy(body.SourcePath, body.TargetNamespace, body.Force)));

        group.MapPost("/recycle", (HttpContext http, IMessageHub rootHub, PathBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Recycle(body.Path)));

        group.MapPost("/compile", (HttpContext http, IMessageHub rootHub, PathBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Compile(body.Path)));

        group.MapPost("/diagnostics", (HttpContext http, IMessageHub rootHub, PathBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.GetDiagnostics(body.Path)));

        group.MapPost("/execute-script", (HttpContext http, IMessageHub rootHub, ExecuteScriptBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.ExecuteScript(body.Path, body.TimeoutSeconds ?? 120)));

        // First-full-frame render of a layout area — the SSR seeding verb (portal-next):
        // returns {areas, data} EXACTLY as the sync-stream wire delivers it. Read-only, and
        // on the cookie-or-Bearer policy: this is the verb an SSR page render is FOR.
        reads.MapPost("/render-area", (HttpContext http, IMessageHub rootHub, RenderAreaBody body, CancellationToken ct) =>
            HandleRenderArea(http, rootHub, body, ct));

        // Server-side Markdig render — the ONE markdown parser (the twin of the Blazor MarkdownView
        // pipeline). Returns {html, codeSubmissions}: the HTML keeps the executable-code-cell,
        // mermaid, @@-embed and __KERNEL_ADDRESS__ markers so the React client hydrates them into
        // live views instead of falling back to its limited local parser (which escapes raw-HTML
        // blocks — doc hero banners — into literal source text).
        group.MapPost("/render-markdown", (HttpContext http, IMessageHub rootHub, RenderMarkdownBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.RenderMarkdown(body.Markdown, body.NodePath)));

        // GUI-shell verbs (portal-next chrome) — the browser twins of the reads the Blazor shell
        // gets in-process: full-node query (search-bar suggestions, notification bell) and URL→
        // (node, area) navigation resolution.
        group.MapPost("/query-nodes", (HttpContext http, IMessageHub rootHub, QueryNodesBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.QueryNodes(body.Query, body.Limit ?? 50)));

        reads.MapPost("/resolve", (HttpContext http, IMessageHub rootHub, PathBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Resolve(body.Path)));

        // Content-collection directory listing — the read half of the React FileBrowser. Path is
        // "{node}/{collection}[/{dir}]"; download uses the existing /content|/api/content URLs, add uses
        // /upload. Returns { collection, path, editable, items:[…] } (or "Error: …").
        group.MapPost("/content/list", (HttpContext http, IMessageHub rootHub, PathBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.ContentList(body.Path)));

        // Mirror Push/Pull — these talk to the mesh hub directly (same as MCP plugin's PostMirror).
        group.MapPost("/mirror", HandleMirror);

        // Local helpers — same logic as the MCP plugin's NavigateTo / GetBaseUrl.
        group.MapPost("/navigate-to", HandleNavigateTo);
        group.MapPost("/base-url", HandleBaseUrl);

        // Binary upload — multipart so `curl -F file=@logo.png -F path=@Foo/content/logo.png` works.
        // DisableAntiforgery: bearer-auth form posts can't carry an antiforgery token; the request
        // is already authenticated by ApiTokenAuthenticationHandler, which is the protection here.
        group.MapPost("/upload", HandleUpload).DisableAntiforgery();

        return endpoints;
    }

    /// <summary>
    /// <c>POST /api/mesh/whoami</c> — the caller's resolved mesh identity, as
    /// <c>{userId, name, email}</c>. <c>userId</c> is the mesh User node's Id (the caller's
    /// home partition, e.g. <c>rbuergi</c>) and is <c>null</c> when the request resolved to
    /// no real user.
    ///
    /// <para>
    /// Reads the identity <c>UserContextMiddleware</c> already stamped on the portal hub's
    /// <see cref="AccessService"/> — the SAME source <c>ApiTokenController</c> uses, and for the
    /// same reason: it is the mesh User Id, never the email-shaped <c>preferred_username</c> an
    /// OIDC provider supplies. An email-shaped id is refused rather than echoed, because a caller
    /// that routed off it would target a <c>{email}</c> partition owning none of the user's data.
    /// </para>
    ///
    /// <para>
    /// This exists so a server-side renderer can learn whose home partition to paint WITHOUT
    /// minting an API token (issue #1477). It is deliberately not an MCP tool: MCP callers
    /// already know who they are — they presented the token.
    /// </para>
    /// </summary>
    private static IResult HandleWhoAmI(HttpContext http)
    {
        var caller = http.RequestServices.GetRequiredService<PortalApplication>()
            .Hub.ServiceProvider.GetRequiredService<AccessService>().Context;

        var userId = caller?.ObjectId;
        var resolved = !string.IsNullOrEmpty(userId)
                       && !userId.Contains('@')
                       && userId != WellKnownUsers.Anonymous;

        return Results.Json(resolved
            ? new WhoAmIResponse(userId, caller!.Name, caller.Email)
            : new WhoAmIResponse(null, null, null));
    }

    /// <summary>Response shape of <c>POST /api/mesh/whoami</c>.</summary>
    public record WhoAmIResponse(string? UserId, string? Name, string? Email);

    /// <summary>Default budget for <c>/render-area</c>; clamped so a caller can neither hang the
    /// request forever nor force a sub-second flake.</summary>
    private const int DefaultRenderAreaTimeoutSeconds = 30;

    /// <summary>
    /// <c>POST /api/mesh/render-area</c> — subscribes the node's layout area server-side (the
    /// same <c>GetRemoteStream</c> primitive the Blazor portal binds with), takes the first
    /// fully-materialised Full <c>{areas,data}</c> frame, disposes the subscription (on every
    /// path, including client abort — the observable chain tears down with the request's
    /// <see cref="CancellationToken"/>), and ships the frame verbatim: hub serializer options,
    /// <c>$type</c> discriminators, JSON-encoded instance keys — byte-identical to the gRPC
    /// wire's Full <c>DataChangedEvent</c>, so an SSR client seeds its area source without
    /// translation. A timeout returns a 504 JSON error instead of hanging; every other failure
    /// ships the <see cref="MeshOperations"/> <c>"Error: …"</c>/<c>"Not found: …"</c> sentinel
    /// exactly like the sibling verbs.
    /// </summary>
    private static Task<IResult> HandleRenderArea(
        HttpContext http, IMessageHub rootHub, RenderAreaBody body, CancellationToken ct)
    {
        var sessionHub = ResolveSession(http, rootHub);
        var ops = new MeshOperations(sessionHub);
        var timeout = Math.Clamp(body.TimeoutSeconds ?? DefaultRenderAreaTimeoutSeconds, 1, 120);
        return ops.RenderArea(body.Path, body.Area, body.Id, timeout)
            .Select(json => (IResult)Results.Content(json, "application/json"))
            .Catch((TimeoutException _) => Observable.Return((IResult)Results.Json(
                new
                {
                    error = $"Timed out after {timeout}s waiting for the first full frame of area " +
                            $"'{body.Area ?? "(default)"}' at '{body.Path}'.",
                },
                statusCode: StatusCodes.Status504GatewayTimeout)))
            .FirstAsync().ToTask(ct);
    }

    private static async Task<IResult> HandleMirror(
        HttpContext http, IMessageHub rootHub, MirrorRequest body, CancellationToken ct)
    {
        var sessionHub = ResolveSession(http, rootHub);
        var delivery = await sessionHub.Observe<MirrorResult>(body, o => o.WithTarget(new Address("mesh")))
            .Catch((Exception _) => Observable.Return((IMessageDelivery<MirrorResult>)null!))
            .FirstAsync().ToTask(ct);
        var result = delivery?.Message ?? new MirrorResult
        {
            Status = "Error",
            Direction = body.Direction,
            SourcePath = body.SourcePath,
            TargetPath = body.TargetPath ?? body.SourcePath,
            Error = "No response from mirror handler — is the mesh hub reachable and AddPersistence configured?",
        };
        return Results.Content(JsonSerializer.Serialize(result, sessionHub.JsonSerializerOptions), "application/json");
    }

    private static IResult HandleNavigateTo(HttpContext http, IOptions<McpConfiguration>? mcp, NavigateBody body)
    {
        var baseUrl = ResolveBaseUrl(http, mcp);
        var resolved = MeshOperations.ResolvePath(body.Path).TrimStart('/');
        return Results.Json(new { url = $"{baseUrl}/{resolved}" });
    }

    private static IResult HandleBaseUrl(HttpContext http, IOptions<McpConfiguration>? mcp) =>
        Results.Json(new { url = ResolveBaseUrl(http, mcp) });

    private static async Task<IResult> HandleUpload(HttpContext http, IMessageHub rootHub, CancellationToken ct)
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

        var sessionHub = ResolveSession(http, rootHub);
        var ops = new MeshOperations(sessionHub);
        var result = await ops.Upload(path, ms.ToArray()).FirstAsync().ToTask(ct);
        return Results.Content(result, "application/json");
    }

    /// <summary>
    /// Registers the bits the REST module needs that aren't already in DI from the
    /// MCP wiring: lift the multipart upload size cap (default 30 MB is too small
    /// for typical document uploads) and ensure <see cref="McpConfiguration"/> is
    /// bound (shared with MCP — same <c>Mcp__BaseUrl</c> env var).
    /// </summary>
    public static IServiceCollection AddMeshApi(this IServiceCollection services)
    {
        services.Configure<FormOptions>(o =>
        {
            // 200 MB — generous but bounded. Matches the working assumption that
            // document / image / spreadsheet uploads are the common case; binaries
            // larger than this should go through a different ingest path.
            o.MultipartBodyLengthLimit = 200L * 1024 * 1024;
            o.ValueLengthLimit = int.MaxValue;
            o.MultipartHeadersLengthLimit = int.MaxValue;
        });

        // McpConfiguration is already bound by AddMeshMcp(); BindConfiguration is
        // idempotent so a second call is harmless if the MCP wiring is absent.
        services.AddOptions<McpConfiguration>().BindConfiguration("Mcp");

        return services;
    }

    private static IMessageHub ResolveSession(HttpContext http, IMessageHub rootHub)
    {
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MeshApiEndpoints));
        return SessionHubResolver.ResolveSessionHub(rootHub, http, "api", logger);
    }

    private static async Task<IResult> RunString(
        HttpContext http,
        IMessageHub rootHub,
        CancellationToken ct,
        Func<MeshOperations, IObservable<string>> work)
    {
        var sessionHub = ResolveSession(http, rootHub);
        var ops = new MeshOperations(sessionHub);
        var result = await work(ops).FirstAsync().ToTask(ct);
        // MeshOperations returns either a JSON document or an "Error: …" sentinel string.
        // Both are safe to ship as application/json — the error string is just a JSON-quoted
        // value the client can branch on (mirrors the MCP-tool contract).
        return Results.Content(result, "application/json");
    }

    private static string ResolveBaseUrl(HttpContext http, IOptions<McpConfiguration>? mcp)
    {
        var configured = mcp?.Value.BaseUrl;
        if (!string.IsNullOrEmpty(configured))
            return configured.TrimEnd('/');
        var req = http.Request;
        return $"{req.Scheme}://{req.Host.Value}".TrimEnd('/');
    }

    // Request DTOs — the framework's System.Text.Json infrastructure binds JSON bodies
    // by property name (case-insensitive). All optional fields default to null / false.
    public record GetBody(string Path);
    public record SearchBody(string Query, string? BasePath);
    public record CatalogDownloadBody(string Plugin);
    public record QueryNodesBody(string Query, int? Limit = null);
    public record CreateBody(string Node);
    public record UpdateBody(string Nodes);
    public record PatchBody(string Path, string Fields);
    public record DeleteBody(string Paths);
    public record MoveBody(string SourcePath, string TargetPath);
    public record CopyBody(string SourcePath, string TargetNamespace, bool Force = false);
    public record PathBody(string Path);
    public record ExecuteScriptBody(string Path, int? TimeoutSeconds);
    public record RenderAreaBody(string Path, string? Area = null, string? Id = null, int? TimeoutSeconds = null);
    public record RenderMarkdownBody(string Markdown, string? NodePath = null);
    public record NavigateBody(string Path);
}
