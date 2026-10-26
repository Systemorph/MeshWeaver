using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MeshWeaver.Compiler;

/// <summary>One Code file a compile consumed: the node path it came from and the text Roslyn saw
/// for it (after <c>@@</c> include substitution for a root file; the included node's own text for
/// an include).</summary>
/// <param name="Path">The Code node's mesh path.</param>
/// <param name="Code">The file's C# text.</param>
public sealed record DependencySourceUnit(string Path, string Code);

/// <summary>
/// Reads the FORWARD dependency edges of one compiled NodeType off the Roslyn compilation that
/// produced it — <c>Doc/Architecture/DependencyGraph</c>. Pure: no mesh, no IO; the caller decides
/// where the result is written.
///
/// <para><b>Two kinds of edge.</b> A <see cref="DependencyEdgeKinds.NodeType"/> edge is a symbol
/// the compiling type's own code binds whose declaration ANOTHER NodeType owns — found by a
/// <see cref="SemanticModel"/> walk, with ownership read from which Code file declared the type
/// (<see cref="CodeConventions.OwnerOfCodePath"/>). A NodeType compile is never referenced against
/// another NodeType's assembly; foreign code arrives as SOURCE (<c>shared=@Other/Source</c>, an
/// <c>@@</c> include) and is compiled into this assembly, so the containing assembly of every such
/// symbol is this one and only the declaring file can say whose it is. A
/// <see cref="DependencyEdgeKinds.Module"/> edge is an installed module assembly the compilation
/// USED (<see cref="Compilation.GetUsedAssemblyReferences"/>), carrying the module's MVID.</para>
/// </summary>
public static class DependencyEdgeExtraction
{
    /// <summary>The most referenced symbol names one edge keeps as evidence.</summary>
    public const int MaxEvidence = 5;

    /// <summary>
    /// Extracts the forward edge set of <paramref name="selfPath"/>'s compile.
    /// </summary>
    /// <param name="compilation">The compilation the emit used (after source generators).</param>
    /// <param name="authoredTrees">The trees to walk for references — the AUTHORED input (skeleton
    /// plus concatenated user code), not generator output.</param>
    /// <param name="selfPath">The compiling NodeType's path.</param>
    /// <param name="roots">The Code files the source queries matched, with their resolved text.</param>
    /// <param name="includes">The <c>@@</c> include closure (included node path → its text).</param>
    /// <param name="moduleReferences">The metadata references that are installed MODULES (as
    /// opposed to the platform baseline or NuGet packages), keyed by reference.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The edge set, without build identity (the caller stamps the emitted MVID).</returns>
    public static DependencyEdgeSet Extract(
        CSharpCompilation compilation,
        IEnumerable<SyntaxTree> authoredTrees,
        string selfPath,
        IReadOnlyList<DependencySourceUnit> roots,
        IReadOnlyDictionary<string, string>? includes,
        IReadOnlySet<MetadataReference> moduleReferences,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrEmpty(selfPath);

        var owners = DeclaredTypeOwners(selfPath, roots, includes, ct);
        var foreignOwners = owners.Values.Where(o => !IsSelf(o, selfPath))
            .ToImmutableSortedSet(StringComparer.OrdinalIgnoreCase);

        var nodeTypeEvidence = foreignOwners.IsEmpty
            ? ImmutableDictionary<string, ImmutableSortedSet<string>>.Empty
            : WalkReferences(compilation, authoredTrees, selfPath, owners, ct);

        var edges = ImmutableList.CreateBuilder<DependencyEdge>();
        foreach (var (target, evidence) in nodeTypeEvidence.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            edges.Add(new DependencyEdge
            {
                Target = target,
                Kind = DependencyEdgeKinds.NodeType,
                Evidence = [.. evidence.Take(MaxEvidence)],
            });
        foreach (var module in UsedModules(compilation, moduleReferences, ct))
            edges.Add(module);

        return new DependencyEdgeSet
        {
            Source = selfPath,
            Edges = edges.ToImmutable(),
            IncludedWithoutUse = [.. foreignOwners
                .Where(o => !nodeTypeEvidence.ContainsKey(o))
                .OrderBy(o => o, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Declared type metadata name → owning NodeType, from a per-file PARSE (no binding). Root files
    /// first; then each include's own declarations are attributed to the INCLUDED node's owner, which
    /// is the more specific answer for text that was spliced into a root. A type declared (partially)
    /// by the compiling type itself stays the compiling type's, so a partial class it extends is
    /// never an edge to itself.
    /// </summary>
    internal static ImmutableDictionary<string, string> DeclaredTypeOwners(
        string selfPath,
        IReadOnlyList<DependencySourceUnit> roots,
        IReadOnlyDictionary<string, string>? includes,
        CancellationToken ct)
    {
        var parseOptions = EmitPipeline.CreateParseOptions();
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);

        void Attribute(string path, string code, bool overrideExisting)
        {
            var owner = CodeConventions.OwnerOfCodePath(path);
            if (owner is null || string.IsNullOrWhiteSpace(code))
                return;
            var root = CSharpSyntaxTree.ParseText(code, parseOptions, cancellationToken: ct).GetRoot(ct);
            foreach (var name in DeclaredTypeNames(root))
            {
                if (!byName.TryGetValue(name, out var existing))
                    byName[name] = owner;
                else if (overrideExisting || (!IsSelf(existing, selfPath) && IsSelf(owner, selfPath)))
                    byName[name] = owner;
            }
        }

        foreach (var unit in roots)
            Attribute(unit.Path, unit.Code, overrideExisting: false);
        if (includes is not null)
            foreach (var (path, code) in includes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Attribute(path, code, overrideExisting: true);
        return byName.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>The metadata names (<c>Ns.Outer+Inner`1</c>) of every type declared in a tree —
    /// the same spelling <see cref="MetadataNameOf"/> produces for the bound symbol.</summary>
    internal static IEnumerable<string> DeclaredTypeNames(SyntaxNode root)
    {
        foreach (var declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
            if (TypeDeclarationName(declaration) is { } name)
                yield return name;
    }

    private static string? TypeDeclarationName(SyntaxNode declaration)
    {
        string? Simple(SyntaxNode node) => node switch
        {
            TypeDeclarationSyntax t => Arity(t.Identifier.ValueText, t.TypeParameterList?.Parameters.Count ?? 0),
            EnumDeclarationSyntax e => e.Identifier.ValueText,
            DelegateDeclarationSyntax d => Arity(d.Identifier.ValueText, d.TypeParameterList?.Parameters.Count ?? 0),
            _ => null,
        };

        if (Simple(declaration) is not { } name)
            return null;
        var namespaces = new List<string>();
        for (var parent = declaration.Parent; parent is not null; parent = parent.Parent)
        {
            if (Simple(parent) is { } outer)
                name = outer + "+" + name;
            else if (parent is BaseNamespaceDeclarationSyntax ns)
                namespaces.Insert(0, string.Concat(ns.Name.ToString().Where(c => !char.IsWhiteSpace(c))));
        }
        return namespaces.Count == 0 ? name : string.Join(".", namespaces) + "." + name;
    }

    private static string Arity(string name, int arity) => arity == 0 ? name : $"{name}`{arity}";

    /// <summary>The metadata name of a source-declared named type, spelled as
    /// <see cref="DeclaredTypeNames"/> spells it.</summary>
    internal static string MetadataNameOf(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            name = outer.MetadataName + "+" + name;
        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
    }

    /// <summary>
    /// The <see cref="SemanticModel"/> walk: every name in the authored trees, bound, whose target is
    /// a type (or a member of a type) declared in this compilation and owned by ANOTHER NodeType —
    /// counted only when the reference sits in code the compiling type owns. Returns owner → the
    /// referenced types' metadata names.
    /// </summary>
    private static ImmutableDictionary<string, ImmutableSortedSet<string>> WalkReferences(
        CSharpCompilation compilation,
        IEnumerable<SyntaxTree> authoredTrees,
        string selfPath,
        ImmutableDictionary<string, string> owners,
        CancellationToken ct)
    {
        var found = new Dictionary<string, ImmutableSortedSet<string>.Builder>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in authoredTrees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var name in tree.GetRoot(ct).DescendantNodes().OfType<SimpleNameSyntax>())
            {
                ct.ThrowIfCancellationRequested();
                if (!IsSelf(OwnerOfLocation(name, owners) ?? selfPath, selfPath))
                    continue;
                var info = model.GetSymbolInfo(name, ct);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (OutermostSourceType(symbol, compilation) is not { } type)
                    continue;
                var typeName = MetadataNameOf(type);
                if (!owners.TryGetValue(typeName, out var owner) || IsSelf(owner, selfPath))
                    continue;
                if (!found.TryGetValue(owner, out var set))
                    found[owner] = set = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
                set.Add(typeName);
            }
        }
        return found.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The owner of the code a reference sits in: the owner of its OUTERMOST enclosing type
    /// declaration, or null when it sits outside every attributed type (the generated skeleton, the
    /// <c>configuration</c> lambda, an assembly attribute) — which the caller reads as the compiling
    /// type's own code.</summary>
    private static string? OwnerOfLocation(SyntaxNode node, ImmutableDictionary<string, string> owners)
    {
        SyntaxNode? outermost = null;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (parent is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                outermost = parent;
        return outermost is not null && TypeDeclarationName(outermost) is { } name
            && owners.TryGetValue(name, out var owner)
            ? owner
            : null;
    }

    /// <summary>The outermost named type a symbol belongs to, when that type is declared in THIS
    /// compilation's source; null for a namespace, a local, or anything from a referenced assembly.</summary>
    private static INamedTypeSymbol? OutermostSourceType(ISymbol? symbol, CSharpCompilation compilation)
    {
        var type = symbol switch
        {
            null or INamespaceSymbol or ILocalSymbol or IParameterSymbol or IRangeVariableSymbol
                or ILabelSymbol or IDiscardSymbol => null,
            INamedTypeSymbol named => named.OriginalDefinition,
            _ => symbol.ContainingType?.OriginalDefinition,
        };
        if (type is null)
            return null;
        while (type.ContainingType is { } outer)
            type = outer.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly) ? type : null;
    }

    /// <summary>The module edges: every reference the compilation USED that is an installed module,
    /// with the module's MVID read from its metadata. Ordinal by assembly name.</summary>
    private static IEnumerable<DependencyEdge> UsedModules(
        CSharpCompilation compilation, IReadOnlySet<MetadataReference> moduleReferences, CancellationToken ct)
    {
        if (moduleReferences.Count == 0)
            return [];
        var edges = new SortedDictionary<string, DependencyEdge>(StringComparer.Ordinal);
        foreach (var reference in compilation.GetUsedAssemblyReferences(ct))
        {
            if (!moduleReferences.Contains(reference)
                || compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;
            edges[assembly.Identity.Name] = new DependencyEdge
            {
                Target = assembly.Identity.Name,
                Kind = DependencyEdgeKinds.Module,
                TargetBuildMvid = MvidOf(reference),
            };
        }
        return edges.Values;
    }

    private static string? MvidOf(MetadataReference reference)
    {
        try
        {
            return reference is PortableExecutableReference pe
                   && pe.GetMetadata() is AssemblyMetadata metadata
                   && metadata.GetModules() is { Length: > 0 } modules
                ? modules[0].GetModuleVersionId().ToString("N")
                : null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            // The reference was good enough for the compile, so this cannot normally happen; the
            // edge still stands — only its build identity is unknown, which a reader sees as null.
            return null;
        }
    }

    /// <summary>
    /// The references among <paramref name="references"/> whose file is one of
    /// <paramref name="moduleLocations"/> — the installed modules, told apart from the platform
    /// baseline and NuGet packages by WHICH FILE they are, never by name. An image-bound module
    /// whose file also sits in the baseline set is still a module.
    /// </summary>
    /// <param name="references">The compile's metadata references.</param>
    /// <param name="moduleLocations">The installed module assemblies' file paths.</param>
    public static IReadOnlySet<MetadataReference> ModuleReferences(
        IEnumerable<MetadataReference> references, IEnumerable<string?> moduleLocations)
    {
        var locations = moduleLocations
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => Path.GetFullPath(l!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (locations.Count == 0)
            return ImmutableHashSet<MetadataReference>.Empty;
        return references
            .Where(r => r is PortableExecutableReference { FilePath: { Length: > 0 } file }
                        && locations.Contains(Path.GetFullPath(file)))
            .ToImmutableHashSet();
    }

    private static bool IsSelf(string owner, string selfPath) =>
        string.Equals(owner, selfPath, StringComparison.OrdinalIgnoreCase);
}
