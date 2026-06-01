using System.Text.Json;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Factory for creating FileSystemStorageAdapter instances from configuration.
/// </summary>
public class FileSystemStorageAdapterFactory : IStorageAdapterFactory
{
    public const string StorageType = "FileSystem";

    /// <summary>
    /// Creates a modifier function that enables WriteIndented for formatted JSON output.
    /// </summary>
    public static Func<JsonSerializerOptions, JsonSerializerOptions> FormattedJsonModifier =>
        options => new JsonSerializerOptions(options) { WriteIndented = true };

    public IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider)
    {
        var basePath = config.BasePath
            ?? throw new InvalidOperationException(
                "Graph:Storage:BasePath is required for FileSystem storage. " +
                "Configure it in appsettings.json.");

        // Resolve to absolute path if relative
        if (!Path.IsPathRooted(basePath))
        {
            basePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), basePath));
        }

        // Check for FormatJson setting to enable formatted output
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null;
        if (config.Settings?.TryGetValue("FormatJson", out var formatValue) == true
            && bool.TryParse(formatValue, out var format) && format)
        {
            writeOptionsModifier = FormattedJsonModifier;
        }

        return new FileSystemStorageAdapter(basePath, writeOptionsModifier);
    }
}
