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

        // The foreign owners whose code this compile CONSUMED — read off the files (roots and
        // includes), not off the declared types, so a foreign file that declares no type (only
        // directives or assembly attributes) is still counted as an inclusion.
        var foreignOwners = roots.Select(u => u.Path)
            .Concat(includes?.Keys ?? [])
            .Select(CodeConventions.OwnerOfCodePath)
            .OfType<string>()
            .Where(o => !IsSelf(o, selfPath))
            .ToImmutableSortedSet(StringComparer.OrdinalIgnoreCase);

        // A self-contained compile (no foreign file) pays for neither the per-file parse nor the walk.
        var nodeTypeEvidence = foreignOwners.IsEmpty
            ? ImmutableDictionary<string, ImmutableSortedSet<string>>.Empty
            : WalkReferences(compilation, authoredTrees, selfPath, DeclaredTypeOwners(selfPath, roots, includes, ct), ct);

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
    /// Declared type metadata name → its owning NodeType(s), from a per-file PARSE (no binding). Root
    /// files first; then each include's own declarations are attributed to the INCLUDED node's owner,
    /// which is the more specific answer for text that was spliced into a root. A type declared
    /// (partially) by the compiling type itself stays the compiling type's, so a partial class it
    /// extends is never an edge to itself.
    ///
    /// <para>Usually ONE owner. More than one only when a declaration sits in a span that two
    /// owners' include texts both match (identical snippets): text alone cannot tell which copy came
    /// from whom, so the declaration is attributed to EVERY candidate — an extra edge, never a
    /// missing one.</para>
    /// </summary>
    internal static ImmutableDictionary<string, ImmutableSortedSet<string>> DeclaredTypeOwners(
        string selfPath,
        IReadOnlyList<DependencySourceUnit> roots,
        IReadOnlyDictionary<string, string>? includes,
        CancellationToken ct)
    {
        var parseOptions = EmitPipeline.CreateParseOptions();
        var byName = new Dictionary<string, ImmutableSortedSet<string>>(StringComparer.Ordinal);

        void Attribute(string path, string code, bool overrideExisting)
        {
            var owner = CodeConventions.OwnerOfCodePath(path);
            if (owner is null || string.IsNullOrWhiteSpace(code))
                return;
            var root = CSharpSyntaxTree.ParseText(code, parseOptions, cancellationToken: ct).GetRoot(ct);
            foreach (var name in DeclaredTypeNames(root))
            {
                if (!byName.TryGetValue(name, out var existing))
                    byName[name] = Owners(owner);
                else if (overrideExisting || (!ContainsSelf(existing, selfPath) && IsSelf(owner, selfPath)))
                    byName[name] = Owners(owner);
            }
        }

        foreach (var unit in roots)
            Attribute(unit.Path, unit.Code, overrideExisting: false);
        if (includes is not null)
        {
            foreach (var (path, code) in includes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Attribute(path, code, overrideExisting: true);

            // An include spliced INSIDE a namespace of the including file declares its types under
            // that namespace, which a separate parse of the include cannot know. Find each include's
            // text in the root it was spliced into and attribute the declarations within that span,
            // under the names they carry IN THE ROOT, to the included node's owner.
            foreach (var unit in roots)
            {
                // EVERY occurrence: one snippet may be spliced in more than once (into two namespaces).
                var spans = includes
                    .Select(kv => (Owner: CodeConventions.OwnerOfCodePath(kv.Key), Text: kv.Value))
                    .Where(x => x.Owner is not null && x.Text.Length > 0)
                    .SelectMany(x => Occurrences(unit.Code, x.Text).Select(start => (x.Owner, Start: start, x.Text.Length)))
                    .ToArray();
                if (spans.Length == 0)
                    continue;
                var root = CSharpSyntaxTree.ParseText(unit.Code, parseOptions, cancellationToken: ct).GetRoot(ct);
                foreach (var declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
                {
                    if (TypeDeclarationName(declaration) is not { } name)
                        continue;
                    var at = declaration.SpanStart;
                    // EVERY owner whose text covers the declaration. Two owners supplying the same
                    // snippet both match both copies, and text cannot say which copy is whose — so
                    // both are named rather than the last one overwriting the other.
                    var covering = spans
                        .Where(span => at >= span.Start && at < span.Start + span.Length)
                        .Select(span => span.Owner!)
                        .ToImmutableSortedSet(StringComparer.OrdinalIgnoreCase);
                    if (!covering.IsEmpty)
                        byName[name] = covering;
                }
            }
        }
        return byName.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static ImmutableSortedSet<string> Owners(string owner) =>
        ImmutableSortedSet.Create(StringComparer.OrdinalIgnoreCase, owner);

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            yield return at;
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
                // ValueText, as the bound symbol spells it: `@Lib` and `\u004Cib` are both `Lib`.
                namespaces.Insert(0, string.Join(".", ns.Name.DescendantNodesAndSelf()
                    .OfType<IdentifierNameSyntax>().Select(i => i.Identifier.ValueText)));
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
        ImmutableDictionary<string, ImmutableSortedSet<string>> owners,
        CancellationToken ct)
    {
        var found = new Dictionary<string, ImmutableSortedSet<string>.Builder>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in authoredTrees)
        {
            var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var node in tree.GetRoot(ct).DescendantNodes(
                         descendIntoChildren: n => n is not UsingDirectiveSyntax))
            {
                ct.ThrowIfCancellationRequested();
                // A using directive is an IMPORT, not a use — and a hoisted one sits in the skeleton,
                // outside every attributed type, where it would read as the compiling type's own.
                if (node is UsingDirectiveSyntax)
                    continue;
                var bound = BoundSymbols(node, model, ct).ToArray();
                if (bound.Length == 0
                    || OwnerOfLocation(node, owners) is { } locationOwners && !ContainsSelf(locationOwners, selfPath))
                    continue;
                foreach (var (typeName, typeOwners) in bound.SelectMany(symbol => OwnedSourceTypes(symbol, compilation, owners)))
                {
                    // Only the self owner is dropped: identical text supplied by the compiling type
                    // AND a foreign owner names both, and the foreign one is still an edge.
                    foreach (var owner in typeOwners.Where(o => !IsSelf(o, selfPath)))
                    {
                        if (!found.TryGetValue(owner, out var set))
                            found[owner] = set = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
                        set.Add(typeName);
                    }
                }
            }
        }
        return found.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every symbol a syntax node BINDS — named (a simple name) or implicit: the enumerator pattern
    /// of a <c>foreach</c> (an extension <c>GetEnumerator</c> nobody names), the awaiter pattern of an
    /// <c>await</c>, a deconstruction's <c>Deconstruct</c>, a query clause's operator methods, a
    /// collection initializer's <c>Add</c>, a target-typed <c>new()</c>'s constructor, an indexer
    /// (plain or conditional), the <c>Dispose</c> a <c>using</c> calls, and a user-defined implicit
    /// conversion.
    /// </summary>
    private static IEnumerable<ISymbol?> BoundSymbols(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        switch (node)
        {
            case SimpleNameSyntax name:
                var info = model.GetSymbolInfo(name, ct);
                yield return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                break;
            case CommonForEachStatementSyntax forEach:
                var each = model.GetForEachStatementInfo(forEach);
                yield return each.GetEnumeratorMethod;
                yield return each.MoveNextMethod;
                yield return each.CurrentProperty;
                // The per-element conversions (`foreach (int v in Make())` over a C-owned element).
                yield return each.CurrentConversion.MethodSymbol;
                yield return each.ElementConversion.MethodSymbol;
                if (forEach is ForEachVariableStatementSyntax deconstructed)
                    foreach (var method in DeconstructMethods(model.GetDeconstructionInfo(deconstructed)))
                        yield return method;
                break;
            case AwaitExpressionSyntax awaited:
                var awaitInfo = model.GetAwaitExpressionInfo(awaited);
                yield return awaitInfo.GetAwaiterMethod;
                yield return awaitInfo.GetResultMethod;
                break;
            case AssignmentExpressionSyntax { Left: TupleExpressionSyntax or DeclarationExpressionSyntax } assignment:
                foreach (var method in DeconstructMethods(model.GetDeconstructionInfo(assignment)))
                    yield return method;
                break;
            case QueryClauseSyntax clause:
                var clauseInfo = model.GetQueryClauseInfo(clause, ct);
                yield return clauseInfo.CastInfo.Symbol;
                yield return clauseInfo.OperationInfo.Symbol;
                break;
            case SelectOrGroupClauseSyntax or OrderingSyntax:
                yield return model.GetSymbolInfo(node, ct).Symbol;
                break;
            case BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax:
                // A user-defined operator (A's factory returns C's struct; B adds two of them).
                yield return model.GetSymbolInfo(node, ct).Symbol;
                break;
            case AssignmentExpressionSyntax compound when !compound.IsKind(SyntaxKind.SimpleAssignmentExpression):
                yield return model.GetSymbolInfo(compound, ct).Symbol;
                break;
            case ImplicitObjectCreationExpressionSyntax created:
                yield return model.GetSymbolInfo(created, ct).Symbol;
                break;
            case InitializerExpressionSyntax { RawKind: (int)SyntaxKind.CollectionInitializerExpression } initializer:
                foreach (var element in initializer.Expressions)
                    yield return model.GetCollectionInitializerSymbolInfo(element, ct).Symbol;
                break;
            case InvocationExpressionSyntax:
                // `Make()()` invokes a C-owned delegate's Invoke, which no name spells.
                yield return model.GetSymbolInfo(node, ct).Symbol;
                break;
            case WithExpressionSyntax with:
                // `Make() with { }` calls the receiver record's synthesized clone.
                yield return model.GetTypeInfo(with.Expression, ct).Type;
                break;
            case ElementAccessExpressionSyntax or ElementBindingExpressionSyntax:
                // An indexer: `Make()[0]` and `Make()?[0]` name no member of the indexed type.
                yield return model.GetSymbolInfo(node, ct).Symbol;
                break;
            case UsingStatementSyntax usingStatement:
                // `using (Make()) { }` disposes a value whose type nobody names.
                var resource = usingStatement.Expression is { } usedExpression
                    ? model.GetTypeInfo(usedExpression, ct).Type
                    : usingStatement.Declaration is { } declared ? model.GetTypeInfo(declared.Type, ct).Type : null;
                yield return DisposeOf(resource, usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword));
                break;
            case LocalDeclarationStatementSyntax { UsingKeyword.RawKind: (int)SyntaxKind.UsingKeyword } usingDeclaration:
                yield return DisposeOf(model.GetTypeInfo(usingDeclaration.Declaration.Type, ct).Type,
                    usingDeclaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword));
                break;
        }

        if (node is ExpressionSyntax expression && model.GetConversion(expression, ct) is { IsUserDefined: true } conversion)
            yield return conversion.MethodSymbol;
    }

    /// <summary>The method a <c>using</c> calls on <paramref name="type"/> — its own (or inherited)
    /// parameterless <c>Dispose</c> / <c>DisposeAsync</c>, the pattern a ref struct relies on — or the
    /// type itself when the call goes through an explicit interface implementation.</summary>
    private static ISymbol? DisposeOf(ITypeSymbol? type, bool isAsync)
    {
        var name = isAsync ? "DisposeAsync" : "Dispose";
        for (var t = type; t is not null; t = t.BaseType)
            if (t.GetMembers(name).OfType<IMethodSymbol>()
                    .FirstOrDefault(m => !m.IsStatic && m.Parameters.All(p => p.IsOptional || p.IsParams)) is { } method)
                return method;
        return type;
    }

    private static IEnumerable<ISymbol?> DeconstructMethods(DeconstructionInfo info)
    {
        yield return info.Method;
        foreach (var nested in info.Nested)
            foreach (var method in DeconstructMethods(nested))
                yield return method;
    }

    /// <summary>The owner of the code a reference sits in: the owner of its INNERMOST enclosing type
    /// declaration the ownership map names, or null when it sits outside every attributed type (the generated skeleton, the
    /// <c>configuration</c> lambda, an assembly attribute) — which the caller reads as the compiling
    /// type's own code.</summary>
    private static ImmutableSortedSet<string>? OwnerOfLocation(
        SyntaxNode node, ImmutableDictionary<string, ImmutableSortedSet<string>> owners)
    {
        // Innermost enclosing declaration the map names: a foreign record spliced into the compiling
        // type's class is the foreign owner's code, not the class's.
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (parent is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
                && TypeDeclarationName(parent) is { } name
                && owners.TryGetValue(name, out var owner))
                return owner;
        return null;
    }

    /// <summary>The OWNED types a symbol reaches — its own type (or containing type for a member),
    /// plus every type argument of a constructed type or generic method, array element and pointer
    /// target, recursively — each looked up from itself outwards through its containing types, the
    /// first one the ownership map names, when declared in THIS compilation's source. Nothing for a
    /// namespace, a local, or anything from a referenced assembly. Innermost first, so A's record
    /// spliced into B's class (<c>Host+AThing</c>) is A's. The type arguments are what keep an alias
    /// (<c>using AList = List&lt;AThing&gt;;</c>) an edge: its uses bind the external <c>List&lt;T&gt;</c>,
    /// and only the argument names the foreign owner.</summary>
    private static IEnumerable<(string Name, ImmutableSortedSet<string> Owners)> OwnedSourceTypes(
        ISymbol? symbol, CSharpCompilation compilation, ImmutableDictionary<string, ImmutableSortedSet<string>> owners)
    {
        IEnumerable<INamedTypeSymbol> reached = symbol switch
        {
            null or INamespaceSymbol or ILocalSymbol or IParameterSymbol or IRangeVariableSymbol
                or ILabelSymbol or IDiscardSymbol => [],
            ITypeSymbol type => TypesIn(type),
            IMethodSymbol method => TypesIn(method.ContainingType).Concat(method.TypeArguments.SelectMany(TypesIn)),
            _ => TypesIn(symbol.ContainingType),
        };
        foreach (var type in reached.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
                continue;
            for (var t = type; t is not null; t = t.ContainingType?.OriginalDefinition)
            {
                var name = MetadataNameOf(t);
                if (owners.TryGetValue(name, out var typeOwners))
                {
                    yield return (name, typeOwners);
                    break;
                }
            }
        }
    }

    /// <summary>The original definitions of <paramref name="type"/> and of every type its
    /// construction mentions (type arguments, containing types' arguments, array elements, pointer
    /// targets).</summary>
    private static IEnumerable<INamedTypeSymbol> TypesIn(ITypeSymbol? type)
    {
        switch (type)
        {
            case INamedTypeSymbol named:
                yield return named.OriginalDefinition;
                for (var t = named; t is not null; t = t.ContainingType)
                    foreach (var argument in t.TypeArguments)
                        foreach (var inner in TypesIn(argument))
                            yield return inner;
                break;
            case IArrayTypeSymbol array:
                foreach (var inner in TypesIn(array.ElementType))
                    yield return inner;
                break;
            case IPointerTypeSymbol pointer:
                foreach (var inner in TypesIn(pointer.PointedAtType))
                    yield return inner;
                break;
        }
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

    /// <summary>A declaration the compiling type (partly) owns is its own: never an edge to itself.</summary>
    private static bool ContainsSelf(ImmutableSortedSet<string> owners, string selfPath) =>
        owners.Any(o => IsSelf(o, selfPath));
}
