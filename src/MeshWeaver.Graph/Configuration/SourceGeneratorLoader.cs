using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Discovers Roslyn <c>[Generator]</c> source generators from the assemblies a node
/// pulls in via <c>#r "nuget:..."</c>, so generators are modular and travel with the
/// node Source instead of being baked into this low-level project. The canonical
/// example is <c>MeshWeaver.BusinessRules.Generator</c>'s <c>ScopeCodeGenerator</c>:
/// a node declares <c>IScope&lt;,&gt;</c> and adds
/// <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c>, and the compiler discovers +
/// runs it here.
/// </summary>
internal static class SourceGeneratorLoader
{
    /// <summary>
    /// Process-global, path-keyed memo. A generator assembly is a build <i>tool</i>: it is
    /// loaded once per absolute path for the process lifetime (mirroring
    /// <c>KernelScriptReferences.Materialized</c>), never tied to a per-node collectible
    /// <c>NodeAssemblyLoadContext</c> (which it would pin and leak). The same handful of
    /// <c>#r</c> tool assemblies are shared across every node compile, so one load amortises.
    /// Allowlisted in <c>NoStaticCollectionsTest</c> as a MEMO.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ImmutableArray<ISourceGenerator>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the source generators found across <paramref name="assemblyPaths"/> (the
    /// node's <c>#r</c>-resolved assemblies). Non-generator assemblies contribute nothing.
    /// </summary>
    public static ImmutableArray<ISourceGenerator> Discover(
        IEnumerable<string> assemblyPaths, ILogger logger) =>
        assemblyPaths
            .Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(p => Cache.GetOrAdd(p, path => LoadFrom(path, logger)))
            .ToImmutableArray();

    private static ImmutableArray<ISourceGenerator> LoadFrom(string path, ILogger logger)
    {
        // A generator is a process-wide tool keyed by path → load into the default
        // (non-collectible) context, NOT the per-node collectible ALC.
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
                // A generator built against a different Microsoft.CodeAnalysis identity
                // splits type identity and throws here. Skip it loudly rather than abort.
                logger.LogWarning(ex,
                    "SourceGeneratorLoader: failed to instantiate generator {Generator} from {Path} " +
                    "(likely a Microsoft.CodeAnalysis version mismatch); skipping", type.FullName, path);
            }
        }

        if (builder.Count > 0)
            logger.LogInformation("SourceGeneratorLoader: discovered {Count} generator(s) in {Path}",
                builder.Count, path);
        return builder.ToImmutable();
    }
}
