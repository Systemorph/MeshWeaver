using Microsoft.AspNetCore.Http;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// Streams an upstream response straight to the caller: status, the headers an OCI client needs,
/// then the body copied socket-to-socket.
///
/// <para>🚨 The body is NEVER buffered. <c>CopyToAsync</c> against the raw response stream is what
/// keeps a 300 MB layer off the heap; materialising it would OOM the portal under a rolling
/// restart, when many pods pull at once.</para>
/// </summary>
public sealed class UpstreamPassthroughResult(HttpResponseMessage upstream) : IResult
{
    /// <summary>Headers an OCI client depends on. <c>Docker-Content-Digest</c> is how a client
    /// verifies it got the bytes it asked for, and dropping it silently breaks digest pinning.</summary>
    private static readonly string[] ForwardedHeaders =
    [
        "Docker-Content-Digest", "Content-Type", "Content-Length",
        "Accept-Ranges", "Content-Range", "ETag", "Docker-Distribution-Api-Version",
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        using (upstream)
        {
            httpContext.Response.StatusCode = (int)upstream.StatusCode;
            foreach (var name in ForwardedHeaders)
            {
                if (upstream.Headers.TryGetValues(name, out var v))
                    httpContext.Response.Headers[name] = v.ToArray();
                else if (upstream.Content.Headers.TryGetValues(name, out var cv))
                    httpContext.Response.Headers[name] = cv.ToArray();
            }

            // Content-Length is set from the upstream header above; Kestrel refuses a body longer
            // than it, so a truncated upstream surfaces as a failed request rather than a short
            // layer the client would cache as complete.
            await using var body = await upstream.Content.ReadAsStreamAsync(httpContext.RequestAborted);
            await body.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
        }
    }
}
