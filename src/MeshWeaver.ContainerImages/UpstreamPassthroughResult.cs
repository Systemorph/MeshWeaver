using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// Streams an upstream response straight to the caller: status, the headers an OCI client needs,
/// then the body copied socket-to-socket.
///
/// <para>🚨 The body is NEVER buffered. <c>CopyToAsync</c> against the raw response stream is what
/// keeps a 300 MB layer off the heap; materialising it would OOM the portal under a rolling
/// restart, when many pods pull at once.</para>
///
/// <para>🚨 And the copy runs THROUGH <see cref="IIoPool"/>, holding one slot for the whole
/// transfer. Not buffering keeps one layer off the heap; the pool is what keeps a HUNDRED
/// concurrent layer pulls — a rolling restart of a large deployment — from each holding a socket,
/// a buffer and a thread's worth of scheduling at once. The bound is backpressure, deliberately:
/// a pull that waits for a slot is correct, a portal that falls over is not. The pool slot is
/// held across the copy rather than only across the connect, because the transfer is the part
/// that costs.</para>
/// </summary>
public sealed class UpstreamPassthroughResult(HttpResponseMessage upstream) : IResult
{
    private readonly IIoPool? pool;
    private readonly byte[]? body;
    private readonly Func<Stream, ContainerBlobFill?>? fill;

    /// <summary>
    /// A passthrough whose body copy is bounded by <paramref name="ioPool"/>, optionally serving
    /// <paramref name="preRead"/> bytes the caller already read (a manifest it recorded) instead
    /// of the upstream stream.
    /// </summary>
    /// <param name="upstream">The upstream response; this result owns its disposal.</param>
    /// <param name="ioPool">The pool bounding the transfer, or null for an unbounded copy.</param>
    /// <param name="preRead">Body bytes already read, or null to stream the upstream body.</param>
    public UpstreamPassthroughResult(
        HttpResponseMessage upstream, IIoPool? ioPool, byte[]? preRead = null)
        : this(upstream)
    {
        pool = ioPool;
        body = preRead;
    }

    /// <summary>
    /// A passthrough that also FILLS the read-through cache as it streams — the miss half of the
    /// mirror.
    ///
    /// <para>The fill is created from the response body rather than handed in ready-made, so the
    /// temporary file is opened inside the pool slot that bounds the transfer and never before the
    /// transfer starts. A null return from <paramref name="cacheFill"/> means "not cacheable", and
    /// the copy is then exactly the uncached one.</para>
    /// </summary>
    /// <param name="upstream">The upstream response; this result owns its disposal.</param>
    /// <param name="ioPool">The pool bounding the transfer, or null for an unbounded copy.</param>
    /// <param name="cacheFill">Opens the tee over the response body, or returns null to skip
    /// caching this response.</param>
    public UpstreamPassthroughResult(
        HttpResponseMessage upstream, IIoPool? ioPool, Func<Stream, ContainerBlobFill?> cacheFill)
        : this(upstream)
    {
        pool = ioPool;
        fill = cacheFill;
    }

    /// <summary>Headers an OCI client depends on. <c>Docker-Content-Digest</c> is how a client
    /// verifies it got the bytes it asked for, and dropping it silently breaks digest pinning.
    /// <c>Link</c> is the <c>tags/list</c> continuation (a RELATIVE URL, so it resolves against
    /// the mirror) — without it a paginated listing ends after its first page and reads as
    /// complete.</summary>
    private static readonly string[] ForwardedHeaders =
    [
        "Docker-Content-Digest", "Content-Type", "Content-Length",
        "Accept-Ranges", "Content-Range", "ETag", "Docker-Distribution-Api-Version", "Link",
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

            if (body is not null)
            {
                // A manifest the caller already read in order to record it. Bounded by
                // ContainerImageOptions.MaxRecordedManifestBytes at the point it was read, so
                // this is kilobytes — never a layer.
                await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted);
                return;
            }

            // Content-Length is set from the upstream header above; Kestrel refuses a body longer
            // than it, so a truncated upstream surfaces as a failed request rather than a short
            // layer the client would cache as complete.
            if (pool is null)
            {
                await Copy(httpContext, httpContext.RequestAborted);
                return;
            }

            var logger = httpContext.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(UpstreamPassthroughResult));
            // 🚨 ObserveCompletion, never .ToTask() — a Task completed inside an Rx pipeline
            // resumes its awaiter INLINE on the signalling thread, still inside Rx's trampoline,
            // and in a pool that is the worst place for it: the continuation that runs there is
            // the one RELEASING THE SLOT.
            //
            // 🚨 cancelSource: true, because this wait OWNS a bounded resource. A client that
            // gives up mid-layer (a `docker pull` interrupted, a pod rescheduled) must release the
            // pool permit with it — under the non-disposing default the permit would be held for
            // the full remaining transfer that nobody is reading, invisibly, until the pool
            // starved (#2772).
            await pool.Invoke(ct => Copy(httpContext, ct))
                .FirstAsync()
                .ObserveCompletion(
                    ex => logger?.LogWarning(ex,
                        "Container registry mirror: the upstream body copy for {Path} faulted "
                        + "after the request had already been answered", httpContext.Request.Path),
                    cancelSource: true,
                    httpContext.RequestAborted);
        }
    }

    private async Task Copy(HttpContext httpContext, CancellationToken ct)
    {
        await using var source = await upstream.Content.ReadAsStreamAsync(ct);
        // 🚨 The tee is opened HERE, inside the pool slot, and only when the response is
        // cacheable. Everything else is the copy this method has always been.
        var tee = fill?.Invoke(httpContext.Response.Body);
        if (tee is null)
        {
            await source.CopyToAsync(httpContext.Response.Body, ct);
            return;
        }

        await using (tee)
        {
            await source.CopyToAsync(tee, ct);
            // CommitAsync never throws: by now the caller holds every byte, so a cache failure has
            // nothing left to break. An abort before this line leaves the temporary, which
            // DisposeAsync removes.
            await tee.CommitAsync(ct);
        }
    }
}
