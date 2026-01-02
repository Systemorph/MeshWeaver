using MeshWeaver.Domain;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Factory for creating FileSystemStorageAdapter instances from configuration.
/// </summary>
public class FileSystemStorageAdapterFactory : IStorageAdapterFactory
{
    public const string StorageType = "FileSystem";

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

        return new FileSystemStorageAdapter(
            basePath,
            typeRegistryFactory: () => serviceProvider.GetService<ITypeRegistry>());
    }
}
