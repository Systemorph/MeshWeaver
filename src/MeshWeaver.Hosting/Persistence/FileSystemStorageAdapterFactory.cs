using System.Text.Json;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Factory for creating FileSystemStorageAdapter instances from configuration.
/// </summary>
public class FileSystemStorageAdapterFactory : IStorageAdapterFactory
{
    /// <summary>
    /// The storage type discriminator this factory handles (<c>"FileSystem"</c>).
    /// </summary>
    public const string StorageType = "FileSystem";

    /// <summary>
    /// Creates a modifier function that enables WriteIndented for formatted JSON output.
    /// </summary>
    public static Func<JsonSerializerOptions, JsonSerializerOptions> FormattedJsonModifier =>
        options => new JsonSerializerOptions(options) { WriteIndented = true };

    /// <inheritdoc />
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

        // A real logger so the change feed can surface a subscriber that throws during fan-out —
        // a null logger restores exactly the silent-failure mode IsolatedChangeFeed exists to kill.
        var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<FileSystemStorageAdapter>();
        // 🚨 The mesh-scoped pool registry, resolved LOUDLY — never a silent IoPool.Unbounded
        // fallback. This factory is the path every config-declared FileSystem data source takes
        // (the FutuRe sample among them), and dropping the registry here is exactly how all of
        // that mesh's file I/O ended up on the ledgerless unbounded pool, invisible to the
        // teardown drain (the issue #613 exit=139 straggler source).
        var ioPoolRegistry = serviceProvider.GetRequiredIoPoolRegistry();
        // Module-contributed parsers (the AI module's agent parser) — resolved here because this
        // factory is the DI-aware construction site. Without them an `.md` carrying
        // `nodeType: Agent` parses as plain Markdown on every file-system-backed mesh, silently.
        return new FileSystemStorageAdapter(
            basePath, ioPoolRegistry, writeOptionsModifier, logger: logger,
            contributedParsers: serviceProvider.GetServices<Parsers.IFileFormatParser>());
    }
}
