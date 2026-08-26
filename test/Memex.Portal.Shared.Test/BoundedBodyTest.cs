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

    [Fact]
    public async Task Empty_body_is_empty_string_not_null()
    {
        using var s = new MemoryStream();
        Assert.Equal("", await BoundedBody.ReadAsync(s, maxBytes: 10, CancellationToken.None));
    }
}
