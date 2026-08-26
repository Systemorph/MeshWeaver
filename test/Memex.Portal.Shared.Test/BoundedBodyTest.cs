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
}
