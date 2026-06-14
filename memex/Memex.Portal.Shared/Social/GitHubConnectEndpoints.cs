using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
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

            if (!string.IsNullOrEmpty(error))
                return Results.Redirect($"/?connect=github-error&reason={Uri.EscapeDataString(error)}");
            if (!http.Request.Cookies.TryGetValue(StateCookieName, out var cookie) || string.IsNullOrEmpty(cookie))
                return Results.BadRequest("Missing connect state cookie (CSRF).");
            http.Response.Cookies.Delete(StateCookieName);

            string cookieState, returnPath;
            try
            {
                var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cookie)).Split('|', 2);
                cookieState = parts[0];
                returnPath = parts.Length > 1 ? parts[1] : "/";
            }
            catch { return Results.BadRequest("Bad state cookie."); }

            if (!string.Equals(cookieState, state, StringComparison.Ordinal))
                return Results.BadRequest("State mismatch (CSRF).");
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest("No authorization code.");

            var userId = http.User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Results.BadRequest("Not authenticated.");

            var redirectUri = BuildRedirectUri(http);
            var tcs = new TaskCompletionSource<IResult>();
            oauth.ExchangeCode(code!, redirectUri)
                .SelectMany(token => oauth.GetLogin(token.AccessToken)
                    .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
                    .SelectMany(login => creds.Save(userId!, token, login)))
                .Subscribe(
                    _ =>
                    {
                        logger.LogInformation("Stored GitHub credential for {User}", userId);
                        tcs.TrySetResult(Results.Redirect(SafeReturn(returnPath, "github-ok")));
                    },
                    ex =>
                    {
                        logger.LogWarning(ex, "GitHub connect failed for {User}", userId);
                        tcs.TrySetResult(Results.Redirect(SafeReturn(returnPath, "github-error")));
                    });
            return await tcs.Task;
        }).RequireAuthorization();

        return endpoints;
    }

    private static string SafeReturn(string returnPath, string status)
    {
        var rp = string.IsNullOrWhiteSpace(returnPath) || !returnPath.StartsWith("/", StringComparison.Ordinal) ? "/" : returnPath;
        var sep = rp.Contains('?') ? "&" : "?";
        return $"{rp}{sep}connect={status}";
    }

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return WebEncoders.Base64UrlEncode(buf);
    }

    private static string BuildRedirectUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{CallbackPath}";
}
