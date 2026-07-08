using System;
using System.Net.Http;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Social;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// Member <b>publishing + engagement</b> endpoints for the LinkedIn integration — the
/// <c>w_member_social</c> side of the connect flow (<see cref="LinkedInConnectEndpoints"/> grants
/// the scope; these endpoints use it):
///
/// <code>
///   POST /linkedin/publish              JSON { postPath?, profilePath?, text?, visibility? } → publish a member post
///   GET  /linkedin/publish?postPath=…   UI trigger — publish a Post node, redirect back with ?publish=ok|error
///   GET  /linkedin/engagement?postPath=… UI trigger — refresh like/comment counts for a published Post node
/// </code>
///
/// These are thin ASP.NET minimal-API adapters over <see cref="LinkedInPublishService"/> (in
/// <c>MeshWeaver.Social</c>), which owns the credential-read → publish → node-write-back chain and its
/// access gates. <c>async</c>/<c>HttpClient</c> is fine here (endpoint handlers are NOT hub code); the
/// service carries the request's AccessContext through the mesh reads/writes — it NEVER runs as system,
/// so a caller who lacks access to the post (or to the credential) is denied by the normal mesh
/// permission checks before any LinkedIn call is made. The versioned <c>/rest/*</c> calls require the
/// app to be granted <c>w_member_social</c> and the member to have re-consented after that scope was
/// added; a missing scope surfaces as <c>reason=missing-w_member_social-reconnect</c>.
/// </summary>
public static class LinkedInPublishEndpoints
{
    private const string ApiVersion = LinkedInPostsApi.DefaultApiVersion;

    /// <summary>Registers the publish + engagement endpoints. Call alongside <c>MapLinkedInConnect()</c>.</summary>
    public static IEndpointRouteBuilder MapLinkedInPublish(this IEndpointRouteBuilder endpoints)
    {
        // 1) JSON publish API — body { postPath?, profilePath?, text?, visibility? }.
        endpoints.MapPost("/linkedin/publish", async (
            HttpContext http,
            PublishRequest body,
            IMessageHub hub,
            IMeshService mesh,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggers) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Unauthorized();

            var svc = new LinkedInPublishService(hub, mesh, loggers.CreateLogger<LinkedInPublishService>());
            var client = httpFactory.CreateClient();
            var ct = http.RequestAborted;

            if (!string.IsNullOrWhiteSpace(body.PostPath))
            {
                if (!IsSafePath(body.PostPath!))
                    return Results.BadRequest(new { ok = false, reason = "bad-post-path" });
                var outcome = await svc.PublishPostAsync(client, body.PostPath!, body.Text, body.Visibility, ApiVersion, ct);
                return outcome.Success
                    ? Results.Json(new { ok = true, urn = outcome.Urn, url = outcome.PostUrl, status = "Published" })
                    : Results.Json(new { ok = false, reason = outcome.Reason, status = outcome.StatusCode }, statusCode: HttpStatusFor(outcome.Reason, outcome.StatusCode));
            }

            if (!string.IsNullOrWhiteSpace(body.ProfilePath) && !string.IsNullOrWhiteSpace(body.Text))
            {
                var outcome = await svc.PublishTextAsync(client, body.ProfilePath!, body.Text!, body.Visibility, ApiVersion, ct);
                return outcome.Success
                    ? Results.Json(new { ok = true, urn = outcome.Urn, url = outcome.PostUrl, status = "Published" })
                    : Results.Json(new { ok = false, reason = outcome.Error, status = outcome.StatusCode }, statusCode: HttpStatusFor(outcome.Error, outcome.StatusCode));
            }

            return Results.BadRequest(new { ok = false, reason = "postPath-or-profilePath+text-required" });
        }).RequireAuthorization();

        // 2) UI trigger — publish a specific Post node, redirect back to it. Surfaced as a node-menu
        //    item (SocialPostMenuProvider) whose Href is a plain GET navigation.
        endpoints.MapGet("/linkedin/publish", async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string postPath,
            IMessageHub hub,
            IMeshService mesh,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggers) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });
            if (!IsSafePath(postPath))
                return Redirect(postPath, "publish", ok: false, "bad-post-path");

            var svc = new LinkedInPublishService(hub, mesh, loggers.CreateLogger<LinkedInPublishService>());
            var outcome = await svc.PublishPostAsync(httpFactory.CreateClient(), postPath, null, null, ApiVersion, http.RequestAborted);
            return Redirect(postPath, "publish", outcome.Success, outcome.Reason);
        }).RequireAuthorization();

        // 3) UI trigger — refresh engagement for a published Post node, redirect back.
        endpoints.MapGet("/linkedin/engagement", async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string postPath,
            IMessageHub hub,
            IMeshService mesh,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggers) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });
            if (!IsSafePath(postPath))
                return Redirect(postPath, "engagement", ok: false, "bad-post-path");

            var svc = new LinkedInPublishService(hub, mesh, loggers.CreateLogger<LinkedInPublishService>());
            var outcome = await svc.RefreshEngagementAsync(httpFactory.CreateClient(), postPath, ApiVersion, http.RequestAborted);
            return Redirect(postPath, "engagement", outcome.Success, outcome.Reason);
        }).RequireAuthorization();

        return endpoints;
    }

    private static int HttpStatusFor(string? reason, int upstreamStatus) => reason switch
    {
        "access-denied" => StatusCodes.Status403Forbidden,
        "post-not-found" => StatusCodes.Status404NotFound,
        "not-connected" or "missing-w_member_social-reconnect" => StatusCodes.Status409Conflict,
        "profile-path-missing" or "empty-text" or "not-published" => StatusCodes.Status400BadRequest,
        _ => upstreamStatus is >= 400 and < 600 ? upstreamStatus : StatusCodes.Status502BadGateway,
    };

    private static IResult Redirect(string postPath, string action, bool ok, string? reason)
    {
        var target = IsSafePath(postPath) ? "/" + postPath : "/";
        var sep = target.Contains('?') ? "&" : "?";
        var url = $"{target}{sep}{action}={(ok ? "ok" : "error")}";
        if (!ok && !string.IsNullOrEmpty(reason))
            url += $"&reason={Uri.EscapeDataString(reason!)}";
        return Results.Redirect(url);
    }

    /// <summary>
    /// A <c>postPath</c> is a mesh node path we interpolate into a Location header. Reject anything
    /// that could turn the redirect into an open redirect (leading slash, protocol-relative, scheme,
    /// traversal) — mirrors <see cref="LinkedInPageSyncEndpoints"/>'s guard.
    /// </summary>
    private static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Contains("//", StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.Contains(':');

    /// <summary>JSON body for <c>POST /linkedin/publish</c>.</summary>
    public sealed record PublishRequest(string? PostPath, string? ProfilePath, string? Text, string? Visibility);
}
