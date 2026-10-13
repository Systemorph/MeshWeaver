using SkiaSharp;

namespace MeshWeaver.ThumbnailGenerator;

/// <summary>
/// The image half of the thumbnail generator: fits the captured content into the thumbnail box and
/// scales the 2x screenshot down to it. Drawn with SkiaSharp (MIT), the library the portal already
/// ships for its share cards and favicons; SixLabors.ImageSharp was dropped because every version up
/// to 4.1.1 carries high-severity advisories and the patched line needs a paid licence.
/// </summary>
public static class ThumbnailResizer
{
    /// <summary>
    /// Scales a PNG to exactly <paramref name="width"/> x <paramref name="height"/> with a bicubic
    /// (Catmull-Rom) filter — the same family as ImageSharp's default <c>Resize</c> sampler — and
    /// writes it as a lossless PNG.
    /// </summary>
    public static void ResizePng(string sourcePath, string destinationPath, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width),
                $"A thumbnail needs a positive size; got {width}x{height}.");

        using var source = SKBitmap.Decode(sourcePath)
            ?? throw new InvalidDataException($"'{sourcePath}' is not an image SkiaSharp can decode.");
        using var resized = source.Resize(
            new SKImageInfo(width, height, source.ColorType, source.AlphaType),
            new SKSamplingOptions(SKCubicResampler.CatmullRom))
            ?? throw new InvalidOperationException(
                $"SkiaSharp could not resize '{sourcePath}' to {width}x{height}.");
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(destinationPath);
        data.SaveTo(output);
    }

    /// <summary>
    /// Fits content of the given size into a <paramref name="maxWidth"/> x <paramref name="maxHeight"/>
    /// box, preserving its aspect ratio.
    /// </summary>
    public static (int Width, int Height) CalculateScaledDimensions(int contentWidth, int contentHeight, int maxWidth, int maxHeight)
    {
        double contentAspectRatio = (double)contentWidth / contentHeight;
        double targetAspectRatio = (double)maxWidth / maxHeight;

        // Wider content fits to the width, taller content to the height.
        return contentAspectRatio > targetAspectRatio
            ? (maxWidth, (int)(maxWidth / contentAspectRatio))
            : ((int)(maxHeight * contentAspectRatio), maxHeight);
    }
}
