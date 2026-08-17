using MeshWeaver.Graph.Configuration;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Compiler;

/// <summary>
/// Source-generator shaping for dynamic NodeType compiles: which generator assemblies run, and
/// which legacy <c>#r "nuget:"</c> directives are stripped before NuGet resolution. Both change
/// what Roslyn emits without any API change, so they live inside the toolchain identity boundary
/// (#1707).
/// </summary>
public static class GeneratorPipeline
{
    /// <summary>
    /// Paths of source-generator assemblies fed to EVERY dynamic-node compilation, resolved once
    /// per process. 🚨 The platform does NOT ship any — business rules / scopes moved OUT of the
    /// platform (see the comment blocks in <c>MeshWeaver.Graph.csproj</c> and
    /// <c>Memex.Portal.Distributed.csproj</c>): the scope runtime is a shared-source library node
    /// in the <c>MeshWeaver.Plugins/BusinessRules</c> plugin, pulled into a consumer's compilation
    /// via <c>shared=@BusinessRules/Scope/Source</c>, and the plugin carries the
    /// <c>ScopeCodeGenerator</c> SOURCE for the generator-injection seam. So on a deployed image
    /// this list is EMPTY. It only fills when a <c>MeshWeaver.BusinessRules.Generator.dll</c> is
    /// physically present next to the app (a dev/self-host tree that placed one there) — kept as
    /// graceful degradation for such trees, and because the legacy-<c>#r</c> strip keys off it.
    /// </summary>
    internal static readonly IReadOnlyList<string> BuiltInGeneratorPaths = ResolveBuiltInGenerators();

    /// <summary>
    /// NuGet package id (and, with <c>.dll</c>, assembly file name) of the BusinessRules scope
    /// source generator. When a copy is present in the app base (<see cref="BuiltInGeneratorPaths"/>
    /// non-empty), a legacy <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> is redundant and is
    /// filtered out of BOTH the generator list (avoid a double-run → CS0101, see
    /// <see cref="RunSourceGenerators"/>) and the NuGet resolve set (avoid a dead round-trip).
    /// </summary>
    internal const string BuiltInScopeGeneratorId = "MeshWeaver.BusinessRules.Generator";

    private static IReadOnlyList<string> ResolveBuiltInGenerators()
    {
        var path = Path.Combine(AppContext.BaseDirectory, BuiltInScopeGeneratorId + ".dll");
        return File.Exists(path) ? [path] : [];
    }

    /// <summary>
    /// Removes a legacy <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> from the NuGet resolve
    /// set when the generator ships built-in (<paramref name="builtInPresent"/>). The generator is
    /// now part of the platform, so that <c>#r</c> is redundant — and RESOLVING it hard-fails on a
    /// deployed image: after <c>BakeMeshLocalFeed</c> was removed (#395) the mesh-local feed
    /// (<c>dist/packages</c>) is gone, so NuGet throws
    /// <c>"The local source '/app/dist/packages' doesn't exist"</c> and breaks every deployed scope
    /// node still carrying the legacy <c>#r</c> (the prod BalanceSheet failure). Behaviour is
    /// unchanged: the built-in generator still emits the <c>IScope&lt;,&gt;</c> implementations, and
    /// <see cref="RunSourceGenerators"/> already de-dups the generator itself (CS0101). When the
    /// built-in is somehow absent the <c>#r</c> is kept so the generator can still resolve via NuGet.
    /// Other package references are never touched.
    /// </summary>
    internal static void StripBuiltInScopeGeneratorRef(List<NuGetPackageReference> refs, bool builtInPresent)
    {
        if (builtInPresent)
            refs.RemoveAll(r => string.Equals(
                r.Id, BuiltInScopeGeneratorId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs the built-in scope generator plus any OTHER generator a node <c>#r</c>'d over
    /// <paramref name="compilation"/>. A node's own
    /// <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> is filtered OUT — otherwise the same
    /// generator loads from two paths (built-in + baked) and runs twice → duplicate
    /// <c>IScope&lt;,&gt;</c> implementations → CS0101. Legacy nodes that still carry that
    /// <c>#r</c> keep compiling (built-in supersedes it, and the strip above keeps it out of the
    /// NuGet resolve set so it never round-trips).
    /// </summary>
    internal static CSharpCompilation RunSourceGenerators(
        CSharpCompilation compilation, IReadOnlyList<string> generatorAssemblyPaths, ILogger logger, CancellationToken ct)
    {
        IReadOnlyList<string> allPaths = BuiltInGeneratorPaths.Count == 0
            ? generatorAssemblyPaths
            : [.. BuiltInGeneratorPaths,
               .. generatorAssemblyPaths.Where(p => !string.Equals(
                   Path.GetFileName(p), BuiltInScopeGeneratorId + ".dll", StringComparison.OrdinalIgnoreCase))];
        if (allPaths.Count == 0)
            return compilation;
        var generators = SourceGeneratorLoader.Discover(allPaths, logger);
        if (generators.IsDefaultOrEmpty)
            return compilation;
        var driver = CSharpGeneratorDriver.Create(generators);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, ct);
        return (CSharpCompilation)updated;
    }
}
