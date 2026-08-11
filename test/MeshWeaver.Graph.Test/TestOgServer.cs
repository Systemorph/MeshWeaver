using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A tiny loopback HTTP server for the Open Graph tests: serves a configurable HTML head and
/// counts requests, so the promise-cache ("fetch once, replay to all") and the failure-eviction
/// retry can be asserted against REAL HTTP — no mocks (WritingTests.md).
/// </summary>
internal sealed class TestOgServer : IDisposable
{
    private readonly HttpListener listener;
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
        // HttpListener cannot bind port 0; reserve a free port via TcpListener first.
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        BaseUrl = $"http://127.0.0.1:{port}/";
        listener = new HttpListener();
        listener.Prefixes.Add(BaseUrl);
        listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!listener.IsListening)
            {
                return; // listener stopped — normal teardown
            }

            Interlocked.Increment(ref requestCount);
            var iconHref = IconHref;
            var iconLink = iconHref is null ? string.Empty : $"<link rel=\"icon\" href=\"{iconHref}\">";
            var head = OmitOgTags
                // The SPA catch-all shell a portal serves mid-restart: HTTP 200, a plain
                // <title>, and NO og:* tags whatsoever.
                ? "<title>Memex Portal</title>" + iconLink
                : $"<meta property=\"og:title\" content=\"{Title}\">"
                  + "<meta property=\"og:description\" content=\"Served description.\">"
                  + "<meta property=\"og:image\" content=\"/og.png\">"
                  + iconLink;
            var html = $"<html><head>{head}</head><body></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            try
            {
                context.Response.StatusCode = StatusCode;
                context.Response.ContentType = "text/html";
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
            }
            catch (Exception)
            {
                // Client aborted mid-write — irrelevant to the assertions.
            }
        }
    }

    public void Dispose()
    {
        try
        {
            listener.Stop();
            listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
