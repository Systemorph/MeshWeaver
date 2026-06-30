using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Blazor.AI;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
/// <b>Auth</b>: gated by the existing <c>McpAuthenticationExtensions.PolicyName</c>
/// policy — same <c>Authorization: Bearer mw_…</c> token format as <c>/mcp</c>, validated
/// by <c>ApiTokenAuthenticationHandler</c>.
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
        var group = endpoints.MapGroup(RoutePrefix)
            .RequireAuthorization(Memex.Portal.Shared.Authentication.McpAuthenticationExtensions.PolicyName);

        group.MapPost("/get", (HttpContext http, IMessageHub rootHub, GetBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Get(body.Path)));

        group.MapPost("/search", (HttpContext http, IMessageHub rootHub, SearchBody body, CancellationToken ct) =>
            RunString(http, rootHub, ct, ops => ops.Search(body.Query, body.BasePath)));

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
    public record CreateBody(string Node);
    public record UpdateBody(string Nodes);
    public record PatchBody(string Path, string Fields);
    public record DeleteBody(string Paths);
    public record MoveBody(string SourcePath, string TargetPath);
    public record CopyBody(string SourcePath, string TargetNamespace, bool Force = false);
    public record PathBody(string Path);
    public record ExecuteScriptBody(string Path, int? TimeoutSeconds);
    public record NavigateBody(string Path);
}
