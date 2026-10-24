using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Compiler;

/// <summary>
/// Discovers Roslyn <c>[Generator]</c> source generators from the assemblies a node pulls in via
/// <c>#r "nuget:..."</c>, so generators are modular and travel with the node Source instead of being
/// baked into the low-level <c>MeshWeaver.Graph</c> project. A generator referenced from Graph
/// is a Roslyn analyzer that propagates to every downstream project and bloats every build
/// (project_graph_generator_build_bloat); instead the compile pipeline injects
/// <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> for nodes that declare an <c>IScope&lt;,&gt;</c>,
/// the resolver fetches it from the mesh-local feed, and its <c>ScopeCodeGenerator</c> is discovered +
/// run here — emitting the concrete <c>IScope&lt;TIdentity,TState&gt;</c> implementations.
/// </summary>
internal static class SourceGeneratorLoader
{
    /// <summary>
    /// Returns the source generators found across <paramref name="assemblyPaths"/> (the node's
    /// <c>#r</c>-resolved assemblies — a SMALL, node-specific set, never the full framework
    /// reference list). Non-generator assemblies contribute nothing.
    /// </summary>
    public static ImmutableArray<ISourceGenerator> Discover(IEnumerable<string> assemblyPaths, ILogger logger)
    {
        var paths = assemblyPaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            byName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        var preloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<ISourceGenerator>();
        foreach (var path in paths.Where(CanDefineGenerators))
        {
            PreloadDependencies(path, byName, preloaded, logger);
            builder.AddRange(LoadFrom(path, logger));
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Loads, dependencies first, the assemblies among the candidates that a GENERATOR assembly
    /// references (transitively) and the host does not ship — e.g. a helper package a generator's
    /// type derives from, which lives in another package directory the default context cannot probe.
    /// Only a generator's own closure is loaded; an ordinary library of the <c>#r</c> closure (the
    /// SqlClient stub) never is.
    /// </summary>
    private static void PreloadDependencies(
        string path, IReadOnlyDictionary<string, string> byName, HashSet<string> preloaded, ILogger logger)
    {
        foreach (var reference in ReferencedNames(path))
        {
            if (!byName.TryGetValue(reference, out var dependency) || !preloaded.Add(reference))
                continue;
            if (MeshWeaver.NuGet.PackageAssemblyProbe.HostIdentityOf(reference) is not null)
                continue; // the host binds it
            // The candidates are COMPILE assets; what loads is the package's RUNTIME asset — a
            // RID-specific library's lib/ copy is a stub that, once in the default context, every
            // NodeType context would unify with as the host's copy. Its dependencies are read from
            // the RUNTIME image too: the stub may not reference what the implementation needs.
            if (RuntimeAssetOf(dependency) is not { } runtimeAsset)
                continue;
            PreloadDependencies(runtimeAsset, byName, preloaded, logger);
            try
            {
                Assembly.LoadFrom(runtimeAsset);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
            {
                logger.LogDebug(ex, "SourceGeneratorLoader: could not preload generator dependency {Path}", dependency);
            }
        }
    }

    /// <summary>
    /// The runtime asset standing for the compile asset at <paramref name="compilePath"/>
    /// (<c>{package}/lib/{tfm}/{name}.dll</c>): the package's <c>runtimes/{rid}/lib/{tfm}/</c> copy for
    /// the running RID when it ships a runtime group, else the compile asset itself; <c>null</c> when
    /// the runtime group for this RID does not carry the assembly (nothing to load here).
    /// </summary>
    internal static string? RuntimeAssetOf(string compilePath)
        => RuntimeAssetObservingToken(compilePath, CancellationToken.None);

    /// <summary>
    /// <see cref="RuntimeAssetOf"/> from a pooled leaf: the package's recursive file listing is one
    /// round trip per entry, so <paramref name="cancellationToken"/> is checked before each one is
    /// handed to the selector. A distinct name, not an overload, so the bare <c>cref</c>s to
    /// <see cref="RuntimeAssetOf"/> stay unambiguous (CS0419).
    /// </summary>
    internal static string? RuntimeAssetObservingToken(string compilePath, CancellationToken cancellationToken)
    {
        var tfmDir = Path.GetDirectoryName(compilePath);
        var libDir = tfmDir is null ? null : Path.GetDirectoryName(tfmDir);
        if (libDir is null || !string.Equals(Path.GetFileName(libDir), "lib", StringComparison.OrdinalIgnoreCase))
            return compilePath;
        var packageRoot = Path.GetDirectoryName(libDir);
        if (packageRoot is null || !Directory.Exists(Path.Combine(packageRoot, "runtimes")))
            return compilePath;
        cancellationToken.ThrowIfCancellationRequested();
        var files = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(f =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Path.GetRelativePath(packageRoot, f);
            });
        var assets = MeshWeaver.NuGet.RuntimeAssetSelector.Select(
            packageRoot, files, global::NuGet.Frameworks.NuGetFramework.Parse("net10.0"),
            MeshWeaver.NuGet.RuntimeAssetSelector.RidFallbackChain());
        var fileName = Path.GetFileName(compilePath);
        return assets.Runtime.FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ReferencedNames(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!pe.HasMetadata) return [];
            var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            return [.. reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))];
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// True when the assembly at <paramref name="path"/> can define a generator at all: it
    /// references <c>Microsoft.CodeAnalysis</c>, where <see cref="GeneratorAttribute"/>,
    /// <see cref="ISourceGenerator"/> and <see cref="IIncrementalGenerator"/> live. Read from the
    /// METADATA, without loading anything.
    ///
    /// <para>🚨 Why it must not load to find out: discovery loads into the DEFAULT context, and a
    /// <c>#r</c> closure is mostly ordinary libraries. Loading Microsoft.Data.SqlClient's
    /// <c>lib/</c> COMPILE asset there — the "not supported on this platform" stub — made it the
    /// HOST's copy as far as the NodeType load context could tell
    /// (<c>PackageAssemblyProbe.TryUnifyWithHost</c> unifies with what the default context
    /// holds), so the NodeType bound the stub instead of the package's
    /// <c>runtimes/&lt;rid&gt;/lib/</c> asset and threw PlatformNotSupportedException at its first
    /// <c>new SqlConnection</c>. A non-generator assembly is never loaded here now.</para>
    /// </summary>
    internal static bool CanDefineGenerators(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!pe.HasMetadata) return false;
            var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in reader.AssemblyReferences)
            {
                if (reader.StringComparer.Equals(reader.GetAssemblyReference(handle).Name, "Microsoft.CodeAnalysis"))
                    return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            return false;
        }
    }

    private static ImmutableArray<ISourceGenerator> LoadFrom(string path, ILogger logger)
    {
        if (!CanDefineGenerators(path))
            return ImmutableArray<ISourceGenerator>.Empty;

        // A generator is a process-wide build TOOL loaded into the default (non-collectible)
        // context — never the per-node collectible ALC (which it would pin and leak).
        // Assembly.LoadFrom is idempotent per path (the runtime returns the already-loaded
        // assembly), so no memo cache is needed.
        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(path);
        }
        catch (Exception ex)
        {
            // Unloadable #r assembly — not a usable generator. Surface, don't fail the compile.
            logger.LogDebug(ex, "SourceGeneratorLoader: could not load {Path} for generator discovery", path);
            return ImmutableArray<ISourceGenerator>.Empty;
        }

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types; // salvage the types that did load
        }

        var builder = ImmutableArray.CreateBuilder<ISourceGenerator>();
        foreach (var type in types)
        {
            if (type is null || type.IsAbstract) continue;
            if (type.GetCustomAttributes(typeof(GeneratorAttribute), inherit: false).Length == 0) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            try
            {
                switch (Activator.CreateInstance(type))
                {
                    case IIncrementalGenerator incremental:
                        builder.Add(incremental.AsSourceGenerator());
                        break;
                    case ISourceGenerator source:
                        builder.Add(source);
                        break;
                }
            }
            catch (Exception ex)
            {
                // A generator built against a different Microsoft.CodeAnalysis identity splits
                // type identity and throws here. Skip it loudly rather than abort the compile.
                logger.LogWarning(ex,
                    "SourceGeneratorLoader: failed to instantiate generator {Generator} from {Path} " +
                    "(likely a Microsoft.CodeAnalysis version mismatch); skipping", type.FullName, path);
            }
        }
        return builder.ToImmutable();
    }
}
