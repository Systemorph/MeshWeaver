using System.Text;
using Memex.Portal.Shared.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Memex.Portal.Shared.Test;

public class BoundedBodyTest
{
    [Fact]
    public async Task Body_within_the_cap_is_returned_intact()
    {
        var text = new string('x', 1000);
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.Equal(text, await BoundedBody.ReadAsync(s, maxBytes: 1000, CancellationToken.None));
    }

    [Fact]
    public async Task Body_over_the_cap_is_refused_not_buffered()
    {
        // No Content-Length is involved at all — this is the chunked-request shape.
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1001)));
        Assert.Null(await BoundedBody.ReadAsync(s, maxBytes: 1000, CancellationToken.None));
    }

    /// <summary>The contract is on bytes READ, not bytes buffered: an oversized body must be refused
    /// after at most max+1 bytes have been pulled from the stream. Only a byte counter can tell an
    /// enforced cap from a promised one — null comes back either way.</summary>
    [Fact]
    public async Task Never_reads_more_than_max_plus_one_bytes_from_the_stream()
    {
        var counting = new CountingStream(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 100_000))));
        Assert.Null(await BoundedBody.ReadAsync(counting, maxBytes: 1000, CancellationToken.None));
        Assert.True(counting.BytesRead <= 1001, $"read {counting.BytesRead} bytes; the contract is at most 1001");
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var n = await inner.ReadAsync(buffer, ct); BytesRead += n; return n; }
        public override int Read(byte[] buffer, int offset, int count)
        { var n = inner.Read(buffer, offset, count); BytesRead += n; return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Empty_body_is_empty_string_not_null()
    {
        using var s = new MemoryStream();
        Assert.Equal("", await BoundedBody.ReadAsync(s, maxBytes: 10, CancellationToken.None));
    }

    /// <summary>
    /// The bytes form must be BYTE-EXACT. A signature is computed over what the sender sent, so a
    /// body that is not valid UTF-8 must survive the read unchanged — reading it as a string and
    /// re-encoding turns every invalid sequence into U+FFFD, and the HMAC would then be computed
    /// over bytes nobody signed. 0xFF 0xFE is not valid UTF-8, which is exactly why it is the probe.
    /// </summary>
    [Fact]
    public async Task ReadBytes_is_byte_exact_for_input_that_is_not_valid_utf8()
    {
        byte[] raw = [0x7B, 0xFF, 0xFE, 0x00, 0x7D];
        var got = await BoundedBody.ReadBytesAsync(new MemoryStream(raw), 1024, TestContext.Current.CancellationToken);
        Assert.Equal(raw, got);

        // And the contrast that makes the point: the string form cannot round-trip these bytes.
        var viaString = await BoundedBody.ReadAsync(new MemoryStream(raw), 1024, TestContext.Current.CancellationToken);
        Assert.NotEqual(raw, System.Text.Encoding.UTF8.GetBytes(viaString!));
    }

    [Fact]
    public async Task ReadBytes_over_the_cap_is_refused()
    {
        var over = new byte[65];
        var got = await BoundedBody.ReadBytesAsync(new MemoryStream(over), 64, TestContext.Current.CancellationToken);
        Assert.Null(got);
    }

    [Fact]
    public async Task ReadBytes_at_exactly_the_cap_is_returned()
    {
        var exact = new byte[64];
        for (var i = 0; i < exact.Length; i++) exact[i] = (byte)i;
        var got = await BoundedBody.ReadBytesAsync(new MemoryStream(exact), 64, TestContext.Current.CancellationToken);
        Assert.Equal(exact, got);
    }

    /// <summary>
    /// 🚨 A TRUNCATED body is not an OVERSIZED one (#4860). When a client aborts mid-delivery,
    /// Kestrel raises "Unexpected end of request content" from the body read. Swallowing that into
    /// the <c>null</c> that means "over the cap" would answer 413 to a caller whose connection
    /// dropped, and log a cap breach for a body far under the cap. The two outcomes must stay
    /// distinguishable, so the exception propagates and the endpoints turn it into a 400.
    /// </summary>
    [Fact]
    public async Task A_client_disconnect_mid_body_is_not_reported_as_over_the_cap()
    {
        // 8 bytes arrive, then the connection drops — far below a 1000-byte cap, so if this came
        // back as null the caller could not tell it from a payload that was genuinely too large.
        var truncated = new TruncatedStream(deliver: 8);

        await Assert.ThrowsAsync<BadHttpRequestException>(
            () => BoundedBody.ReadBytesAsync(truncated, maxBytes: 1000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_string_form_keeps_the_same_distinction()
        => await Assert.ThrowsAsync<BadHttpRequestException>(
            () => BoundedBody.ReadAsync(new TruncatedStream(deliver: 8), maxBytes: 1000,
                TestContext.Current.CancellationToken));

    /// <summary>Delivers <paramref name="deliver"/> bytes, then fails the way Kestrel does when the
    /// peer goes away before the declared Content-Length has arrived.</summary>
    private sealed class TruncatedStream(int deliver) : Stream
    {
        private int _remaining = deliver;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0)
                throw new BadHttpRequestException("Unexpected end of request content.", 400);
            var n = Math.Min(_remaining, buffer.Length);
            buffer.Span[..n].Fill((byte)'x');
            _remaining -= n;
            return ValueTask.FromResult(n);
        }

        // Plain synchronous fill — never a blocking bridge over the async form
        // (BlockingBridgeInTestRatchetGuard holds test/ at zero, and bridging the async form
        // here would trip it while changing what the test measures).
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                throw new BadHttpRequestException("Unexpected end of request content.", 400);
            var n = Math.Min(_remaining, count);
            Array.Fill(buffer, (byte)'x', offset, n);
            _remaining -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
