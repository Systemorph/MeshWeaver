using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Reactive.Assertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A shared post unfurled in WhatsApp as an EMPTY large card (2026-10-07): title, description
/// and domain present, the picture box reserved and blank. The head declared the post's
/// <c>mediaUrl</c> — a PROGRESSIVE 1264×848 JPEG on a third-party Supabase host — raw, with no
/// <c>og:image:type</c> and no size, while <c>twitter:card=summary_large_image</c> committed the
/// unfurler to the large layout. The drawn card, which is a declared 1200×630 image on our own
/// origin, was never affected.
///
/// <para>The fix re-serves an authored foreign picture from this origin as
/// <c>/api/og/{node}.jpg</c>: a baseline 1200×630 JPEG within the byte budget, declared as such.
/// These pin both halves — what the head is told to declare, and that the route then actually
/// answers with exactly that — through the endpoint's OWN decision
/// (<see cref="SeoEndpoints.AuthoredCardResult"/>) over a real
/// <see cref="OpenGraphPreviewService"/>, whose HTTP leg is an in-process handler: no network.</para>
///
/// <para>The foreign host is a literal TEST-NET-3 address (203.0.113.0/24): public by the SSRF
/// guard's rules, so the production guard runs unmodified, and it never resolves through DNS.</para>
/// </summary>
public class AuthoredShareImageTest
{
    private const string MediaUrl = "https://203.0.113.10/storage/v1/object/public/post-images/master.jpg";

    /// <summary>A 316×212 PROGRESSIVE JPEG (cjpeg -progressive): left half red, right half blue.
    /// The progressive encoding is the property under test; the two halves let a pixel read tell the
    /// authored picture from the drawn card.</summary>
    private static readonly byte[] ProgressiveFixture = Convert.FromBase64String(
        "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"
        + "/9k="    );

    private static MeshNode Post(object? content) =>
        new("CrossingTheChasmPlaybook", "Posts") { NodeType = "SocialMedia/Post", Name = "Crossing The Chasm Playbook", Content = content };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // ── What the head is told to declare ─────────────────────────────────────────────────────────

    /// <summary>
    /// The head names OUR route for a foreign authored picture, and that route is one the head can
    /// declare a type and a size for. Before the fix <see cref="SeoResolver.ShareImage"/> returned
    /// the Supabase URL itself and <see cref="SeoResolver.IsGeneratedCard"/> said false for it — the
    /// "no type, no size" head the reporter's card was built from.
    /// </summary>
    [Fact]
    public void AForeignMediaUrl_IsDeclaredAsTheReServedCard_WithItsTypeAndSize()
    {
        var post = Post(Json(new { text = "post", mediaUrl = MediaUrl }));

        var image = SeoResolver.ShareImage(post);

        Assert.Equal("/api/og/Posts/CrossingTheChasmPlaybook.jpg", image);
        Assert.True(SeoResolver.IsGeneratedCard(image), "the head declares 1200×630 only for a portal-served card");
        Assert.Equal("image/jpeg", SeoResolver.CardMediaType(image));
        // The route still reads the ORIGINAL — the authored field is not rewritten.
        Assert.Equal(MediaUrl, SeoResolver.ExtractImage(post));
    }

    /// <summary>
    /// NEGATIVE CONTROLS on the same instrument: a picture on this origin's own content route is
    /// declared unchanged with no invented type, and a page that authored nothing keeps the drawn
    /// PNG — so the rule above fires on "foreign", not on "authored" or on everything.
    /// </summary>
    [Fact]
    public void AnOwnOriginPicture_AndNoPicture_AreUntouched()
    {
        var own = Post(Json(new { ogImage = "/api/content/Posts/content/og.png" }));
        Assert.Equal("/api/content/Posts/content/og.png", SeoResolver.ShareImage(own));
        Assert.Null(SeoResolver.CardMediaType(SeoResolver.ShareImage(own)));

        var none = Post(Json(new { text = "post" }));
        Assert.Equal("/api/og/Posts/CrossingTheChasmPlaybook.png", SeoResolver.ShareImage(none));
        Assert.Equal("image/png", SeoResolver.CardMediaType(SeoResolver.ShareImage(none)));
        Assert.Null(SeoResolver.CardMediaType("https://cdn.example/api/og/banner.jpg"));
    }

    // ── What the route then answers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The precondition, measured rather than assumed: the fixture really IS progressive. Without
    /// this, the baseline assertion below could pass on a fixture that was baseline to begin with.
    /// </summary>
    [Fact]
    public void TheFixtureIsAProgressiveJpeg()
    {
        var (progressive, width, height) = JpegFrame(ProgressiveFixture);
        Assert.True(progressive);
        Assert.Equal((316, 212), (width, height));
    }

    /// <summary>
    /// 🚨 THE FIX, end to end through the route's own decision: the foreign progressive JPEG comes
    /// back as exactly what the head declares — a BASELINE JPEG of exactly 1200×630, inside the byte
    /// budget, shared-cacheable — and it is the AUTHORED picture (red left of centre, blue right),
    /// fitted whole rather than cropped.
    /// </summary>
    [Fact]
    public async Task AForeignProgressiveJpeg_IsServedAsTheDeclaredBaseline1200x630Jpeg()
    {
        var foreign = new ForeignHost(_ => Image(ProgressiveFixture));
        using var renderer = new OgCardRenderer("Memex");

        var jpeg = await Serve(foreign, renderer, "the authored picture must be re-served");

        var (progressive, width, height) = JpegFrame(jpeg.Bytes);
        Assert.False(progressive, "a progressive JPEG is one of the encodings an unfurler may refuse");
        Assert.Equal((OgCardRenderer.Width, OgCardRenderer.Height), (width, height));
        Assert.Equal("image/jpeg", jpeg.ContentType);
        Assert.True(jpeg.Bytes.Length <= OgCardRenderer.MaxShareImageBytes, $"{jpeg.Bytes.Length} bytes is over the budget");
        Assert.Equal("public, max-age=86400", jpeg.CacheControl);
        Assert.Equal(Hue.Red, HueAt(jpeg.Bytes, 400, 315));
        Assert.Equal(Hue.Blue, HueAt(jpeg.Bytes, 800, 315));
        Assert.Equal("image/*", foreign.LastAccept);
    }

    /// <summary>
    /// A foreign host that does not hand over a picture — down, or answering an HTML challenge page
    /// with 200 — still gets the unfurler what the head promised: the node's DRAWN card, as a
    /// 1200×630 baseline JPEG. And the failure is not cached: the next request fetches again and,
    /// the host having recovered, serves the authored picture. (NEGATIVE CONTROL for the pixel read
    /// above: the drawn card is not red at the same spot.)
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "text/plain")]
    [InlineData(HttpStatusCode.OK, "text/html")]
    public async Task APictureThatCannotBeHad_ServesTheDrawnCardAsJpeg_AndTheNextRequestTriesAgain(
        HttpStatusCode status, string mediaType)
    {
        var recovered = 0;
        var foreign = new ForeignHost(_ => Volatile.Read(ref recovered) == 0
            ? new HttpResponseMessage(status) { Content = new StringContent("<html>challenge</html>", System.Text.Encoding.UTF8, mediaType) }
            : Image(ProgressiveFixture));
        using var renderer = new OgCardRenderer("Memex");

        var fallback = await Serve(foreign, renderer, "an unavailable picture must still answer");
        var (progressive, width, height) = JpegFrame(fallback.Bytes);
        Assert.False(progressive);
        Assert.Equal((OgCardRenderer.Width, OgCardRenderer.Height), (width, height));
        Assert.NotEqual(Hue.Red, HueAt(fallback.Bytes, 400, 315));

        Volatile.Write(ref recovered, 1);
        var authored = await Serve(foreign, renderer, "the retry must reach the recovered host");
        Assert.Equal(Hue.Red, HueAt(authored.Bytes, 400, 315));
        Assert.Equal(2, foreign.Requests);
    }

    /// <summary>
    /// Nothing is cached in the process — an unbounded per-URL cache would grow with every public
    /// node and edit, and go stale when an author replaces the bytes behind one URL. Each request
    /// re-reads the source; the response's strong ETag and day-long shared cacheability are what
    /// spare the foreign host. Same bytes in, same bytes out, so the ETag holds across requests.
    /// </summary>
    [Fact]
    public async Task EachRequestReReadsTheSource_AndYieldsTheSameBytes()
    {
        var foreign = new ForeignHost(_ => Image(ProgressiveFixture));
        using var renderer = new OgCardRenderer("Memex");

        var first = await Serve(foreign, renderer, "first request");
        var second = await Serve(foreign, renderer, "second request");

        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(2, foreign.Requests);
    }

    /// <summary>
    /// A phone photo stored sideways is served UPRIGHT. The fixture with an EXIF Orientation of 6
    /// (stored rotated; display turns it 90° clockwise) puts its red half on TOP; read without the
    /// orientation it would sit on the left, so top and bottom of the centre column would agree.
    /// </summary>
    [Fact]
    public async Task AnExifRotatedPicture_IsServedUpright()
    {
        var foreign = new ForeignHost(_ => Image(WithExifOrientation(ProgressiveFixture, 6)));
        using var renderer = new OgCardRenderer("Memex");

        var jpeg = await Serve(foreign, renderer, "the rotated picture must be re-served");

        Assert.Equal(Hue.Red, HueAt(jpeg.Bytes, 600, 120));
        Assert.Equal(Hue.Blue, HueAt(jpeg.Bytes, 600, 510));
    }

    /// <summary>
    /// 🚨 A decompression bomb is refused BEFORE allocation: a few-hundred-byte PNG declaring
    /// 60000×60000 pixels would be a 14 GB bitmap. It never reaches a decode — the declared size is
    /// over <see cref="OgCardRenderer.MaxDecodedPixels"/> — and the route serves the drawn card.
    /// </summary>
    [Fact]
    public async Task APictureDeclaringHugeDimensions_IsRefusedAndTheDrawnCardServed()
    {
        var bomb = PngDeclaring(60_000, 60_000);
        using var renderer = new OgCardRenderer("Memex");
        Assert.Null(renderer.NormaliseAuthored(bomb));

        var foreign = new ForeignHost(_ => Image(bomb, "image/png"));
        var served = await Serve(foreign, renderer, "a refused picture must still answer");
        var (_, width, height) = JpegFrame(served.Bytes);
        Assert.Equal((OgCardRenderer.Width, OgCardRenderer.Height), (width, height));
        Assert.NotEqual(Hue.Red, HueAt(served.Bytes, 400, 315));
    }

    /// <summary>
    /// A SCHEME-RELATIVE authored URL (<c>//host/…</c>) starts with <c>/</c> but names another host,
    /// so it is re-served like an absolute one — declared as the portal card, fetched as https —
    /// rather than slipping through as a raw, undeclared foreign picture.
    /// </summary>
    [Fact]
    public async Task ASchemeRelativeMediaUrl_IsTreatedAsForeign()
    {
        const string networkPath = "//203.0.113.10/storage/v1/object/public/post-images/master.jpg";
        var post = Post(Json(new { text = "post", mediaUrl = networkPath }));
        Assert.Equal("/api/og/Posts/CrossingTheChasmPlaybook.jpg", SeoResolver.ShareImage(post));

        Uri? asked = null;
        var foreign = new ForeignHost(request => { asked = request.RequestUri; return Image(ProgressiveFixture); });
        using var renderer = new OgCardRenderer("Memex");
        var served = await Serve(foreign, renderer, "the network-path picture must be re-served", networkPath);

        Assert.Equal("https://203.0.113.10/storage/v1/object/public/post-images/master.jpg", asked?.ToString());
        Assert.Equal(Hue.Red, HueAt(served.Bytes, 400, 315));
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Served(byte[] Bytes, string? ContentType, string CacheControl);

    private static async Task<Served> Serve(
        ForeignHost foreign, OgCardRenderer renderer, string because, string mediaUrl = MediaUrl)
    {
        var http = new DefaultHttpContext();
        var result = await SeoEndpoints.AuthoredCardResult(
                foreign.Fetcher, NullLogger.Instance, http, renderer,
                Post(Json(new { text = "post", mediaUrl })), sharedCacheable: true)
            .Should().Within(TestTimeouts.Convergence).Emit(because);
        var file = Assert.IsType<FileContentHttpResult>(result);
        return new Served(file.FileContents.ToArray(), file.ContentType, http.Response.Headers.CacheControl.ToString());
    }

    private static HttpResponseMessage Image(byte[] bytes, string mediaType = "image/jpeg")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>The JPEG with an APP1 Exif segment carrying one Orientation tag, spliced in after SOI.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // big-endian header, IFD0 at 8
            0x00, 0x01,                                               // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,           // Orientation, SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,  // value
            0x00, 0x00, 0x00, 0x00,                                   // no next IFD
        ];
        byte[] exifHeader = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00];
        var length = 2 + exifHeader.Length + tiff.Length;
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exifHeader, .. tiff];
        return [.. jpeg.Take(2), .. app1, .. jpeg.Skip(2)];
    }

    /// <summary>A structurally valid PNG whose IHDR declares <paramref name="width"/>×<paramref name="height"/>
    /// and whose image data is a single empty deflate stream — tiny on the wire, huge if decoded.</summary>
    private static byte[] PngDeclaring(int width, int height)
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            var crc = Crc32(typed);
            return [.. BigEndian(data.Length), .. typed, .. BigEndian((int)crc)];
        }
        static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }
        byte[] ihdr = [.. BigEndian(width), .. BigEndian(height), 8, 2, 0, 0, 0]; // 8-bit RGB
        byte[] idat = [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];           // empty zlib stream
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", ihdr), .. Chunk("IDAT", idat), .. Chunk("IEND", [])];
    }

    /// <summary>The foreign picture host: a real <see cref="OpenGraphPreviewService"/> whose client
    /// is backed by an in-process handler, counting what it is asked.</summary>
    private sealed class ForeignHost : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> answer;
        private int requests;

        public ForeignHost(Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            this.answer = answer;
            Fetcher = new OpenGraphPreviewService(
                new ServiceCollection().AddSingleton<IHttpClientFactory>(this).BuildServiceProvider());
        }

        public OpenGraphPreviewService Fetcher { get; }
        public int Requests => Volatile.Read(ref requests);
        public string? LastAccept { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            LastAccept = request.Headers.Accept.ToString();
            return Task.FromResult(answer(request));
        }
    }

    /// <summary>The frame header of a JPEG: SOF2 is progressive, SOF0/SOF1 baseline.</summary>
    private static (bool Progressive, int Width, int Height) JpegFrame(byte[] jpeg)
    {
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg.Take(2).ToArray());
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            Assert.Equal(0xFF, jpeg[i]);
            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2)
                return (marker == 0xC2, (jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6]);
            i += 2 + length;
        }
        throw new Xunit.Sdk.XunitException("no frame header found — not a JPEG");
    }

    private enum Hue { Red, Blue, Other }

    private static Hue HueAt(byte[] jpeg, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        var c = bitmap.GetPixel(x, y);
        return c.Red > 150 && c.Blue < 90 ? Hue.Red
            : c.Blue > 150 && c.Red < 90 ? Hue.Blue
            : Hue.Other;
    }
}
