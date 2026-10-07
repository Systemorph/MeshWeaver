using System.Net;
using Memex.Portal.Shared.Seo;
using MeshWeaver.Mesh;
using SkiaSharp;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the PHOTO card: an authored external picture is declared as the portal's own 1200×630 JPEG
/// (WhatsApp shows an empty frame for print-sized masters), the render fits the size budget, and the
/// fetch's SSRF fence refuses every non-public address.
/// </summary>
public class SharePhotoTest
{
    private static MeshNode Post(string? mediaUrl) =>
        new("Gap", "Posts")
        {
            NodeType = "SocialMedia/Post",
            Name = "Gap",
            Content = System.Text.Json.JsonSerializer.SerializeToElement(new { text = "body", mediaUrl }),
        };

    private static byte[] Noise(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = new Random(7);
        for (var y = 0; y < height; y += 4)
        for (var x = 0; x < width; x += 4)
        {
            var color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            for (var dy = 0; dy < 4 && y + dy < height; dy++)
            for (var dx = 0; dx < 4 && x + dx < width; dx++)
                bitmap.SetPixel(x + dx, y + dy, color);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    [Fact]
    public void ShareImage_ExternalAuthoredPicture_IsDeclaredAsThePhotoCard()
    {
        var image = SeoResolver.ShareImage(Post("https://cdn.example.com/master.png"));

        Assert.Equal("/api/og/Posts/Gap.jpg", image);
        Assert.True(SeoResolver.IsPhotoCard(image));
        Assert.False(SeoResolver.IsGeneratedCard(image)); // the head must not declare it image/png
    }

    [Fact]
    public void ShareImage_RootRelativeAuthoredOrNone_Unchanged()
    {
        Assert.Equal("/api/content/S/og.png", SeoResolver.ShareImage(Post("/api/content/S/og.png")));
        Assert.Equal("/api/og/Posts/Gap.png", SeoResolver.ShareImage(Post(null)));
        Assert.True(SeoResolver.IsGeneratedCard("/api/og/Posts/Gap.png"));
    }

    [Fact]
    public void PreviewCard_ExternalAuthoredPicture_UsesThePhotoCard()
    {
        Assert.Equal("/api/og/Posts/Gap.jpg",
            SeoResolver.ComposePreviewCard(Post("https://cdn.example.com/master.jpg"), null).Image);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    public void Render_PrintMaster_FitsTheCardAndTheBudget(SKEncodedImageFormat format)
    {
        var master = Noise(3720, 2500, format);

        var card = SharePhoto.Render(master);

        Assert.NotNull(card);
        Assert.True(card!.Length <= SharePhoto.TargetBytes, $"{card.Length} bytes");
        using var decoded = SKBitmap.Decode(card);
        Assert.Equal(OgCardRenderer.Width, decoded.Width);
        Assert.Equal(OgCardRenderer.Height, decoded.Height);
        Assert.Equal(SKEncodedImageFormat.Jpeg, SKCodec.Create(new SKMemoryStream(card)).EncodedFormat);
    }

    [Fact]
    public void Render_NotAPicture_IsNull() =>
        Assert.Null(SharePhoto.Render("<html>not an image</html>"u8.ToArray()));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Fence_RefusesNonPublicAddresses(string address) =>
        Assert.False(SharePhoto.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("104.18.38.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1")]
    public void Fence_AdmitsPublicAddresses(string address) =>
        Assert.True(SharePhoto.IsPublic(IPAddress.Parse(address)));
}
