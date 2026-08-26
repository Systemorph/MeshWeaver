using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Compiler;

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
    /// <c>TryAddSingleton</c> keeps the first registration.
    /// </summary>
    public static IServiceCollection AddFileSystemAssemblyStore(
        this IServiceCollection services, string rootDirectory)
    {
        services.TryAddSingleton<IAssemblyStore>(sp => new FileSystemAssemblyStore(
            rootDirectory,
            sp.GetRequiredService<ILogger<FileSystemAssemblyStore>>(),
            KeepVersionsPerType(sp.GetService<IConfiguration>())));
        return services;
    }

    /// <summary>
    /// Config key overriding how many of a type's most recent versions the store keeps per framework
    /// generation (<see cref="FileSystemAssemblyStore.KeepVersionsPerType"/>). Sits in the same
    /// <c>AssemblyCache:Retention:*</c> family as the generation sweep's knobs so an operator has one
    /// place to look — but unlike the sweep's <c>Delete</c>, this one is ARMED by default: within a
    /// generation a wrong answer costs a recompile, not a <c>BadImageFormatException</c>.
    /// </summary>
    public const string KeepVersionsPerTypeConfigKey = "AssemblyCache:Retention:KeepVersionsPerType";

    /// <summary>
    /// The configured per-type version budget, or
    /// <see cref="FileSystemAssemblyStore.DefaultKeepVersionsPerType"/> when unset or malformed — a
    /// typo in a knob must never shrink the budget below what a mixed-build window needs.
    /// </summary>
    public static int KeepVersionsPerType(IConfiguration? configuration) =>
        int.TryParse(configuration?[KeepVersionsPerTypeConfigKey], out var keep) && keep >= 1
            ? keep
            : FileSystemAssemblyStore.DefaultKeepVersionsPerType;
}
