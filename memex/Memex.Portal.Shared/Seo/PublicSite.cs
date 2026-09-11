using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Seo;

/// <summary>
/// 🚨 THE ONE PLACE THAT KNOWS WHICH HOST IS THE PUBLIC WEB SITE.
///
/// <para>A portal can serve two hosts from one process: a PUBLIC host (<c>www.meshweaver.cloud</c>)
/// that is the canonical address of everything a signed-out visitor may read — the landing, the
/// documentation, the store, the courses — and an APP host (<c>memex.meshweaver.cloud</c>) that
/// carries sign-in, the APIs, MCP, gRPC and the plugin registry. Search engines key ranking to the
/// host, so public content must have exactly ONE address: the canonical link, the sitemap, the
/// Open Graph URL and robots.txt all read the public host from here, and the app host answers a
/// signed-out request for a public page with a permanent redirect to it
/// (<see cref="UsePublicHostRedirect"/>).</para>
///
/// <para>Nothing here is on when <c>Portal:PublicHost</c> is unset: a single-host deployment
/// keeps behaving exactly as before, with the request's own host as the canonical one. Full
/// design: <c>Doc/Architecture/PublicWebPresence</c>.</para>
/// </summary>
public static class PublicSite
{
    /// <summary>The public host name, e.g. <c>www.meshweaver.cloud</c>. Unset ⇒ single-host.</summary>
    public const string PublicHostKey = "Portal:PublicHost";

    /// <summary>
    /// The node a signed-out visitor sees at <c>/</c> — the landing page, e.g. <c>MeshWeaver</c>.
    /// Unset ⇒ the root is not a node page (the portal's own welcome route answers it).
    /// </summary>
    public const string LandingPathKey = "Portal:LandingPath";

    /// <summary>The configured public host, trimmed, or null when the deployment has one host.</summary>
    public static string? PublicHost(IConfiguration configuration)
    {
        var value = configuration[PublicHostKey];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
    }

    /// <summary>The configured landing node path, trimmed of slashes, or null.</summary>
    public static string? LandingPath(IConfiguration configuration)
    {
        var value = configuration[LandingPathKey];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('/');
    }

    /// <summary>
    /// The base URL every crawler-facing absolute link is built on: <c>https://{PublicHost}</c>
    /// when a public host is configured, else the request's own scheme and host. The public host
    /// is always <c>https</c> — it is a name on the internet, not a listener.
    /// </summary>
    public static string CanonicalBaseUrl(IConfiguration configuration, HttpRequest request)
        => PublicHost(configuration) is { } host
            ? $"https://{host}"
            : $"{request.Scheme}://{request.Host}";

    /// <summary>
    /// True when this request arrived on a host that is NOT the public one while a public host is
    /// configured — i.e. on the app host. Such a response must never be indexed: it is the same
    /// page under a second address.
    /// </summary>
    public static bool IsAppOnlyHost(IConfiguration configuration, HttpRequest request)
        => PublicHost(configuration) is { } host
           && !string.Equals(request.Host.Value, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The node path a request path stands for: the landing node for <c>/</c> when one is
    /// configured, otherwise the path itself with its slashes trimmed. Empty for <c>/</c> with no
    /// landing configured.
    /// </summary>
    public static string NodePathFor(IConfiguration configuration, string? requestPath)
    {
        var trimmed = (requestPath ?? "").Trim('/');
        return trimmed.Length == 0 ? LandingPath(configuration) ?? "" : trimmed;
    }

    /// <summary>
    /// 🚨 THE APP HOST DOES NOT SERVE PUBLIC PAGES TO STRANGERS — it sends them to the public host.
    ///
    /// <para>A signed-out <c>GET</c> for HTML on the app host, for a path that resolves to a page
    /// the <see cref="MeshWeaver.Mesh.Security.AnonymousGate"/> admits, answers <c>301</c> to the
    /// same path on the public host. Everything else is untouched: a signed-in user (their session
    /// cookie lives on the app host), a private page (which goes on to the sign-in redirect as
    /// before), an API or asset request, and every request when no public host is configured. So
    /// a link shared from inside the app keeps working for its author and lands a stranger on the
    /// address search engines know.</para>
    ///
    /// <para><paramref name="isPublicPage"/> decides publicness, reactively; the default asks the
    /// mesh through <see cref="SeoResolver.Resolve"/> — the same gate the head is built from — and
    /// a test hands in its own decision so the routing rule is pinned without a mesh. The ONE
    /// <c>Task</c> bridge is here, at the ASP.NET middleware boundary, through
    /// <see cref="ReactiveCompletion.ObserveCompletion{T}(IObservable{T}, Action{Exception}, System.Threading.CancellationToken)"/>
    /// (never <c>.ToTask()</c>). Register AFTER authentication (it reads <c>User.Identity</c>) and
    /// before the page endpoints.</para>
    ///
    /// <para>The decision is the boolean projection of the gate on purpose: "not public" and "the
    /// gate could not decide" both mean NO redirect here, and no redirect is the fail-closed
    /// action — the request continues into the app's own handling, which evaluates the tri-state
    /// gate itself and answers the visitor (sign-in, or unavailable). A 301 that named a page on
    /// the public host on an undetermined verdict would be the assertion this avoids.</para>
    /// </summary>
    public static IApplicationBuilder UsePublicHostRedirect(
        this IApplicationBuilder app, Func<HttpContext, string, IObservable<bool>>? isPublicPage = null)
    {
        var decide = isPublicPage ?? DefaultIsPublicPage;
        return app.Use(async (http, next) =>
        {
            var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
            var publicHost = PublicHost(configuration);
            if (publicHost is null
                || !IsAppOnlyHost(configuration, http.Request)
                || !HttpMethods.IsGet(http.Request.Method)
                || http.User.Identity?.IsAuthenticated == true
                || !AcceptsHtml(http.Request))
            {
                await next(http);
                return;
            }

            var nodePath = NodePathFor(configuration, http.Request.Path.Value);
            if (nodePath.Length == 0 || !SeoResolver.IsCandidatePath(nodePath))
            {
                await next(http);
                return;
            }

            var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(PublicSite));
            var isPublic = await decide(http, nodePath)
                .FirstAsync()
                .Catch<bool, Exception>(_ => Observable.Return(false))
                .ObserveCompletion(
                    ex => logger?.LogWarning(ex, "Public-host decision for '{Path}' faulted after the response had settled", nodePath),
                    http.RequestAborted);
            if (!isPublic)
            {
                await next(http);
                return;
            }

            var target = $"https://{publicHost}{http.Request.Path}{http.Request.QueryString}";
            http.Response.Redirect(target, permanent: true);
        });
    }

    /// <summary>
    /// 🚨 <c>HEAD</c> IS ANSWERED, NOT REFUSED. Razor component endpoints accept only <c>GET</c>
    /// and <c>POST</c>, so a link checker, an uptime probe or a crawler asking <c>HEAD /Doc</c> got
    /// <c>405</c> from a page that serves fine — measured 2026-09-11. This runs the request as a
    /// <c>GET</c> and discards the body, which is what <c>HEAD</c> means.
    ///
    /// <para>🚨 Register BEFORE <c>UseRouting</c>: an endpoint is matched by method, so once routing
    /// has run the GET-only page route has already failed to match and the rewrite changes
    /// nothing (the 405 is routing's own answer). A host that never calls <c>UseRouting</c>
    /// explicitly gets one inserted ahead of its first middleware, which puts this after it.</para>
    /// </summary>
    public static IApplicationBuilder UseHeadAsGet(this IApplicationBuilder app)
        => app.Use(async (http, next) =>
        {
            if (!HttpMethods.IsHead(http.Request.Method))
            {
                await next(http);
                return;
            }
            var method = http.Request.Method;
            http.Request.Method = HttpMethods.Get;
            var body = http.Response.Body;
            http.Response.Body = Stream.Null;
            try
            {
                await next(http);
            }
            finally
            {
                // Restore both: middleware that resumes after `next` (logging, tracing) must see
                // the request as the HEAD it was.
                http.Request.Method = method;
                http.Response.Body = body;
            }
        });

    private static bool AcceptsHtml(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        return accept.Length == 0
               || accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
               || accept.Contains("*/*", StringComparison.Ordinal);
    }

    // The production decision: the same anonymous-gated resolution the crawler head is built
    // from. A page the gate refuses — or a mesh that does not answer — is "not public", and the
    // request continues to the app's own handling (sign-in redirect), never to a redirect that
    // would name a page on the public host. Cold, reactive; bridged once by the caller.
    private static IObservable<bool> DefaultIsPublicPage(HttpContext http, string nodePath)
    {
        var hub = http.RequestServices.GetService<IMessageHub>();
        return hub is null
            ? Observable.Return(false)
            : SeoResolver.Resolve(hub, nodePath).Select(data => data is not null && data.Remainder is null);
    }
}
