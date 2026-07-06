namespace MeshWeaver.Mesh.Services;

/// <summary>
/// A generated raster image — the bytes plus their MIME type (typically <c>image/png</c>).
/// </summary>
/// <param name="Data">The encoded image bytes.</param>
/// <param name="ContentType">The MIME type, e.g. <c>image/png</c>.</param>
public sealed record GeneratedImage(byte[] Data, string ContentType);

/// <summary>
/// Generates a <b>raster</b> image (PNG) from a text prompt using a configured
/// image-generation model — the companion to <see cref="IIconGenerator"/> (which produces
/// vector SVG via an LLM). Implementations route the HTTP round through the I/O pool and
/// select the backend (Azure OpenAI Images, OpenAI, or a local Stable-Diffusion endpoint)
/// from the chosen model's provider.
/// </summary>
public interface IImageGenerator
{
    /// <summary>
    /// Produces a raster image for <paramref name="prompt"/>. Emits exactly once on success;
    /// OnError on failure (no image model configured, provider unsupported, network error,
    /// cancellation).
    /// </summary>
    /// <param name="prompt">The text description of the image to generate.</param>
    /// <param name="size">Optional <c>WxH</c> hint (e.g. <c>1024x1024</c>); backend default when null.</param>
    /// <param name="modelId">Optional explicit image-model id; the first image-capable model when null.</param>
    /// <param name="ct">Cancels the underlying HTTP round.</param>
    IObservable<GeneratedImage> GenerateImageAsync(
        string prompt, string? size = null, string? modelId = null, CancellationToken ct = default);
}
