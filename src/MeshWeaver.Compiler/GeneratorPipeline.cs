using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Compiler;

/// <summary>
/// Source-generator shaping for dynamic NodeType compiles: which generators run — the ones a node
/// <c>#r</c>'d and the ones a NodeType declared (<see cref="DeclaredGenerators"/>) — and how they
/// enter the content key. Both change what Roslyn emits without any API change, so they live
/// inside the toolchain identity boundary (#1707). The platform itself ships no generator.
/// </summary>
public static class GeneratorPipeline
{
    /// <summary>
    /// The assemblies the stage-1 CONTENT KEY hashes for the generator half: the <c>#r</c>-resolved
    /// candidates plus, for every candidate whose package ships a
    /// RID-specific runtime group, the runtime asset discovery actually LOADS in its place
    /// (<see cref="SourceGeneratorLoader.RuntimeAssetOf"/>) — two runtime helpers behind one compile
    /// stub can make a generator emit different source, so they must not share a key. Identical to
    /// the candidates for every package without a <c>runtimes/</c> folder. Shared by the bake, the
    /// runtime compile and the re-evaluation lane. (A DECLARED generator is not here: its identity
    /// rides in the generated text, <see cref="DeclaredGenerators.Stamp"/>.)
    /// </summary>
    internal static IReadOnlyList<string> DigestedGeneratorPaths(IReadOnlyList<string> generatorAssemblyPaths)
        => DigestedGeneratorPaths(generatorAssemblyPaths, CancellationToken.None);

    /// <summary>
    /// The digested paths from a pooled leaf: <paramref name="cancellationToken"/> is checked before
    /// each candidate's runtime-asset discovery and between the entries of the package walk that
    /// discovery makes, so a drain never waits out the whole file-system pre-pass (review on #7121).
    /// </summary>
    internal static IReadOnlyList<string> DigestedGeneratorPaths(
        IReadOnlyList<string> generatorAssemblyPaths, CancellationToken cancellationToken)
    {
        var effective = generatorAssemblyPaths;
        List<string>? runtime = null;
        foreach (var path in effective)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SourceGeneratorLoader.RuntimeAssetObservingToken(path, cancellationToken) is { } asset
                && !string.Equals(asset, path, StringComparison.OrdinalIgnoreCase))
                (runtime ??= []).Add(asset);
        }
        return runtime is null ? [.. effective] : [.. effective, .. runtime];
    }

    /// <summary>
    /// Runs every generator a node <c>#r</c>'d over <paramref name="compilation"/>.
    ///
    /// <para>🚨 The generators load through <paramref name="ledger"/> for
    /// <paramref name="loadedFor"/>: a different build of a generator this process already loaded for
    /// another NodeType throws <see cref="SourceGeneratorBuildConflictException"/> and fails this
    /// compile, naming both (#7444).</para>
    /// </summary>
    internal static CSharpCompilation RunSourceGenerators(
        CSharpCompilation compilation, IReadOnlyList<string> generatorAssemblyPaths,
        SourceGeneratorLedger ledger, string loadedFor, ILogger logger, CancellationToken ct)
        => RunSourceGeneratorsWithNodeOutput(compilation, generatorAssemblyPaths, [], ledger, loadedFor, logger, ct, out _);

    /// <summary>
    /// Runs every generator a node <c>#r</c>'d AND every generator a NodeType DECLARED for this
    /// compile (<paramref name="declaredGenerators"/>, policy <c>declared-source-generators</c>) over
    /// <paramref name="compilation"/>, and hands back the trees they added. Every one of them runs
    /// code a node or a package chose — the platform contributes no generator of its own — so what
    /// they emit is the author's code for the in-mesh impersonation check
    /// (<c>InMeshImpersonationReferences</c>), which must scan it AFTER generation (#6619 review).
    /// </summary>
    internal static CSharpCompilation RunSourceGeneratorsWithNodeOutput(
        CSharpCompilation compilation, IReadOnlyList<string> generatorAssemblyPaths,
        IReadOnlyList<DeclaredGenerator> declaredGenerators,
        SourceGeneratorLedger ledger, string loadedFor, ILogger logger,
        CancellationToken ct, out ImmutableArray<SyntaxTree> nodeGeneratedTrees)
    {
        nodeGeneratedTrees = [];
        if (generatorAssemblyPaths.Count == 0 && declaredGenerators.Count == 0)
            return compilation;
        var generators = SourceGeneratorLoader.Discover(generatorAssemblyPaths, ledger, loadedFor, logger);
        foreach (var declared in declaredGenerators.OrderBy(g => g.OwnerPath, StringComparer.Ordinal))
            generators = generators.AddRange(DeclaredGenerators.Load(declared, ledger, loadedFor, logger, ct));
        if (generators.IsDefaultOrEmpty)
            return compilation;
        var driver = CSharpGeneratorDriver.Create(generators);
        var ran = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, ct);
        nodeGeneratedTrees = [.. ran.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources.Select(g => g.SyntaxTree))];
        return (CSharpCompilation)updated;
    }
}
