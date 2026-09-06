using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// Serves a resident cache entry: the bytes the mirror already fetched, streamed from disk to the
/// caller WITHOUT contacting the upstream at all.
///
/// <para>That last clause is the whole availability claim. A pull whose every digest is resident
/// completes with the upstream unreachable, because nothing on this path talks to it.</para>
///
/// <para>🚨 Bounded and unbuffered for the same reasons as
/// <see cref="UpstreamPassthroughResult"/>: the copy runs through the <c>Blob</c> pool, holding
/// one slot for the whole transfer, and never materialises the entry. A cache hit on a 300 MB
/// layer must be no more dangerous to the portal than a miss.</para>
///
/// <para><c>Docker-Content-Digest</c> is the entry's KEY, and the entry was verified against it
/// when stored — so unlike a proxied response, this header cannot disagree with the bytes.</para>
/// </summary>
/// <param name="entry">The open entry; this result owns its disposal.</param>
/// <param name="pool">The pool bounding the transfer, or null for an unbounded copy.</param>
/// <param name="headersOnly">True for a HEAD request: the headers are the answer, and the body is
/// deliberately not read off disk.</param>
public sealed class CachedContentResult(
    ContainerCacheEntry entry, IIoPool? pool, bool headersOnly) : IResult
{
    /// <summary>Media type used when the entry carries no recorded one — a layer, which every OCI
    /// client treats as opaque bytes.</summary>
    public const string DefaultMediaType = "application/octet-stream";

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        await using (entry.Content)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.Headers["Content-Type"] = entry.MediaType ?? DefaultMediaType;
            httpContext.Response.Headers["Content-Length"] = entry.Length.ToString();
            httpContext.Response.Headers["Docker-Content-Digest"] = entry.Digest;
            httpContext.Response.Headers["Accept-Ranges"] = "bytes";
            httpContext.Response.Headers[ContainerImageEndpoints.ApiVersionHeader] =
                ContainerImageEndpoints.ApiVersion;

            if (headersOnly)
                return;

            if (pool is null)
            {
                await entry.Content.CopyToAsync(
                    httpContext.Response.Body, httpContext.RequestAborted);
                return;
            }

            var logger = httpContext.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(CachedContentResult));
            // 🚨 ObserveCompletion with cancelSource: true — identical reasoning to the upstream
            // passthrough. The wait owns a bounded resource, so a client that gives up mid-layer
            // must release the pool permit with it rather than leaving it held for a transfer
            // nobody is reading.
            await pool.Invoke(ct => entry.Content.CopyToAsync(httpContext.Response.Body, ct))
                .FirstAsync()
                .ObserveCompletion(
                    ex => logger?.LogWarning(ex,
                        "Container registry mirror: serving {Digest} from the cache faulted after "
                        + "the request had already been answered", entry.Digest),
                    cancelSource: true,
                    httpContext.RequestAborted);
        }
    }
}
