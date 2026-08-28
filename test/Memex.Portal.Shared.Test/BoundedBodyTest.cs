using System.Text;
using Memex.Portal.Shared.Api;
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
}
