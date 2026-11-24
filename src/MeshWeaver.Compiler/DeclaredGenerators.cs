using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Compiler;

/// <summary>One source file of a declared generator: its Code node path and its text.</summary>
/// <param name="Path">The Code node's mesh path.</param>
/// <param name="Code">The C# text.</param>
public sealed record DeclaredGeneratorFile(string Path, string Code);

/// <summary>
/// A Roslyn source generator a NodeType DECLARES as source (<c>NodeTypeDefinition.Generators</c>,
/// policy <c>declared-source-generators</c>) and that a compile will run.
/// </summary>
/// <param name="OwnerPath">The NodeType that declares it.</param>
/// <param name="Files">Its source files, in declaration order.</param>
public sealed record DeclaredGenerator(string OwnerPath, ImmutableArray<DeclaredGeneratorFile> Files)
{
    /// <summary>
    /// SHA-256 (hex) over the generator's file paths and normalised texts — what the generator IS,
    /// independent of when or where it was read. Stamped into the generated compile input, so it is
    /// part of the content key of every compile that runs this generator
    /// (<see cref="DeclaredGenerators.Stamp"/>); the compiler's identity is already part of that key.
    /// </summary>
    public string SourceHash { get; } = DeclaredGenerators.HashOf(OwnerPath, Files);

    /// <summary>The cache key of the built generator: the declaring NodeType and the source hash.</summary>
    public string Key => $"{OwnerPath} sha256:{SourceHash}";
}

/// <summary>
/// 🚨 <b>Declared source generators</b> (policy <c>declared-source-generators</c>,
/// <c>Doc/Architecture/NodeTypeCompilation</c> → "Declared source generators"). A package ships a
/// Roslyn source generator as SOURCE on one of its NodeTypes (<c>generators</c> in the type's
/// definition — paths relative to that type). The mesh compiles those files into an analyzer
/// assembly, loads it into a COLLECTIBLE context of its own, and runs it over:
/// <list type="bullet">
///   <item>the declaring type's own compile, and</item>
///   <item>every compile whose resolved source set reaches into the declaring type's
///   <c>Source/</c> or <c>Test/</c> subtree — a <c>shared=@{type}/Source</c> consumer.</item>
/// </list>
/// Nothing else gets it: a compile that pulls none of the declaring type's sources is untouched,
/// so a package cannot change what an unrelated NodeType compiles to.
///
/// <para><b>One rule for every producer.</b> The runtime compile
/// (<c>MeshNodeCompilationService</c>), the bake and <c>mw-plugin-test compile</c>/<c>build</c>
/// (<see cref="NodeSetCompiler"/>) all decide WHICH generators run through
/// <see cref="CandidateOwners"/> and <see cref="FilePath"/>, stamp them with <see cref="Stamp"/>,
/// and run them through <see cref="Load"/> — so the same sources produce the same bytes under the
/// same content key on a portal and in CI.</para>
///
/// <para><b>Why a context of its own, not the default one.</b> <c>#r</c>-resolved generators load
/// into the default context, where one process holds one build per assembly name, and a second
/// build is refused (#7444). A declared generator changes whenever its package does, so it would
/// hit that refusal on the first package update without a restart. Each build (owner + source
/// hash) therefore gets its own collectible <see cref="AssemblyLoadContext"/>, which resolves
/// everything it references — Roslyn above all — from the default context, so the generator's
/// <see cref="ISourceGenerator"/> is the host compiler's own type. The built generators are held
/// by the <see cref="SourceGeneratorLedger"/> of whoever owns the compiles, never statically.</para>
/// </summary>
public static class DeclaredGenerators
{
    /// <summary>The <c>AssemblyMetadata</c> key that records a declared generator in a build.</summary>
    internal const string MetadataKey = "MeshWeaver.DeclaredGenerator";

    /// <summary>
    /// The NodeTypes that may contribute generators to a compile of <paramref name="selfPath"/>:
    /// the type itself, plus the owner of every resolved source path that lies in another type's
    /// <c>Source/</c> or <c>Test/</c> subtree (<see cref="CodeConventions.OwnerOfCodePath"/>).
    /// Ordinal-sorted, so every producer visits them in the same order.
    /// </summary>
    /// <param name="selfPath">The NodeType being compiled.</param>
    /// <param name="sourcePaths">The Code node paths the compile's source queries matched.</param>
    public static ImmutableArray<string> CandidateOwners(string selfPath, IEnumerable<string> sourcePaths)
    {
        var owners = new SortedSet<string>(StringComparer.Ordinal) { selfPath };
        foreach (var path in sourcePaths)
        {
            if (CodeConventions.OwnerOfCodePath(path) is { Length: > 0 } owner
                && !owner.Equals(selfPath, StringComparison.OrdinalIgnoreCase))
                owners.Add(owner);
        }
        return [.. owners];
    }

    /// <summary>
    /// The mesh path of one <c>generators</c> entry of <paramref name="ownerPath"/>. An entry is
    /// relative to the declaring type, so a package can only point at its own content: an absolute
    /// path, an <c>@</c> reference or a <c>..</c> segment is refused.
    /// </summary>
    /// <param name="ownerPath">The declaring NodeType.</param>
    /// <param name="entry">The declared entry, e.g. <c>GeneratorSource/ScopeCodeGenerator</c>.</param>
    /// <exception cref="CompilationException">The entry is not a relative path below the type.</exception>
    public static string FilePath(string ownerPath, string entry)
    {
        var trimmed = (entry ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('/') || trimmed.StartsWith('@')
            || trimmed.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new CompilationException(ownerPath,
                $"'{ownerPath}' declares the generator source '{entry}', which is not a path relative to "
                + "the NodeType. A `generators` entry names a Code node BELOW the declaring type, e.g. "
                + "\"GeneratorSource/MyGenerator\".");
        return $"{ownerPath}/{trimmed}";
    }

    /// <summary>
    /// Records <paramref name="generators"/> in the generated compile input, one
    /// <c>[assembly: AssemblyMetadata("MeshWeaver.DeclaredGenerator", "{owner} sha256:{hash}")]</c>
    /// per generator, ahead of the skeleton's first attribute (the same anchor
    /// <c>NuGetBuildRecord</c> uses). Being in the TEXT, it is in every content key computed over
    /// that text — the runtime's, the bake's, the cache's and the re-evaluation lane's — and it is
    /// readable off the emitted bytes. No generators: the text is returned unchanged, so every
    /// compile that runs none keeps its key.
    /// </summary>
    /// <param name="generatedSource">The generated compile input.</param>
    /// <param name="generators">The declared generators the compile runs.</param>
    public static string Stamp(string generatedSource, IReadOnlyList<DeclaredGenerator> generators)
    {
        if (generators.Count == 0)
            return generatedSource;

        var anchor = generatedSource.IndexOf(NuGetBuildRecord.SkeletonAttributePrefix, StringComparison.Ordinal);
        if (anchor < 0 || (anchor > 0 && generatedSource[anchor - 1] != '\n'))
            throw new InvalidOperationException(
                $"The compile runs {generators.Count} declared source generator(s) but the generated source carries "
                + $"no line starting '{NuGetBuildRecord.SkeletonAttributePrefix}' to record them ahead of; the "
                + "skeleton generator's output changed shape.");

        var stamp = new StringBuilder();
        foreach (var generator in generators.OrderBy(g => g.OwnerPath, StringComparer.Ordinal))
            stamp.Append("[assembly: System.Reflection.AssemblyMetadata(")
                .Append(SymbolDisplay.FormatLiteral(MetadataKey, quote: true))
                .Append(", ")
                .Append(SymbolDisplay.FormatLiteral(generator.Key, quote: true))
                .Append(")]\n");
        return generatedSource.Insert(anchor, stamp.ToString());
    }

    /// <summary>The source hash of a declared generator — see <see cref="DeclaredGenerator.SourceHash"/>.</summary>
    internal static string HashOf(string ownerPath, ImmutableArray<DeclaredGeneratorFile> files)
    {
        var document = new StringBuilder("declared-generator/v1\n");
        document.Append("owner=").Append(ownerPath).Append('\n');
        foreach (var file in files)
            document.Append("file=").Append(file.Path).Append('\n')
                .Append("code=").Append(Sha256Hex(Normalize(file.Code))).Append('\n');
        return Sha256Hex(document.ToString());
    }

    /// <summary>
    /// The generators <paramref name="generator"/> defines, built on first use and then served from
    /// <paramref name="ledger"/>. The sources compile against the host's own reference set
    /// (<see cref="CompileReferences.Default"/>, which carries the Roslyn the process runs), are
    /// emitted deterministically, and load into a collectible context of their own.
    /// </summary>
    /// <param name="generator">The declared generator.</param>
    /// <param name="ledger">The compile owner's ledger, which holds the built generators.</param>
    /// <param name="loadedFor">The NodeType whose compile needs it — named in a refusal.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="CompilationException">The generator sources do not compile, or define no
    /// generator.</exception>
    internal static ImmutableArray<ISourceGenerator> Load(
        DeclaredGenerator generator, SourceGeneratorLedger ledger, string loadedFor, ILogger logger,
        CancellationToken ct)
        => ledger.DeclaredBuild(generator.Key, () => Build(generator, loadedFor, logger, ct));

    private static ImmutableArray<ISourceGenerator> Build(
        DeclaredGenerator generator, string loadedFor, ILogger logger, CancellationToken ct)
    {
        var parseOptions = EmitPipeline.CreateParseOptions();
        var trees = generator.Files
            .Select(f => CSharpSyntaxTree.ParseText(f.Code, parseOptions, path: f.Path, cancellationToken: ct))
            .ToList();
        var assemblyName = $"DeclaredGenerator_{CodeConventions.SanitizeNodeName(generator.OwnerPath)}_{generator.SourceHash[..16]}";
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            CompileReferences.Default,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                deterministic: true,
                concurrentBuild: false,
                nullableContextOptions: NullableContextOptions.Disable));

        using var image = new MemoryStream();
        var emit = compilation.Emit(image, cancellationToken: ct);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(10)
                .Select(d => d.ToString());
            throw new CompilationException(loadedFor,
                $"The source generator declared by '{generator.OwnerPath}' "
                + $"({string.Join(", ", generator.Files.Select(f => f.Path))}) does not compile, so "
                + $"'{loadedFor}' cannot be compiled with it: {string.Join(" | ", errors)}");
        }

        image.Position = 0;
        var context = new DeclaredGeneratorLoadContext($"{MetadataKey}:{generator.Key}");
        var assembly = context.LoadFromStream(image);
        var generators = SourceGeneratorLoader.InstantiateGenerators(assembly, generator.Key, logger);
        if (generators.IsDefaultOrEmpty)
            throw new CompilationException(loadedFor,
                $"'{generator.OwnerPath}' declares the generator source(s) "
                + $"{string.Join(", ", generator.Files.Select(f => f.Path))}, but they define no "
                + "[Generator] type with a parameterless constructor.");
        logger.LogInformation(
            "Declared source generator {Key} built for {LoadedFor}: {Count} generator(s)",
            generator.Key, loadedFor, generators.Length);
        return generators;
    }

    private static string Normalize(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return string.Empty;
        var text = code[0] == '﻿' ? code[1..] : code;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// The collectible context one declared generator build lives in. It resolves NOTHING itself:
    /// every reference — Microsoft.CodeAnalysis first of all — falls through to the default context,
    /// so the generator implements the host compiler's own <see cref="ISourceGenerator"/>.
    /// </summary>
    private sealed class DeclaredGeneratorLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
