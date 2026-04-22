using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// DI helpers for registering <see cref="IAssemblyStore"/> implementations. Registration
/// is additive with <c>TryAddSingleton</c>: the first registration wins, so hosts that
/// prefer a blob-backed store just register it before calling <see cref="AddFileSystemAssemblyStore"/>.
/// Nothing registers <see cref="NullAssemblyStore"/> by default — callers that never
/// register a store simply get the current "compile every time" behaviour.
/// </summary>
public static class AssemblyStoreExtensions
{
    /// <summary>
    /// Register a <see cref="FileSystemAssemblyStore"/> rooted at <paramref name="rootDirectory"/>.
    /// Intended for the monolith portal and tests. Safe to call multiple times —
    /// <see cref="TryAddSingleton"/> keeps the first registration.
    /// </summary>
    public static IServiceCollection AddFileSystemAssemblyStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.TryAddSingleton<IAssemblyStore>(sp => new FileSystemAssemblyStore(
            rootDirectory,
            sp.GetRequiredService<ILogger<FileSystemAssemblyStore>>()));
        return services;
    }
}
