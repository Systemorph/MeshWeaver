using System;
using System.Net;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.AspNetCore.Portal; // PortalApplication
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Memex.Portal.Shared.Authentication;

namespace Memex.Portal.Shared.Social;

/// <summary>
/// The browser half of the instance-owned GitHub App's manifest flow
/// (<c>Doc/Architecture/InstanceRepositoryAccess</c>):
///
///   GET /admin/github-app/connect?organization={org}&amp;returnPath={path}
///       — a global administrator starts the flow: <see cref="InstanceGitHubApp.Start"/> records the
///         pending state, the state goes into a cookie, and the answer is a one-button form that
///         POSTs the manifest to GitHub (the manifest flow is a form POST by GitHub's design).
///   GET /admin/github-app/callback?code=…&amp;state=…
///       — GitHub's redirect back: <see cref="InstanceGitHubApp.Complete"/> checks cookie, state,
///         expiry and admin, exchanges the one-time code, and stores the App's credentials as
///         Instance Secrets. The admin lands back on the page they started from.
///
/// <para>Every decision lives in <see cref="InstanceGitHubApp"/>; this file only carries the
/// browser's cookie and the redirects. Nothing here ever logs or echoes a credential.</para>
/// </summary>
public static class GitHubAppManifestEndpoints
{
    /// <summary>Maps the two endpoints. Called from <see cref="GitHubConnectEndpoints.MapGitHubConnect"/>.</summary>
    public static IEndpointRouteBuilder MapGitHubAppManifest(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(InstanceGitHubApp.StartPath, async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? organization,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? returnPath,
            ILoggerFactory loggers) =>
        {
            if (!http.User.Identity?.IsAuthenticated ?? true)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.Path + http.Request.QueryString });
            var logger = loggers.CreateLogger("GitHubAppManifest");
            var back = ReturnUrlPolicy.Sanitize(returnPath);
            var hub = Hub(http);
            var locale = Locale(hub);
            return await InstanceGitHubApp.Start(hub, BaseUrl(http), organization)
                .Select(flow =>
                {
                    http.Response.Cookies.Append(InstanceGitHubApp.StateCookieName,
                        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{flow.State}|{back}")),
                        new CookieOptions
                        {
                            HttpOnly = true,
                            Secure = true,
                            SameSite = SameSiteMode.Lax,
                            Path = InstanceGitHubApp.CallbackPath,
                            MaxAge = InstanceGitHubApp.StateLifetime,
                        });
                    return Results.Content(FormPage(flow, locale), "text/html; charset=utf-8");
                })
                .Catch((Exception ex) =>
                {
                    logger.LogWarning("GitHub App manifest flow refused at start: {Reason}", ex.Message);
                    return Observable.Return(Results.Redirect(Back(back, "error", GitSyncRefusalException.Localize(ex, locale))));
                })
                .FirstAsync()
                .ObserveCompletion(
                    ex => logger.LogWarning(ex, "GitHub App manifest start faulted after its answer was sent"),
                    http.RequestAborted);
        }).RequireAuthorization();

        endpoints.MapGet(InstanceGitHubApp.CallbackPath, async (
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? code,
            [Microsoft.AspNetCore.Mvc.FromQuery] string? state,
            GitHubAppManifestClient client,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("GitHubAppManifest");
            string? cookieState = null;
            var back = "/";
            if (http.Request.Cookies.TryGetValue(InstanceGitHubApp.StateCookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
            {
                var malformed = false;
                try
                {
                    var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cookie)).Split('|', 2);
                    cookieState = parts[0];
                    back = ReturnUrlPolicy.Sanitize(parts.Length > 1 ? parts[1] : "/");
                }
                catch (FormatException)
                {
                    // A malformed cookie is no state at all: Complete refuses it by name below.
                    malformed = true;
                }
                // Only THIS callback's cookie is cleared: the cookie belongs to the newest flow this
                // browser started, and a stale or forged callback for another flow must not take it away.
                if (malformed || string.Equals(cookieState, state, StringComparison.Ordinal))
                    http.Response.Cookies.Delete(InstanceGitHubApp.StateCookieName,
                        new CookieOptions { Path = InstanceGitHubApp.CallbackPath, Secure = true, HttpOnly = true });
            }
            var hub = Hub(http);
            var locale = Locale(hub);
            return await InstanceGitHubApp.Complete(hub, code, state, cookieState, client.Convert)
                .Select(app => Results.Redirect(Back(back, "connected", null)))
                .Catch((Exception ex) =>
                {
                    // The refusal's English text names no credential (GitSyncRefusal templates carry
                    // names and statuses only), so it is safe to log and to show.
                    logger.LogWarning("GitHub App manifest flow refused at callback: {Reason}", ex.Message);
                    return Observable.Return(Results.Redirect(Back(back, "error", GitSyncRefusalException.Localize(ex, locale))));
                })
                .FirstAsync()
                .ObserveCompletion(
                    ex => logger.LogWarning(ex, "GitHub App manifest callback faulted after its answer was sent"),
                    http.RequestAborted);
        }).RequireAuthorization();

        return endpoints;
    }

    private static IMessageHub Hub(HttpContext http) =>
        http.RequestServices.GetRequiredService<PortalApplication>().Hub;

    private static string? Locale(IMessageHub hub) =>
        hub.ServiceProvider.GetService<AccessService>()?.Context?.Locale;

    /// <summary>The instance's origin as the browser reached it — the host the state cookie is set
    /// on, so GitHub's redirect returns to the same host and the cookie travels with it.</summary>
    private static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    private static string Back(string returnPath, string status, string? reason)
    {
        var separator = returnPath.Contains('?') ? "&" : "?";
        var url = $"{returnPath}{separator}githubApp={status}";
        return reason is { Length: > 0 } ? url + "&reason=" + Uri.EscapeDataString(reason) : url;
    }

    /// <summary>
    /// The one-button page that POSTs the manifest to GitHub. GitHub's manifest flow is a form POST
    /// from the browser by design, so this is an HTTP interstitial rather than a mesh view; it runs
    /// no script (the portal's CSP allows <c>form-action https:</c>), and every value is HTML-encoded.
    /// </summary>
    private static string FormPage(InstanceGitHubAppFlow flow, string? locale)
    {
        string T(string key) => WebUtility.HtmlEncode(MeshWeaver.Messaging.LocalizationCatalog.Get(key, locale));
        return $"""
            <!doctype html>
            <html lang="{MeshWeaver.Messaging.Locales.Resolve(locale)}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{T("instanceApp.page.title")}</title></head>
            <body>
            <h1>{T("instanceApp.page.title")}</h1>
            <p>{T("instanceApp.page.explain")}</p>
            <form method="post" action="{WebUtility.HtmlEncode(flow.PostUrl)}">
            <input type="hidden" name="manifest" value="{WebUtility.HtmlEncode(flow.Manifest)}">
            <button type="submit">{T("instanceApp.page.continue")}</button>
            </form>
            </body></html>
            """;
    }
}
