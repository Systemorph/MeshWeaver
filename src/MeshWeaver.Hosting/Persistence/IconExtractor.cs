using System.Text.RegularExpressions;
using System.Web;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Extracts inline data URI icons and saves them as separate files.
/// </summary>
public partial class IconExtractor
{
    private readonly string _contentBasePath;
    private readonly string _urlPrefix;

    /// <summary>
    /// Creates a new IconExtractor.
    /// </summary>
    /// <param name="contentBasePath">Base directory for storing extracted icons (e.g., samples/Graph/content).</param>
    /// <param name="urlPrefix">URL prefix for icon references (e.g., /static/storage/content).</param>
    public IconExtractor(string contentBasePath, string urlPrefix = "/static/storage/content")
    {
        _contentBasePath = contentBasePath;
        _urlPrefix = urlPrefix.TrimEnd('/');
    }

    // Regex to match SVG data URIs
    [GeneratedRegex(@"^data:image/svg\+xml[;,]")]
    private static partial Regex DataUriPrefixRegex();

    /// <summary>
    /// Extracts an inline data URI icon and saves it to a file.
    /// </summary>
    /// <param name="iconData">The icon data (may be a data URI or already a path).</param>
    /// <param name="nodePath">The node path used to determine the output directory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new icon path reference, or the original value if not a data URI.</returns>
    public async Task<string?> ExtractAndSaveIconAsync(
        string? iconData,
        string nodePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(iconData))
            return iconData;

        // Check if it's a data URI
        if (!DataUriPrefixRegex().IsMatch(iconData))
            return iconData; // Return as-is if not a data URI

        try
        {
            var svgContent = DecodeDataUri(iconData);
            if (string.IsNullOrEmpty(svgContent))
                return iconData;

            // Determine output path
            var normalizedPath = nodePath.Trim('/').Replace('\\', '/');
            var iconRelativePath = $"{normalizedPath}/icon.svg";
            var iconFilePath = Path.Combine(
                _contentBasePath,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar),
                "icon.svg");

            // Create directory if needed
            var directory = Path.GetDirectoryName(iconFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write the SVG file
            await File.WriteAllTextAsync(iconFilePath, svgContent, ct);

            // Return the URL reference
            return $"{_urlPrefix}/{iconRelativePath}";
        }
        catch
        {
            // If extraction fails, return original value
            return iconData;
        }
    }

    /// <summary>
    /// Decodes a data URI to get the raw content.
    /// Handles both base64 and URL-encoded formats.
    /// </summary>
    private static string? DecodeDataUri(string dataUri)
    {
        // Format: data:image/svg+xml;base64,{base64data}
        // or: data:image/svg+xml,{urlencoded-svg}
        // or: data:image/svg+xml;charset=utf-8,{urlencoded-svg}

        var commaIndex = dataUri.IndexOf(',');
        if (commaIndex < 0)
            return null;

        var metadata = dataUri[..commaIndex].ToLowerInvariant();
        var data = dataUri[(commaIndex + 1)..];

        if (metadata.Contains("base64"))
        {
            // Base64 encoded
            try
            {
                var bytes = Convert.FromBase64String(data);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
        else
        {
            // URL encoded
            return HttpUtility.UrlDecode(data);
        }
    }

    /// <summary>
    /// Checks if a string is a data URI that should be extracted.
    /// </summary>
    public static bool IsDataUri(string? value)
    {
        return !string.IsNullOrEmpty(value) && DataUriPrefixRegex().IsMatch(value);
    }
}
