using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Infrastructure; // PortalApplication
using MeshWeaver.GitSync;
using MeshWeaver.Messaging;             // AccessService
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// GitHub OAuth2 authorization-code flow for connecting a user's GitHub identity for
/// <see cref="MeshWeaver.GitSync">GitHub Sync</see>. Mirrors
/// <see cref="LinkedInConnectEndpoints"/> — the deployed surface is just the parts that
/// need browser cookies + a whitelisted callback URL:
///
///   GET /connect/github/me                  — redirect into the flow for the signed-in user
///   GET /connect/github?returnPath={path}   — start (CSRF cookie, redirect to GitHub authorize)
///   GET /connect/github/callback?code=…      — exchange code → token, store credential, redirect back
///
/// The token is stored encrypted at <c>{userId}/_Provider/GitHub</c> via
/// <see cref="GitHubCredentialService"/>, keyed by the authenticated user's name
/// (which is the email = <c>AccessContext.ObjectId</c> in this portal).
/// </summary>
public static class GitHubConnectEndpoints
{
    public const string StateCookieName = "gh_connect_state";
    private const string CallbackPath = "/connect/github/callback";

    public static IEndpointRouteBuilder MapGitHubConnect(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/connect/github/me", (HttpContext http) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = "/connect/github/me" });
            return Results.Redirect("/connect/github?returnPath=/");
        }).RequireAuthorization();

        endpoints.MapGet("/connect/github", (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? returnPath,
            GitHubOAuthService oauth) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });
            if (!oauth.IsConfigured)
                return Results.Problem("GitHub OAuth is not configured (GitHub:OAuth:ClientId + ClientSecret).", statusCode: 500);

            var state = GenerateState();
            var rp = string.IsNullOrWhiteSpace(returnPath) ? "/" : returnPath!;
            http.Response.Cookies.Append(StateCookieName,
                WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{state}|{rp}")),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.FromMinutes(10),
                });

            return Results.Redirect(oauth.BuildAuthorizeUrl(BuildRedirectUri(http), state));
        }).RequireAuthorization();

        endpoints.MapGet(CallbackPath, async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? code,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? state,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? error,
            GitHubOAuthService oauth,
            GitHubCredentialService creds,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("GitHubConnect");

            // Recover the originating page (and CSRF state) from the cookie FIRST, so every failure
            // below redirects the user BACK to the GitHub Sync tab WITH a visible reason — never a
            // silent bounce to the home page. (Errors are also logged at Warning so they surface in
            // Loki / App Insights, not just the GUI.)
            string cookieState = "", returnPath = "/";
            if (http.Request.Cookies.TryGetValue(StateCookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
            {
                http.Response.Cookies.Delete(StateCookieName);
                try
                {
                    var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cookie)).Split('|', 2);
                    cookieState = parts[0];
                    returnPath = parts.Length > 1 ? parts[1] : "/";
                }
                catch { /* malformed cookie — fall through to the state check below */ }
            }

            IResult Fail(string reason)
            {
                logger.LogWarning("GitHub connect failed: {Reason} (user {User})", reason, http.User.Identity?.Name);
                return Results.Redirect(SafeReturn(returnPath, "github-error", reason));
            }

            if (!string.IsNullOrEmpty(error))
                return Fail(error!);
            if (string.IsNullOrEmpty(cookieState))
                return Fail("missing or bad connect-state cookie (CSRF)");
            if (!string.Equals(cookieState, state, StringComparison.Ordinal))
                return Fail("connect-state mismatch (CSRF)");
            if (string.IsNullOrEmpty(code))
                return Fail("no authorization code returned by GitHub");

            // The credential MUST be keyed by the mesh User.Id (e.g. "rbuergi") — the SAME identifier
            // the GitHub Sync tab and Sync read it under (AccessContext.ObjectId). Using
            // http.User.Identity.Name (the display name "Roland Buergi") saved it under the wrong key,
            // so the tab never found it ("nothing happens" after ?connect=github-ok). Mirror
            // OAuthConnectController.ResolveMeshUserId: prefer the resolved AccessContext.ObjectId,
            // fall back to the preferred_username/email local part.
            var userId = ResolveMeshUserId(http);
            if (string.IsNullOrEmpty(userId))
                return Fail("could not resolve your mesh user id (retry after a normal browser login)");

            var redirectUri = BuildRedirectUri(http);
            // Reactive end-to-end; bridge to Task ONCE at the HTTP boundary via FirstAsync().ToTask()
            // — the sanctioned edge pattern (see OAuthConnectController.ExchangeToken). NO hand-woven
            // TaskCompletionSource/Subscribe. The credential write's AccessContext is carried through
            // the framework's .Subscribe / IoPool boundary from the request context the middleware set.
            return await oauth.ExchangeCode(code!, redirectUri)
                .SelectMany(token => oauth.GetLogin(token.AccessToken)
                    .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
                    .SelectMany(login => creds.Save(userId!, token, login)))
                .Select(_ =>
                {
                    logger.LogInformation("Stored GitHub credential for {User}", userId);
                    return (IResult)Results.Redirect(SafeReturn(returnPath, "github-ok", null));
                })
                .Catch((Exception ex) =>
                {
                    // Surface the REAL reason (token exchange / GetLogin / credential write) — never swallow.
                    logger.LogWarning(ex, "GitHub connect failed for {User}", userId);
                    return Observable.Return((IResult)Results.Redirect(SafeReturn(returnPath, "github-error", ex.Message)));
                })
                .FirstAsync()
                .ToTask(http.RequestAborted);
        }).RequireAuthorization();

        return endpoints;
    }

    private static string SafeReturn(string returnPath, string status, string? reason)
    {
        var rp = string.IsNullOrWhiteSpace(returnPath) || !returnPath.StartsWith("/", StringComparison.Ordinal) ? "/" : returnPath;
        var sep = rp.Contains('?') ? "&" : "?";
        var url = $"{rp}{sep}connect={status}";
        if (!string.IsNullOrEmpty(reason))
            url += $"&reason={Uri.EscapeDataString(reason!)}";
        return url;
    }

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return WebEncoders.Base64UrlEncode(buf);
    }

    private static string BuildRedirectUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{CallbackPath}";

    /// <summary>
    /// Resolves the mesh <c>User.Id</c> (e.g. <c>rbuergi</c>) to key the credential under — the SAME
    /// identifier the GitHub Sync tab + Sync read it under (<c>AccessContext.ObjectId</c>), NEVER the
    /// display <c>Name</c> claim. Mirrors <c>OAuthConnectController.ResolveMeshUserId</c>: prefer the
    /// resolved <c>AccessContext.ObjectId</c> (email→User.Id, stamped by UserContextMiddleware), fall
    /// back to the <c>preferred_username</c>/email local part when no context is present.
    /// </summary>
    private static string? ResolveMeshUserId(HttpContext http)
    {
        var ctx = http.RequestServices.GetService<PortalApplication>()?
            .Hub.ServiceProvider.GetService<AccessService>()?.Context;
        var resolved = ctx?.ObjectId;
        if (!string.IsNullOrEmpty(resolved) && !resolved.Contains('@'))
            return resolved;
        var claim = http.User.FindFirstValue("preferred_username") ?? http.User.FindFirstValue(ClaimTypes.Email);
        return UsernameFromEmail(claim);
    }

    /// <summary>Email-shaped identifier → its local part (the username / mesh partition key,
    /// e.g. <c>rbuergi@systemorph.com → rbuergi</c>); unchanged when there's no <c>@</c>.</summary>
    private static string? UsernameFromEmail(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var at = value.IndexOf('@');
        return at > 0 ? value[..at] : value;
    }
}
