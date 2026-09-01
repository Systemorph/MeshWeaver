using System;
using System.Linq;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// What <c>/api/icon/{node}.png</c> actually answers. These drive
/// <see cref="SeoEndpoints.IconResult"/> — the endpoint's OWN decision, reached from the route —
/// rather than a re-implementation of it beside the route, which could agree with itself while the
/// shipped answer is wrong.
///
/// <para>The permission decision is not re-tested here on purpose: the route reaches this only
/// through <see cref="SeoResolver.Resolve"/>, the same fail-closed <c>AnonymousGate</c> pass the
/// page head and the share card go through, so there is no second permission rule to drift.</para>
/// </summary>
public class NodeIconEndpointTest
{
    private const string AuthoredMark =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'>"
        + "<rect width='48' height='48' rx='10' fill='#f0d9b5'/>"
        + "<rect x='6' y='6' width='18' height='18' fill='#b58863'/></svg>";

    private static MeshNode Node(string? icon, string path = "Chess") =>
        new(path) { NodeType = "Store/Plugin", Name = "Chess", Icon = icon };

    private static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        int Be(int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
        return (Be(16), Be(20));
    }

    /// <summary>The node's authored mark, served as a real PNG of the requested size.</summary>
    [Theory]
    [InlineData(32)]
    [InlineData(180)]
    public void ANodeWithAnAuthoredMark_IsServedAsAPngOfThatSize(int size)
    {
        var http = new DefaultHttpContext();

        var result = SeoEndpoints.IconResult(http, Node(AuthoredMark), size);

        var file = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal("image/png", file.ContentType);
        var (width, height) = PngSize(file.FileContents.ToArray());
        Assert.Equal(size, width);
        Assert.Equal(size, height);
    }

    /// <summary>
    /// 🚨 THE FALLBACK, STATED: a node that carries no mark of its own gets a 404, and nothing ever
    /// requests it — <see cref="SeoResolver.ResolveIconLinks"/> emits no icon link for such a node,
    /// so the portal favicon simply stays. Redirecting to the site favicon here would look like a
    /// fix while telling every consumer that this node's mark IS the portal's, and synthesising a
    /// letter tile would put a picture in the head that the node never chose.
    /// </summary>
    [Fact]
    public void ANodeWithNoMarkOfItsOwn_Is404_AndItsHeadAdvertisesNothingToAskFor()
    {
        var http = new DefaultHttpContext();

        Assert.IsType<NotFound>(SeoEndpoints.IconResult(http, Node(null), 32));
        Assert.IsType<NotFound>(SeoEndpoints.IconResult(http, Node("   "), 32));
        // An emoji and a legacy Fluent NAME are characters, not pictures — same answer.
        Assert.IsType<NotFound>(SeoEndpoints.IconResult(http, Node("📊"), 32));
        Assert.IsType<NotFound>(SeoEndpoints.IconResult(http, Node("Document"), 32));

        Assert.Empty(SeoResolver.ResolveIconLinks(Node(null)));
        Assert.Empty(SeoResolver.ResolveIconLinks(Node("📊")));
    }

    /// <summary>
    /// A mark that is already a RASTER image needs no help — Safari reads those — so this route
    /// does not duplicate it, and the head declares that one URL exactly as it always did.
    /// </summary>
    [Fact]
    public void AMarkThatIsAlreadyRaster_IsNotRedrawnHere()
    {
        var node = Node("https://cdn.example.org/mark.png");

        Assert.IsType<NotFound>(SeoEndpoints.IconResult(new DefaultHttpContext(), node, 32));
        var link = Assert.Single(SeoResolver.ResolveIconLinks(node));
        Assert.Equal("https://cdn.example.org/mark.png", link.Href);
    }

    /// <summary>
    /// Inline svg that will not parse is a CONTENT defect. The route answers 404 like any other
    /// "no icon", but the fault is reported — otherwise a broken authored mark and a node with no
    /// mark are indistinguishable from outside, and the broken one is the only one anyone can fix.
    /// </summary>
    [Fact]
    public void MalformedAuthoredMarkup_Is404_ButIsReported()
    {
        Exception? reported = null;

        var result = SeoEndpoints.IconResult(
            new DefaultHttpContext(), Node("<svg><rect fill='#fff'</svg>"), 32, ex => reported = ex);

        Assert.IsType<NotFound>(result);
        Assert.NotNull(reported);
    }

    /// <summary>
    /// The strong ETag and the shared cache directive: crawlers and browsers refetch favicons
    /// aggressively, and the tag is the render's own hash, so a node that changes its mark produces
    /// a new icon rather than a stale one.
    /// </summary>
    [Fact]
    public void TheResponse_CarriesAStrongEtagAndIsSharedCacheable()
    {
        var http = new DefaultHttpContext();

        SeoEndpoints.IconResult(http, Node(AuthoredMark), 32);

        var etag = http.Response.Headers.ETag.ToString();
        Assert.StartsWith("\"", etag);
        Assert.Equal("public, max-age=86400", http.Response.Headers.CacheControl.ToString());
    }

    /// <summary>A conditional GET whose tag still matches is answered 304, not re-sent.</summary>
    [Fact]
    public void AMatchingIfNoneMatch_Is304()
    {
        var first = new DefaultHttpContext();
        SeoEndpoints.IconResult(first, Node(AuthoredMark), 32);
        var etag = first.Response.Headers.ETag.ToString();

        var second = new DefaultHttpContext();
        second.Request.Headers.IfNoneMatch = etag;

        var result = SeoEndpoints.IconResult(second, Node(AuthoredMark), 32);

        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
    }
}
