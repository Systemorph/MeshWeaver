using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Discovers Roslyn <c>[Generator]</c> source generators from the assemblies a node pulls in via
/// <c>#r "nuget:..."</c>, so generators are modular and travel with the node Source instead of being
/// baked into the low-level <see cref="MeshWeaver.Graph"/> project. A generator referenced from Graph
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
    public static ImmutableArray<ISourceGenerator> Discover(IEnumerable<string> assemblyPaths, ILogger logger) =>
        assemblyPaths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(p => LoadFrom(p, logger))
            .ToImmutableArray();

    private static ImmutableArray<ISourceGenerator> LoadFrom(string path, ILogger logger)
    {
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
