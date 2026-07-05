using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.InstanceSync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// OAuth 2.0 + PKCE "connect to a remote MeshWeaver instance" flow for instance sync. Lets a user
/// authorize this portal against ANOTHER MeshWeaver instance's own login — the remote exposes its
/// own OAuth server (see <c>OAuthConnectController</c>) — so the returned <c>mw_</c> token is stored
/// as the sync party's <see cref="InstanceSyncConfig.RemoteToken"/> instead of pasting a token by hand:
///
///   GET /connect/instance?spaceId=&amp;sourceId=&amp;returnPath=  — start (read RemoteUrl, PKCE cookie, redirect to the remote's /authorize)
///   GET /connect/instance/callback?code=&amp;state=             — exchange code at the remote /token, store RemoteToken, redirect back
///
/// Mirrors <see cref="GitHubConnectEndpoints"/>: a CSRF state cookie, a reactive body bridged to Task
/// once at the HTTP boundary, and error redirects that carry the real reason back to the Sync view.
/// The remote URL is read server-side from the party config — never trusted from a query parameter —
/// and the cookie carries the PKCE verifier + resolved token endpoint so the callback re-reads nothing.
/// </summary>
public static class InstanceConnectEndpoints
{
    /// <summary>The CSRF/PKCE state cookie name.</summary>
    public const string StateCookieName = "inst_connect_state";
    private const string CallbackPath = "/connect/instance/callback";

    /// <summary>Maps the two connect endpoints. Registered from <c>MemexConfiguration</c> next to <c>MapGitHubConnect</c>.</summary>
    public static IEndpointRouteBuilder MapInstanceConnect(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/connect/instance", (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? spaceId,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? sourceId,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? returnPath,
            InstanceSyncService sync,
            InstanceOAuthService oauth,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("InstanceConnect");
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Task.FromResult(Results.Challenge(new AuthenticationProperties
                { RedirectUri = http.Request.Path + http.Request.QueryString }));

            var rp = string.IsNullOrWhiteSpace(returnPath) ? "/" : returnPath!;
            if (string.IsNullOrWhiteSpace(spaceId) || string.IsNullOrWhiteSpace(sourceId))
                return Task.FromResult((IResult)Results.Redirect(SafeReturn(rp, "instance-error", "missing spaceId/sourceId")));

            var redirectUri = BuildRedirectUri(http);

            // Reactive: read the party's RemoteUrl (authoritative, via the node stream), discover the
            // remote's endpoints, set the PKCE/CSRF cookie, redirect to the remote's /authorize. Bridge
            // to Task ONCE at the HTTP boundary — the sanctioned edge pattern (see GitHub callback).
            return sync.ReadConfigAuthoritative(spaceId!, sourceId!)
                .Timeout(TimeSpan.FromSeconds(10))
                .SelectMany(cfg =>
                {
                    var remoteUrl = cfg?.RemoteUrl?.Trim();
                    if (string.IsNullOrWhiteSpace(remoteUrl))
                        return Observable.Return((IResult)Results.Redirect(
                            SafeReturn(rp, "instance-error", "set the remote instance URL first, then Connect")));

                    return oauth.Discover(remoteUrl!).Select(ep =>
                    {
                        var verifier = InstanceOAuthService.NewVerifier();
                        var challenge = InstanceOAuthService.Challenge(verifier);
                        var state = InstanceOAuthService.NewState();

                        // Cookie payload = the callback's full working set (each field base64url'd, so
                        // '|'/'.' in a returnPath can't corrupt the split): state, party, remote URL,
                        // resolved token endpoint, PKCE verifier, returnPath.
                        var payload = string.Join(".", new[]
                        {
                            state, spaceId!, sourceId!, remoteUrl!, ep.Token, verifier, rp
                        }.Select(Enc));

                        http.Response.Cookies.Append(StateCookieName, payload, new CookieOptions
                        {
                            HttpOnly = true,
                            Secure = true,
                            SameSite = SameSiteMode.Lax,
                            MaxAge = TimeSpan.FromMinutes(10),
                        });

                        return (IResult)Results.Redirect(
                            InstanceOAuthService.BuildAuthorizeUrl(ep.Authorize, redirectUri, challenge, state));
                    });
                })
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Instance connect start failed for {Space}/{Source}", spaceId, sourceId);
                    return Observable.Return((IResult)Results.Redirect(SafeReturn(rp, "instance-error", ex.Message)));
                })
                .FirstAsync()
                .ToTask(http.RequestAborted);
        }).RequireAuthorization();

        endpoints.MapGet(CallbackPath, (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? code,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? state,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? error,
            InstanceSyncService sync,
            InstanceOAuthService oauth,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("InstanceConnect");

            // Recover the working set (and CSRF state) from the cookie FIRST, so every failure below
            // redirects the user BACK to the Sync view WITH a visible reason — never a silent bounce.
            string cookieState = "", spaceId = "", sourceId = "", remoteUrl = "", tokenEndpoint = "",
                   verifier = "", returnPath = "/";
            if (http.Request.Cookies.TryGetValue(StateCookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
            {
                http.Response.Cookies.Delete(StateCookieName);
                try
                {
                    var p = cookie.Split('.');
                    cookieState = Dec(p[0]); spaceId = Dec(p[1]); sourceId = Dec(p[2]);
                    remoteUrl = Dec(p[3]); tokenEndpoint = Dec(p[4]); verifier = Dec(p[5]);
                    returnPath = p.Length > 6 ? Dec(p[6]) : "/";
                }
                catch { /* malformed cookie — the state check below fails it cleanly */ }
            }

            IResult Fail(string reason)
            {
                logger.LogWarning("Instance connect failed: {Reason} (user {User})", reason, http.User.Identity?.Name);
                return Results.Redirect(SafeReturn(returnPath, "instance-error", reason));
            }

            if (!string.IsNullOrEmpty(error))
                return Task.FromResult(Fail(error!));
            if (string.IsNullOrEmpty(cookieState))
                return Task.FromResult(Fail("missing or bad connect-state cookie (CSRF)"));
            if (!string.Equals(cookieState, state, StringComparison.Ordinal))
                return Task.FromResult(Fail("connect-state mismatch (CSRF)"));
            if (string.IsNullOrEmpty(code))
                return Task.FromResult(Fail("no authorization code returned by the remote instance"));

            var redirectUri = BuildRedirectUri(http);
            var configPath = InstanceSyncService.ConfigPath(spaceId, sourceId);

            // Reactive: exchange the code at the remote /token, then store the returned mw_ token as
            // the party's RemoteToken (the worker reads it as the Bearer token). Bridge to Task once.
            return oauth.ExchangeCode(tokenEndpoint, code!, redirectUri, verifier)
                .SelectMany(token => sync.UpdateConfig(configPath, c => c with
                {
                    RemoteUrl = remoteUrl,
                    RemoteToken = token,
                    Active = true,
                }))
                .Select(_ =>
                {
                    logger.LogInformation("Stored remote-instance token for {ConfigPath}", configPath);
                    return (IResult)Results.Redirect(SafeReturn(returnPath, "instance-ok", null));
                })
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex, "Instance connect callback failed for {ConfigPath}", configPath);
                    return Observable.Return((IResult)Results.Redirect(SafeReturn(returnPath, "instance-error", ex.Message)));
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

    private static string BuildRedirectUri(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}{CallbackPath}";

    private static string Enc(string s) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(s ?? ""));
    private static string Dec(string s) => Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(s));
}
