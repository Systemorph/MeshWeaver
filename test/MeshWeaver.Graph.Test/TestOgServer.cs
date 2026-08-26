using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A tiny loopback HTTP server for the Open Graph tests: serves a configurable HTML head and
/// counts requests, so the promise-cache ("fetch once, replay to all") and the failure-eviction
/// retry can be asserted against REAL HTTP — no mocks (WritingTests.md).
///
/// <para>🚨 <b>Kestrel, bound to port 0 — deliberately NOT <c>HttpListener</c></b> (#2436). The
/// previous version reserved a port with a <c>TcpListener</c>, stopped it to learn the number, and
/// then asked <c>HttpListener</c> to bind it — a check-then-use window in which anything on the
/// box could take the port, after which <c>Start()</c> throws and the whole class fails for a
/// reason that has nothing to do with Open Graph. It is the same defect #2379/#2427 fixed in
/// <c>MeshGrpcTrustedKestrelTest</c>, but <c>HttpListener</c> cannot bind <c>:0</c> and cannot be
/// handed an already-bound socket, so that PR's cure does not transfer.</para>
///
/// <para>Kestrel binds <c>:0</c> itself and reports back what it got, so there is no window at all
/// — the port is never unowned between discovery and use. A retry loop around the old shape would
/// have made the flake rarer instead of impossible, which is not a fix.</para>
/// </summary>
internal sealed class TestOgServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private int requestCount;

    /// <summary>The served page's base URL, e.g. <c>http://127.0.0.1:{port}/</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>Requests served so far.</summary>
    public int RequestCount => Volatile.Read(ref requestCount);

    /// <summary>The HTTP status to answer with; flip to test failure → eviction → retry.</summary>
    public volatile int StatusCode = 200;

    /// <summary>The og:title the served head declares.</summary>
    public volatile string Title = "Served Title";

    /// <summary>An optional <c>&lt;link rel="icon"&gt;</c> href for the served head. Null (the
    /// default) declares NO icon link, so the card falls through to the og:image poster.</summary>
    public volatile string? IconHref;

    /// <summary>Serve the SPA catch-all shell instead — HTTP 200 with a plain <c>&lt;title&gt;</c>
    /// and no <c>og:*</c> tags — the successful-but-useless response a portal returns while it is
    /// restarting.</summary>
    public volatile bool OmitOgTags;

    public TestOgServer()
    {
        // An EMPTY builder: no appsettings.json, no environment variables, no command line. A test
        // helper that picked up ambient configuration would be one more thing to explain when it
        // behaves differently on a developer's box than on CI.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRoutingCore();
        builder.Logging.ClearProviders();

        app = builder.Build();
        app.Run(ServeAsync);
        app.Start();

        // Kestrel reports the address it actually bound, which is the whole point of :0. It comes
        // back without a trailing slash; callers concatenate paths onto BaseUrl, so add one.
        var address = app.Urls.First();
        BaseUrl = address.EndsWith('/') ? address : address + "/";
    }

    private async Task ServeAsync(HttpContext context)
    {
        Interlocked.Increment(ref requestCount);

        var iconHref = IconHref;
        var iconLink = iconHref is null ? string.Empty : $"<link rel=\"icon\" href=\"{iconHref}\">";
        // The per-path title mode moved with the OgCard MULTI-target test that used it
        // (the module left the platform); this copy serves the fixed title only.
        var title = Title;
        var head = OmitOgTags
            // The SPA catch-all shell a portal serves mid-restart: HTTP 200, a plain
            // <title>, and NO og:* tags whatsoever.
            ? "<title>Memex Portal</title>" + iconLink
            : $"<meta property=\"og:title\" content=\"{title}\">"
              + "<meta property=\"og:description\" content=\"Served description.\">"
              + "<meta property=\"og:image\" content=\"/og.png\">"
              + iconLink;
        var html = $"<html><head>{head}</head><body></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);

        try
        {
            context.Response.StatusCode = StatusCode;
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Client aborted mid-write — irrelevant to the assertions.
        }
    }

    /// <summary>
    /// Async because Kestrel's shutdown is: bridging it with <c>GetAwaiter().GetResult()</c> would
    /// park the caller's thread, which is the shape <c>BlockingBridgeInTestRatchetGuard</c> exists
    /// to keep out of this repo's tests.
    /// </summary>
    public async ValueTask DisposeAsync() => await app.DisposeAsync().ConfigureAwait(false);
}
