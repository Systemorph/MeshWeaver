using MeshWeaver.ThumbnailGenerator;
using SkiaSharp;
using Xunit;

namespace MeshWeaver.ThumbnailGenerator.Test;

/// <summary>
/// Pins the thumbnail generator's output after the move from SixLabors.ImageSharp to SkiaSharp:
/// the same box fitting, an exact-size scale and a PNG file.
/// </summary>
public class ThumbnailResizerTest
{
    private static readonly string Fixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "screenshot-800x450.png");

    [Fact]
    public void A_2x_capture_becomes_a_png_thumbnail_of_the_fitted_size()
    {
        var output = Path.Combine(Path.GetTempPath(), $"thumbnail-{Guid.NewGuid():N}.png");
        try
        {
            // The generator's defaults: a 400x300 box. 16:9 content is wider than 4:3, so it fits
            // to the width — 400x225 — exactly as the committed sample thumbnails are sized.
            var (width, height) = ThumbnailResizer.CalculateScaledDimensions(800, 450, 400, 300);
            Assert.Equal((400, 225), (width, height));

            ThumbnailResizer.ResizePng(Fixture, output, width, height);

            using var codec = SKCodec.Create(output);
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
            Assert.Equal(400, codec.Info.Width);
            Assert.Equal(225, codec.Info.Height);

            // Scaled, not cropped or blanked: the thumbnail carries the capture's pixels.
            using var bitmap = SKBitmap.Decode(output);
            var distinct = new HashSet<SKColor>();
            for (var y = 0; y < bitmap.Height; y += 15)
                for (var x = 0; x < bitmap.Width; x += 20)
                    distinct.Add(bitmap.GetPixel(x, y));
            Assert.True(distinct.Count > 1, "The thumbnail is a single flat colour — nothing of the capture survived.");
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(1920, 1080, 400, 300, 400, 225)] // wider than the box: fit to width
    [InlineData(600, 1200, 400, 300, 150, 300)]  // taller than the box: fit to height
    [InlineData(800, 600, 400, 300, 400, 300)]   // same aspect: fills the box
    public void Content_is_fitted_into_the_box_preserving_its_aspect_ratio(
        int contentWidth, int contentHeight, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
        => Assert.Equal((expectedWidth, expectedHeight),
            ThumbnailResizer.CalculateScaledDimensions(contentWidth, contentHeight, maxWidth, maxHeight));

    [Fact]
    public void An_undecodable_source_is_refused_by_name()
    {
        var notAnImage = Path.Combine(Path.GetTempPath(), $"not-an-image-{Guid.NewGuid():N}.png");
        File.WriteAllText(notAnImage, "not a png");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                ThumbnailResizer.ResizePng(notAnImage, notAnImage + ".out.png", 10, 10));
            Assert.Contains(notAnImage, ex.Message);
        }
        finally
        {
            File.Delete(notAnImage);
        }
    }
}
