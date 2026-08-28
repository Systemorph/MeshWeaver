using System.Text;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// Reads a request body up to a hard byte cap, and says so when the cap is exceeded.
///
/// <para>🚨 <c>WebhookInboxEndpoints</c> checked <c>Content-Length</c> and then read the body with a
/// plain <c>StreamReader.ReadToEndAsync()</c> — while a comment beside it described "the capped
/// reader below" that did not exist (#2302). A chunked request carries no Content-Length, so that
/// path buffered an unbounded body into memory on a PUBLIC endpoint. This is the reader the comment
/// promised. Pure over a <see cref="Stream"/> so it is unit-tested with a MemoryStream.</para>
/// </summary>
public static class BoundedBody
{
    /// <summary>
    /// The body as UTF-8 text, or <c>null</c> if it exceeds <paramref name="maxBytes"/>. Reads at
    /// most <c>maxBytes + 1</c> bytes, so an oversized body is refused without being buffered.
    /// </summary>
    public static async Task<string?> ReadAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        var buffer = await ReadBufferAsync(body, maxBytes, ct);
        return buffer is null ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// The body as RAW BYTES, or <c>null</c> if it exceeds <paramref name="maxBytes"/>. Same reader,
    /// same cap.
    ///
    /// <para>🚨 Byte-exact, and that is the point: a signature is computed over the bytes GitHub
    /// sent. Reading through <see cref="ReadAsync"/> and re-encoding the string would round-trip
    /// through UTF-8, and any byte sequence that is not valid UTF-8 comes back as U+FFFD — a body
    /// that verifies at the sender would fail here, and worse, one crafted around the substitution
    /// could differ from what was verified. Signature verification must never see a decoded copy.</para>
    /// </summary>
    public static async Task<byte[]?> ReadBytesAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        var buffer = await ReadBufferAsync(body, maxBytes, ct);
        return buffer?.ToArray();
    }

    /// <summary>The one reader both public forms share, so the cap semantics cannot drift apart.</summary>
    private static async Task<MemoryStream?> ReadBufferAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            // 🚨 Ask for no more than the remaining budget PLUS ONE byte. A full-chunk request let the
            // socket read overshoot the cap by up to a chunk before this returned null — the buffer
            // never exceeded the cap, but the stated contract ("at most max+1 bytes READ") was not what
            // the code did. Caught in review. The +1 is what detects "over the cap" without holding it.
            var remaining = maxBytes + 1 - buffer.Length;
            var want = (int)Math.Min(chunk.Length, remaining);
            var read = await body.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer;
    }
}
