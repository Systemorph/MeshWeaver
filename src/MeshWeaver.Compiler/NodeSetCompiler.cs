using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.Compiler;

/// <summary>
/// Compiles ONE dynamic NodeType straight out of a <see cref="NodeSet"/> — the BUILD-PROCESS bake
/// of issue #1763. No <c>MeshBuilder</c>, no hub, no import, no activation: sources come from an
/// in-memory node set the caller assembled from a git tree, and the output is a DLL + PDB on disk
/// plus the per-type dependency record (#1719) a consumer validates before adopting.
///
/// <para><b>Every shaping rule is the runtime's own.</b> This type is an ORCHESTRATOR, not a second
/// implementation: source-query expansion is <see cref="CodeQueryResolver"/>, the <c>@@</c>-include
/// walk is <see cref="NodeCompileShaping.ResolveCodeIncludes"/>, the dedup / executable filter /
/// join order are <see cref="NodeCompileShaping.CollectCompileSources"/> and
/// <see cref="NodeCompileShaping.CombineSources"/>, the skeleton is
/// <see cref="DynamicMeshNodeAttributeGenerator"/>, the <c>#r "nuget:"</c> handling is
/// <see cref="NuGetDirectiveParser"/> + <see cref="GeneratorPipeline"/>, and the emit is
/// <see cref="EmitPipeline"/>. The ONLY thing that differs from
/// <c>MeshNodeCompilationService.CompileCore</c> is where the nodes came from — which is precisely
/// the claim the bake-equivalence test has to pin.</para>
///
/// <para><b>Why it lives in this assembly.</b> Which Code nodes a compile consumes, in what order,
/// with which includes folded in and under which assembly name, is part of that compile's GENERATED
/// INPUT — no different from the skeleton generator or the parse options. So it belongs inside the
/// full-MVID toolchain identity boundary (<see cref="FrameworkBuildIdentity.FullMvidAssemblies"/>).
/// A resolver sitting OUTSIDE that boundary could change what a bake consumes without moving the
/// framework identity, and every portal would adopt the changed bytes without a murmur — the
/// consumer's gates compare identities, and the identity would say nothing had changed. That is the
/// same hole <see cref="CodeConventions.SanitizeNodeName"/> was in until #1763 moved it down out of
/// <c>MeshWeaver.Graph</c>: it names the emitted assembly, so it shaped the bytes from outside the
/// boundary that is supposed to cover them.</para>
///
/// <para><b>Synchronous on purpose.</b> This runs in a build process at its console boundary, never
/// on a hub scheduler, and every leaf it touches is pure CPU or a local file write. The one
/// reactive helper it reuses (<see cref="NodeCompileShaping.ResolveCodeIncludes"/>) is driven with
/// an in-memory reader, so its chain completes on subscribe.</para>
/// </summary>
public static class NodeSetCompiler
{
    /// <summary>
    /// The compile inputs one NodeType resolves to — everything that reaches Roslyn, and nothing
    /// that does not. Produced by <see cref="ResolveInputs"/> so the bake-equivalence test can
    /// compare WHAT WOULD BE COMPILED without emitting anything.
    /// </summary>
    /// <param name="NodePath">The NodeType's mesh path.</param>
    /// <param name="AssemblyName">The emitted assembly's name (<c>DynamicNode_{sanitized}</c>).</param>
    /// <param name="MatchedSourcePaths">The Code nodes the source queries matched, after the dedup
    /// and executable filter, in the order they are concatenated.</param>
    /// <param name="ExpandedQueries">Every expanded source/test query that was evaluated.</param>
    /// <param name="GeneratedSource">The full C# text handed to Roslyn (skeleton + user code).</param>
    /// <param name="NuGetReferences">The <c>#r "nuget:"</c> references the sources declare, after
    /// the built-in-generator strip.</param>
    public sealed record CompileInputs(
        string NodePath,
        string AssemblyName,
        ImmutableArray<string> MatchedSourcePaths,
        ImmutableArray<string> ExpandedQueries,
        string GeneratedSource,
        ImmutableArray<NuGetPackageReference> NuGetReferences)
    {
        /// <summary>
        /// The <c>@@</c>-include CLOSURE this resolution consumed — resolved node path → code text,
        /// transitively, collected BY the substitution in <see cref="ResolveInputs"/> rather than by
        /// a second walk (#2948). These are the Code nodes no source query matched, so they are in
        /// <see cref="GeneratedSource"/> and deliberately NOT in
        /// <see cref="MatchedSourcePaths"/>; the bake folds them into
        /// <see cref="NodeTypeSourceFingerprint"/> so a consumer can tell that an included-only
        /// snippet has changed under a prebuilt assembly.
        ///
        /// <para><c>required</c>, and an <c>init</c> member rather than a positional parameter, for
        /// two different reasons. Positional would have changed the primary constructor's ARITY,
        /// which is a binary break between a module and the platform it loads into — there is no
        /// image that can serve a mixed set, so the host aborts at boot in both directions
        /// (<c>scripts/check-record-signatures.py</c>). And <c>required</c> because the failure mode
        /// of forgetting it is SILENT: an empty closure for a type that has includes is not "the
        /// fingerprint minus the includes", it is a value that disagrees with every honest consumer,
        /// i.e. a refused adoption.</para>
        /// </summary>
        public required ImmutableSortedDictionary<string, string> ResolvedIncludes { get; init; }
    }

    /// <summary>One compiled NodeType's artifacts.</summary>
    /// <param name="NodePath">The NodeType's mesh path — the bundle's key.</param>
    /// <param name="DllPath">The emitted assembly on disk.</param>
    /// <param name="PdbPath">The emitted symbols on disk, or null when none were written.</param>
    /// <param name="Inputs">What was compiled (provenance + the equivalence comparison surface).</param>
    /// <param name="Dependencies">The per-type dependency record read off the emitted assembly's
    /// AssemblyRef table (#1719), including the reserved toolchain entry.</param>
    public sealed record CompiledNode(
        string NodePath,
        string DllPath,
        string? PdbPath,
        CompileInputs Inputs,
        ImmutableSortedDictionary<string, string> Dependencies);

    /// <summary>
    /// Resolves everything a compile of <paramref name="typeNode"/> would consume, WITHOUT
    /// compiling. Throws <see cref="SourceDiscoveryUnavailableException"/> when a declared source
    /// query cannot be evaluated offline — the build-process analogue of the runtime's refusal to
    /// compile against an unestablished source set, and for the identical reason: a short set
    /// yields genuine-looking diagnostics about code that is fine, and the resulting bytes are
    /// adopted with no complaint (#1218).
    /// </summary>
    /// <param name="nodes">The node set the sources are resolved from.</param>
    /// <param name="typeNode">The NodeType's own MeshNode (the skeleton is generated from it).</param>
    /// <param name="sources">The type's declared <c>Sources</c> queries, or null for the default.</param>
    /// <param name="tests">The type's declared <c>Tests</c> queries, or null for the default.</param>
    /// <param name="configuration">The type's <c>configuration</c> lambda — C# stored in a JSON
    /// string field, which is why it is invisible to every <c>.cs</c>-shaped tool and must be
    /// threaded explicitly.</param>
    /// <param name="contentCollections">The type's content-collection registrations.</param>
    /// <param name="logger">Diagnostics (include-resolution warnings ride here).</param>
    public static CompileInputs ResolveInputs(
        NodeSet nodes,
        MeshNode typeNode,
        IReadOnlyList<string>? sources,
        IReadOnlyList<string>? tests,
        string? configuration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(typeNode);
        logger ??= NullLogger.Instance;

        var nodePath = typeNode.Path;
        var resolution = nodes.ResolveSources(sources, tests, nodePath);
        if (!resolution.IsEstablished)
            throw new SourceDiscoveryUnavailableException(
                $"Source discovery for '{nodePath}' could not be ESTABLISHED from the tree, so the "
                + $"compile was not attempted: {resolution.UnestablishedReason}. This is a BAKE "
                + "capability failure, not a compile error — nothing is known to be wrong with the "
                + "code. Either express the source query with path:/namespace:/scope:/nodeType: "
                + "only, or teach NodeSetQuery the selector; NEVER let the bake guess, or it ships "
                + "assemblies built from a set the runtime would not have used.");

        // The dedup (OrdinalIgnoreCase), the executable-cell filter and the join order are the
        // toolchain's — the same call MeshNodeCompilationService.CompileCore makes.
        var (codeFiles, matchedPaths) =
            NodeCompileShaping.CollectCompileSources(resolution.Sources, nodePath, logger);

        // @@ includes, resolved against the SAME node set. The runtime reads them through the mesh
        // under System impersonation with a bounded timeout; here the read is a dictionary lookup,
        // so the reactive chain completes on subscribe.
        //
        // 🚨 #2948 — the walk also RECORDS what it pulled in. An include target is a Code node no
        // source query matched, so it is in the bytes and absent from `matchedPaths`; without this
        // record the bake's source fingerprint could not cover it, and a consumer holding an edited
        // snippet would adopt these bytes as VERIFIED. Collected here, inside the substitution
        // itself, so there is no second traversal to disagree with the first.
        var closure = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolvedFiles = new List<CodeConfiguration>(codeFiles.Count);
        foreach (var codeFile in codeFiles)
        {
            var resolvedCode = NodeCompileShaping
                .ResolveCodeIncludes(
                    codeFile.Code!, new HashSet<string>(StringComparer.Ordinal), nodePath,
                    (anchored, authored) => Observable.Return(ReadInclude(nodes, anchored, authored)),
                    logger, closure)
                .Wait();
            resolvedFiles.Add(
                ReferenceEquals(resolvedCode, codeFile.Code)
                    ? codeFile
                    : codeFile with { Code = resolvedCode });
        }

        var combined = NodeCompileShaping.CombineSources(resolvedFiles);
        var rawSource = new DynamicMeshNodeAttributeGenerator()
            .GenerateAttributeSource(typeNode, combined, configuration, contentCollections);
        var (source, extractedRefs) = NuGetDirectiveParser.Extract(rawSource);
        var nugetRefs = extractedRefs.ToList();
        GeneratorPipeline.StripBuiltInScopeGeneratorRef(
            nugetRefs, builtInPresent: GeneratorPipeline.BuiltInGeneratorPaths.Count > 0);

        return new CompileInputs(
            nodePath,
            $"DynamicNode_{CodeConventions.SanitizeNodeName(nodePath)}",
            [.. matchedPaths],
            resolution.ExpandedQueries,
            source,
            [.. nugetRefs])
        {
            ResolvedIncludes = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, closure),
        };
    }

    /// <summary>
    /// Resolves, compiles and EMITS one NodeType into <paramref name="outputDirectory"/>.
    /// A compile error throws <see cref="CompilationException"/> carrying the Roslyn diagnostics,
    /// exactly as the runtime's emit does.
    /// </summary>
    /// <param name="nodes">The node set the sources are resolved from.</param>
    /// <param name="typeNode">The NodeType's own MeshNode.</param>
    /// <param name="sources">The type's declared <c>Sources</c> queries, or null for the default.</param>
    /// <param name="tests">The type's declared <c>Tests</c> queries, or null for the default.</param>
    /// <param name="configuration">The type's <c>configuration</c> lambda source.</param>
    /// <param name="contentCollections">The type's content-collection registrations.</param>
    /// <param name="references">The metadata references to compile against —
    /// <see cref="CompileReferences.Default"/> composed with the deployment's installed modules.</param>
    /// <param name="dependencyIdOf">The surface-id resolver for the dependency record
    /// (<see cref="CompiledDependencies.CreateIdResolver"/> over the PRODUCING environment).</param>
    /// <param name="toolchainId">This process's toolchain id
    /// (<see cref="CompiledDependencies.ComputeToolchainId"/>).</param>
    /// <param name="outputDirectory">Directory the DLL/PDB are written into. Created if absent.</param>
    /// <param name="resolveNuGet">Resolves <c>#r "nuget:"</c> references to assembly paths. Null
    /// REFUSES a source set that declares any — a bake that silently dropped a package reference
    /// would emit an assembly missing the very types the directive exists to supply.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="ct">Cancellation.</param>
    public static CompiledNode Compile(
        NodeSet nodes,
        MeshNode typeNode,
        IReadOnlyList<string>? sources,
        IReadOnlyList<string>? tests,
        string? configuration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections,
        IReadOnlyList<MetadataReference> references,
        Func<string, string?> dependencyIdOf,
        string toolchainId,
        string outputDirectory,
        Func<IReadOnlyList<NuGetPackageReference>, CancellationToken, IReadOnlyList<string>>? resolveNuGet = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(dependencyIdOf);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        logger ??= NullLogger.Instance;

        var inputs = ResolveInputs(
            nodes, typeNode, sources, tests, configuration, contentCollections, logger);

        var allReferences = references;
        IReadOnlyList<string> generatorPaths = [];
        if (inputs.NuGetReferences.Length > 0)
        {
            if (resolveNuGet is null)
                throw new CompilationException(inputs.NodePath,
                    $"'{inputs.NodePath}' declares {inputs.NuGetReferences.Length} `#r \"nuget:…\"` "
                    + $"reference(s) ({string.Join(", ", inputs.NuGetReferences.Select(r => r.Id))}) "
                    + "but this bake has no NuGet resolver configured. Refusing to compile without "
                    + "them: the emitted assembly would be missing exactly the types the directive "
                    + "supplies, and the consumer would adopt it.");
            generatorPaths = resolveNuGet(inputs.NuGetReferences, ct);
            allReferences =
            [
                .. references,
                .. generatorPaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
            ];
        }

        Directory.CreateDirectory(outputDirectory);
        var nodeName = CodeConventions.SanitizeNodeName(inputs.NodePath);
        var compilation = GeneratorPipeline.RunSourceGenerators(
            EmitPipeline.CreateEmitCompilation(
                inputs.GeneratedSource, inputs.AssemblyName, allReferences, parsePath: "", ct),
            generatorPaths, logger, ct);

        var artifact = EmitPipeline.EmitCompilationToDirectory(
            compilation, nodeName, inputs.NodePath, outputDirectory, ct);
        if (!artifact.MatchesFileOnDisk(out var mismatch))
            throw new CompilationException(inputs.NodePath,
                $"the assembly emitted for '{inputs.NodePath}' could not be persisted: {mismatch}");

        var pdbPath = Path.ChangeExtension(artifact.DllPath, ".pdb");
        return new CompiledNode(
            inputs.NodePath,
            artifact.DllPath,
            File.Exists(pdbPath) ? pdbPath : null,
            inputs,
            CompiledDependencies.Compute(
                ReferencedAssemblyNames(artifact.DllPath), dependencyIdOf, toolchainId,
                GeneratedInputDigestOf(inputs, generatorPaths)));
    }

    /// <summary>
    /// The stage-1 CONTENT KEY digest of a resolved compile (#1707 slice 4): the generated text
    /// plus everything else that decides the bytes given that text. Shared by this bake and the
    /// runtime's <c>MeshNodeCompilationService</c> — the two paths differ only in where the nodes
    /// came from, so their keys must agree for identical content, which is what makes a CI bake's
    /// bytes adoptable by a portal.
    ///
    /// <para>The NuGet-resolved assemblies are folded in as generator CANDIDATES: they are what
    /// <see cref="GeneratorPipeline.RunSourceGenerators"/> discovers over, and a change in what a
    /// <c>#r "nuget:"</c> directive resolves to changes the emitted bytes.</para>
    /// </summary>
    internal static string GeneratedInputDigestOf(
        CompileInputs inputs, IReadOnlyList<string> nugetAssemblyPaths) =>
        GeneratedInputIdentity.OfGeneratedInput(
            inputs.AssemblyName,
            inputs.GeneratedSource,
            EmitPipeline.OptionsFingerprint,
            GeneratedInputIdentity.CompilerIdentity,
            GeneratedInputIdentity.AssemblyFileIdentities(
                GeneratorPipeline.EffectiveGeneratorPaths(nugetAssemblyPaths)));

    /// <summary>
    /// The include reader the tree bake hands <see cref="NodeCompileShaping.ResolveCodeIncludes"/>:
    /// the ANCHORED path first, the authored path only as a fallback — the same two-step the mesh
    /// read performs, so a mount-relative <c>@@</c> resolves identically here (an unresolved include
    /// is left VERBATIM in the source and Roslyn then reports on the <c>@@</c> line itself, which is
    /// how 15 NodeTypes parked on memex-cloud 2026-08-12).
    /// </summary>
    private static (MeshNode? Node, string Path) ReadInclude(
        NodeSet nodes, string anchored, string? authored)
    {
        if (nodes.Find(anchored) is { } hit)
            return (hit, anchored);
        if (authored is { Length: > 0 } && nodes.Find(authored) is { } fallback)
            return (fallback, authored);
        return (null, anchored);
    }

    /// <summary>
    /// The emitted assembly's AssemblyRef simple names — the same table
    /// <c>Assembly.GetReferencedAssemblies()</c> reads at runtime, but through a METADATA-ONLY
    /// reader so a bake never loads content bytes into the process (no collectible ALC, no unload
    /// race, no #890 exposure in a build tool).
    /// </summary>
    private static IEnumerable<string?> ReferencedAssemblyNames(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        // Materialized inside the using: the reader owns the mapped file.
        return reader.AssemblyReferences
            .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
            .ToList();
    }
}
