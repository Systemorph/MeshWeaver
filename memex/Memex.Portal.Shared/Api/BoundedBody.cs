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
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await body.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
