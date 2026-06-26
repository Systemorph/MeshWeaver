namespace MeshWeaver.Layout.Composition;

/// <summary>
/// Defines URL patterns for light and dark mode thumbnails using lambda expressions.
/// </summary>
public record ThumbnailPattern(Func<string, string> LightUrlFactory, Func<string, string> DarkUrlFactory)
{
    /// <summary>Returns the light-mode thumbnail URL for the given <paramref name="areaName"/>.</summary>
    /// <param name="areaName">The name of the layout area whose thumbnail URL is requested.</param>
    /// <returns>The light-mode thumbnail URL string.</returns>
    public string GetLightUrl(string areaName) => LightUrlFactory(areaName);
    /// <summary>Returns the dark-mode thumbnail URL for the given <paramref name="areaName"/>.</summary>
    /// <param name="areaName">The name of the layout area whose thumbnail URL is requested.</param>
    /// <returns>The dark-mode thumbnail URL string.</returns>
    public string GetDarkUrl(string areaName) => DarkUrlFactory(areaName);

    /// <summary>
    /// Creates a pattern from a base path using the default naming convention:
    /// {basePath}/{area}.png and {basePath}/{area}-dark.png
    /// </summary>
    public static ThumbnailPattern FromBasePath(string basePath)
    {
        var normalizedBase = basePath.TrimEnd('/');
        return new ThumbnailPattern(
            area => $"{normalizedBase}/{area}.png",
            area => $"{normalizedBase}/{area}-dark.png"
        );
    }
}
