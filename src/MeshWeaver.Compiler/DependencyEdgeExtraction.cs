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
/// produced it — <c>Doc/Architecture/DependencyGraph</c>. Pure: no mesh; the only IO is reading the
/// emitted image the caller hands over; the caller decides where the result is written.
///
/// <para><b>Static and dynamic edges.</b> Beyond the two described next,
/// <see cref="DependencyEdgeKinds.Platform"/> and <see cref="DependencyEdgeKinds.Package"/> edges are
/// read off the EMITTED image's metadata (<see cref="EmittedReferenceEdges(CSharpCompilation, Stream, DependencyReferenceClasses)"/>)
/// and the dynamic ones off reflection calls in the type's own code
/// (<see cref="DynamicBindingExtraction"/>).</para>
///
/// <para><b>The NodeType and Module edges.</b> A <see cref="DependencyEdgeKinds.NodeType"/> edge is a symbol
/// the compiling type's own code binds whose declaration ANOTHER NodeType owns — found by a
/// <see cref="SemanticModel"/> walk, with ownership read from which Code file declared the type
/// (<see cref="CodeConventions.OwnerOfCodePath"/>). A NodeType compile is never referenced against
/// another NodeType's assembly; foreign code arrives as SOURCE (<c>shared=@Other/Source</c>, an
/// <c>@@</c> include) and is compiled into this assembly, so the containing assembly of every such
/// symbol is this one and only the declaring file can say whose it is. A
/// <see cref="DependencyEdgeKinds.Module"/> edge is an installed module assembly the emitted image
/// carries an <c>AssemblyRef</c> to (<see cref="Compilation.GetUsedAssemblyReferences"/> only when no
/// image is supplied), carrying the module's MVID.</para>
/// </summary>
public static class DependencyEdgeExtraction
{
    /// <summary>The most referenced symbol names one edge keeps as evidence.</summary>
    public const int MaxEvidence = 5;

    /// <summary>
    /// Extracts the forward edge set of <paramref name="selfPath"/>'s compile, with only the
    /// module references classified — every other reference counts as the platform host's.
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
        CancellationToken ct = default) =>
        Extract(compilation, authoredTrees, selfPath, roots, includes,
            new DependencyReferenceClasses(moduleReferences, ImmutableDictionary<MetadataReference, string>.Empty),
            emittedImage: null, nodeGeneratedTrees: null, ct);

    /// <summary>
    /// Extracts the forward edge set of <paramref name="selfPath"/>'s compile: the STATIC edges
    /// (<see cref="DependencyEdgeKinds.NodeType"/>, <see cref="DependencyEdgeKinds.Module"/>,
    /// <see cref="DependencyEdgeKinds.Platform"/>, <see cref="DependencyEdgeKinds.Package"/>) and
    /// the DYNAMIC ones (<see cref="DependencyEdgeKinds.Dynamic"/>,
    /// <see cref="DependencyEdgeKinds.DynamicUnresolved"/>) — reflection calls in the compiling
    /// type's own code (<see cref="DynamicBindingExtraction"/>).
    /// </summary>
    /// <param name="compilation">The compilation the emit used (after source generators).</param>
    /// <param name="authoredTrees">The trees to walk for references — the AUTHORED input.</param>
    /// <param name="selfPath">The compiling NodeType's path.</param>
    /// <param name="roots">The Code files the source queries matched, with their resolved text.</param>
    /// <param name="includes">The <c>@@</c> include closure (included node path → its text).</param>
    /// <param name="references">What each reference is — module, package, or the platform host's.</param>
    /// <param name="emittedImage">Opens the EMITTED assembly's bytes, which the platform and package
    /// edges are read from (<see cref="EmittedReferenceEdges(CSharpCompilation, Stream, DependencyReferenceClasses)"/>);
    /// null records neither kind — the set then says nothing about them, which a reader treats as
    /// unknown, never as "binds no platform assembly".</param>
    /// <param name="nodeGeneratedTrees">The trees a NODE-SUPPLIED source generator emitted (one the
    /// node <c>#r</c>'d) — the compiling type's own code by PROVENANCE, even where they extend a
    /// partial type a foreign owner declares, because no other compile ever sees them.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The edge set, without build identity (the caller stamps the emitted MVID).</returns>
    public static DependencyEdgeSet Extract(
        CSharpCompilation compilation,
        IEnumerable<SyntaxTree> authoredTrees,
        string selfPath,
        IReadOnlyList<DependencySourceUnit> roots,
        IReadOnlyDictionary<string, string>? includes,
        DependencyReferenceClasses references,
        Func<Stream>? emittedImage,
        IReadOnlyCollection<SyntaxTree>? nodeGeneratedTrees = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrEmpty(selfPath);
        ArgumentNullException.ThrowIfNull(references);
        var generated = (nodeGeneratedTrees ?? []).ToImmutableHashSet();
        var trees = authoredTrees.Concat(generated).Distinct().ToList();

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
        var owners = foreignOwners.IsEmpty
            ? ImmutableDictionary<string, ImmutableSortedSet<string>>.Empty
            : DeclaredTypeOwners(selfPath, roots, includes, ct);
        var nodeTypeEvidence = foreignOwners.IsEmpty
            ? ImmutableDictionary<string, ImmutableSortedSet<string>>.Empty
            : WalkReferences(compilation, trees, selfPath, owners, generated, ct);

        var edges = ImmutableList.CreateBuilder<DependencyEdge>();
        foreach (var (target, evidence) in nodeTypeEvidence.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            edges.Add(new DependencyEdge
            {
                Target = target,
                Kind = DependencyEdgeKinds.NodeType,
                Evidence = [.. evidence.Take(MaxEvidence)],
            });
        // Read off the EMITTED image when there is one — the same two metadata tables the platform
        // and package edges come from. GetUsedAssemblyReferences re-binds and lowers every method
        // body of the compilation a SECOND time (the emit already did it once), which made the
        // module edges alone cost about two thirds of the emit on every NodeType compile.
        var modules = (emittedImage is null
            ? UsedModules(compilation, references.Modules, ct)
            : EmittedModuleEdges(compilation, emittedImage, references.Modules, ct)).ToList();
        edges.AddRange(modules);
        if (emittedImage is not null)
        {
            using var image = emittedImage();
            // One assembly name is one runtime identity: when a module and a host assembly share a
            // name, the installed module (classified by FILE) is the edge, never a second one.
            var moduleNames = modules.Select(m => m.Target).ToHashSet(StringComparer.Ordinal);
            edges.AddRange(EmittedReferenceEdges(compilation, image, references)
                .Where(e => !(e.Kind == DependencyEdgeKinds.Platform && moduleNames.Contains(e.Target))));
        }
        // Dynamic bindings count only in code the compiling type OWNS — a reflection call inside a
        // foreign owner's code is that owner's edge, recorded by its own compile.
        edges.AddRange(DynamicBindingExtraction.Extract(
            compilation, trees, references,
            // Generated by a node-supplied generator ⇒ this type's code, whatever partial it extends.
            node => generated.Contains(node.SyntaxTree)
                    || owners.IsEmpty || OwnerOfLocation(node, owners) is not { } o || ContainsSelf(o, selfPath),
            ct));

        return new DependencyEdgeSet
        {
            Source = selfPath,
            Edges = UniqueTargets(selfPath, edges.ToImmutable()),
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

    internal static string? TypeDeclarationName(SyntaxNode declaration)
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
        ImmutableHashSet<SyntaxTree> selfTrees,
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
                if (!selfTrees.Contains(tree)
                    && OwnerOfLocation(node, owners) is { } locationOwners && !ContainsSelf(locationOwners, selfPath))
                    continue;
                var bound = BoundSymbols(node, model, ct).ToArray();
                if (bound.Length == 0)
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
    internal static ImmutableSortedSet<string>? OwnerOfLocation(
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

    /// <summary>The MODULE edges of an EMITTED assembly: every <c>AssemblyRef</c> the compiler wrote
    /// that resolves (by full identity, <see cref="ReferencesFor"/>) to an installed module, with the
    /// module's MVID. Ordinal by assembly name, exactly as <see cref="UsedModules"/> orders them.
    /// <para>The compiler writes an <c>AssemblyRef</c> only for an assembly the IL or metadata
    /// needs, so this is the BOUND set — the instrument the platform and package edges already use,
    /// for the reason <see cref="EmittedReferenceEdges(CSharpCompilation, Stream, DependencyReferenceClasses)"/>
    /// gives: <see cref="Compilation.GetUsedAssemblyReferences"/> also counts a module whose namespace
    /// a <c>using</c> merely imports, and it pays for that answer with a second full binding pass
    /// over every method body. What metadata cannot show — a module symbol the compiler ERASED (an
    /// inlined constant, a <c>[Conditional]</c> call, a <c>nameof</c>) — is added by
    /// <see cref="CompileTimeOnlyModuleEdges"/>, so such a module is still that type's edge: a module
    /// edge selects the type when the module changes, and that change changes or breaks the type's
    /// compile.</para></summary>
    private static IEnumerable<DependencyEdge> EmittedModuleEdges(
        CSharpCompilation compilation, Func<Stream> emittedImage, IReadOnlySet<MetadataReference> moduleReferences,
        CancellationToken ct)
    {
        if (moduleReferences.Count == 0)
            return [];
        var identities = ReferenceIdentities(compilation);
        var edges = new SortedDictionary<string, DependencyEdge>(StringComparer.Ordinal);
        using var image = emittedImage();
        using var pe = new System.Reflection.PortableExecutable.PEReader(image, System.Reflection.PortableExecutable.PEStreamOptions.LeaveOpen);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        foreach (var handle in reader.AssemblyReferences)
            foreach (var reference in ReferencesFor(EmittedAssemblyRefOf(reader, handle), identities))
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
        foreach (var edge in CompileTimeOnlyModuleEdges(compilation, moduleReferences, edges.Keys.ToHashSet(StringComparer.Ordinal), ct))
            edges[edge.Target] = edge;
        return edges.Values;
    }

    /// <summary>The MODULE edges the emitted image cannot show. The compiler leaves no
    /// <c>AssemblyRef</c> for a module symbol it ERASED: a constant (an enum member included) it
    /// inlined, a call to a <c>[Conditional]</c> method (or a use of a <c>[Conditional]</c>
    /// attribute) whose symbol is undefined — and every module member inside that call's arguments
    /// and receiver, whoever declares the conditional method — a module type that only appears in a constant
    /// expression (<c>(int)default(Color)</c>), and any name inside a <c>nameof</c> — a generic
    /// argument included. A change to that module still changes or breaks this type's compile.
    /// Candidates are nominated by NAME — every identifier inside a <c>nameof</c>, and every
    /// identifier spelling a type name (an attribute's with and without its suffix), a constant or
    /// a <c>[Conditional]</c> method that a not-yet-edged module declares — and only those nodes
    /// are bound: an edge when one binds a symbol of that module. That is a syntax walk plus local
    /// bindings, never the second full binding pass <see cref="Compilation.GetUsedAssemblyReferences"/> pays.</summary>
    private static IEnumerable<DependencyEdge> CompileTimeOnlyModuleEdges(
        CSharpCompilation compilation, IReadOnlySet<MetadataReference> moduleReferences,
        IReadOnlySet<string> alreadyEdges, CancellationToken ct)
    {
        var pending = new Dictionary<IAssemblySymbol, MetadataReference>(SymbolEqualityComparer.Default);
        foreach (var reference in moduleReferences)
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly
                && !alreadyEdges.Contains(assembly.Identity.Name))
                pending.TryAdd(assembly, reference);
        if (pending.Count == 0)
            return [];
        var nominated = new HashSet<string>(StringComparer.Ordinal);
        var conditionalNames = new HashSet<string>(StringComparer.Ordinal);
        // Erasable NAMES come from the pending modules only; erasing CALLEES from every module — an
        // already-edged module's [Conditional] method still erases its arguments, and a module member
        // used only there belongs to a module that may not be an edge yet.
        var discarded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in moduleReferences)
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
            {
                discarded.Clear();
                CollectErasableNames(assembly.GlobalNamespace, pending.ContainsKey(assembly) ? nominated : discarded,
                    conditionalNames, ct);
            }
        CollectConditionalCallees(compilation, conditionalNames, ct);

        var found = new SortedDictionary<string, DependencyEdge>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (pending.Count == 0)
                break;
            SemanticModel? model = null;
            var root = tree.GetRoot(ct);
            var insideNameof = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i is { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" }, ArgumentList.Arguments.Count: 1 })
                .SelectMany(i => i.ArgumentList.Arguments[0].Expression.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
                .ToHashSet();
            // An erased [Conditional] call erases its ARGUMENTS and receiver too: a module member used
            // only there (an extension method, a property) leaves no AssemblyRef, whoever declares the
            // conditional method. Only invocations whose callee NAME is a known conditional method are
            // bound; every name inside such a call's receiver and arguments is then bound below.
            var erased = new HashSet<SimpleNameSyntax>();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var (callee, receiver) = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax access => (access.Name, (SyntaxNode?)access.Expression),
                    MemberBindingExpressionSyntax binding => (binding.Name, null),
                    SimpleNameSyntax simple => (simple, null),
                    _ => (null, null),
                };
                if (callee is null || !conditionalNames.Contains(callee.Identifier.ValueText))
                    continue;
                model ??= compilation.GetSemanticModel(tree);
                if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method
                    || !IsErasingCallee((method.ReducedFrom ?? method).OriginalDefinition))
                    continue;
                foreach (var inner in invocation.ArgumentList.DescendantNodes().OfType<SimpleNameSyntax>())
                    erased.Add(inner);
                if (receiver is not null)
                    foreach (var inner in receiver.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
                        erased.Add(inner);
            }
            // Code the compiler DISCARDS as constant-unreachable (#7449) is bound — so a change to a
            // module it uses still breaks this type — yet emits no IL and leaves no AssemblyRef:
            // `if (false) { … }`, `false && …`, `true || …`, a constant ternary's dead arm, `while (false)`,
            // `for (…; false; …)`. Every name in such a branch is bound like an erased call's arguments.
            foreach (var unreachable in ConstantUnreachableBranches(root, () => model ??= compilation.GetSemanticModel(tree), ct))
                foreach (var inner in unreachable.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
                    erased.Add(inner);
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (pending.Count == 0)
                    break;
                if (!insideNameof.Contains(name) && !erased.Contains(name) && !nominated.Contains(name.Identifier.ValueText))
                    continue;
                model ??= compilation.GetSemanticModel(tree);
                var info = model.GetSymbolInfo(name, ct);
                ImmutableArray<ISymbol> symbols = info.Symbol is { } bound ? [bound] : info.CandidateSymbols;
                if (symbols.IsEmpty && model.GetTypeInfo(name, ct).Type is { } type)
                    symbols = [type];
                foreach (var symbol in symbols)
                {
                    var declared = symbol switch
                    {
                        IAliasSymbol alias => alias.Target,
                        IMethodSymbol method => (method.ReducedFrom ?? method).OriginalDefinition,
                        _ => symbol.OriginalDefinition,
                    };
                    if (declared is INamespaceSymbol
                        || declared.ContainingAssembly is not { } owner
                        || !pending.TryGetValue(owner, out var reference))
                        continue;
                    found[owner.Identity.Name] = new DependencyEdge
                    {
                        Target = owner.Identity.Name,
                        Kind = DependencyEdgeKinds.Module,
                        TargetBuildMvid = MvidOf(reference),
                    };
                    pending.Remove(owner);
                }
            }
        }
        return found.Values;
    }

    /// <summary>
    /// The syntax the compiler discards because a CONSTANT condition makes it unreachable (#7449): the
    /// body of <c>if (false)</c> (the else of <c>if (true)</c>), the right operand of <c>false &amp;&amp; …</c>
    /// and <c>true || …</c>, the dead arm of a constant <c>?:</c>, the body of <c>while (false)</c> and of
    /// <c>for (…; false; …)</c> (with its incrementors). Bound code, so a module change still breaks it;
    /// emitted as nothing, so the image shows no <c>AssemblyRef</c> for what it uses.
    /// </summary>
    /// <remarks>
    /// Only a condition that CAN be constant is bound — one built solely from literals, names, member
    /// accesses, parentheses, casts, <c>default</c> and unary/binary operators (no call, no assignment, no
    /// element access, no pattern) — so the cost stays local bindings, never a second full binding
    /// pass. <paramref name="model"/> is created on first need.
    /// </remarks>
    private static IEnumerable<SyntaxNode> ConstantUnreachableBranches(
        SyntaxNode root, Func<SemanticModel> model, CancellationToken ct)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax @if when ConstantBool(@if.Condition) is { } value:
                    if (!value)
                        yield return @if.Statement;
                    else if (@if.Else is { } @else)
                        yield return @else.Statement;
                    break;
                case WhileStatementSyntax @while when ConstantBool(@while.Condition) == false:
                    yield return @while.Statement;
                    break;
                case ForStatementSyntax @for when @for.Condition is { } condition && ConstantBool(condition) == false:
                    yield return @for.Statement;
                    foreach (var incrementor in @for.Incrementors)
                        yield return incrementor;
                    break;
                case ConditionalExpressionSyntax ternary when ConstantBool(ternary.Condition) is { } value:
                    yield return value ? ternary.WhenFalse : ternary.WhenTrue;
                    break;
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } conjunction
                    when ConstantBool(conjunction.Left) == false:
                    yield return conjunction.Right;
                    break;
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalOrExpression } disjunction
                    when ConstantBool(disjunction.Left) == true:
                    yield return disjunction.Right;
                    break;
            }
        }

        bool? ConstantBool(ExpressionSyntax condition)
        {
            if (!CanBeConstant(condition))
                return null;
            var value = model().GetConstantValue(condition, ct);
            return value is { HasValue: true, Value: bool b } ? b : null;
        }
    }

    /// <summary>Whether <paramref name="expression"/> is built only from shapes a C# constant can take.</summary>
    private static bool CanBeConstant(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf().All(n => n switch
        {
            LiteralExpressionSyntax or IdentifierNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax
                or PredefinedTypeSyntax or ParenthesizedExpressionSyntax or CastExpressionSyntax
                or DefaultExpressionSyntax or CheckedExpressionSyntax or GenericNameSyntax or TypeArgumentListSyntax
                or ArrayTypeSyntax or ArrayRankSpecifierSyntax or OmittedArraySizeExpressionSyntax or NullableTypeSyntax => true,
            MemberAccessExpressionSyntax access => access.IsKind(SyntaxKind.SimpleMemberAccessExpression),
            PrefixUnaryExpressionSyntax => true,
            BinaryExpressionSyntax binary => !binary.IsKind(SyntaxKind.CoalesceExpression)
                                             && !binary.IsKind(SyntaxKind.IsExpression)
                                             && !binary.IsKind(SyntaxKind.AsExpression),
            _ => false,
        });

    /// <summary>The names under <paramref name="ns"/> whose use the compiler can ERASE from the
    /// emitted image: every type name (an attribute type's also without its <c>Attribute</c>
    /// suffix), every constant field (enum members included) and every <c>[Conditional]</c> method,
    /// nested types included.</summary>
    private static void CollectErasableNames(INamespaceSymbol ns, HashSet<string> names, HashSet<string> conditionals,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var member in ns.GetMembers())
            switch (member)
            {
                case INamespaceSymbol inner:
                    CollectErasableNames(inner, names, conditionals, ct);
                    break;
                case INamedTypeSymbol type:
                    CollectErasableNames(type, names, conditionals);
                    break;
            }
    }

    private static void CollectErasableNames(INamedTypeSymbol type, HashSet<string> names, HashSet<string> conditionals)
    {
        names.Add(type.Name);
        if (type.Name.Length > "Attribute".Length && type.Name.EndsWith("Attribute", StringComparison.Ordinal))
            names.Add(type.Name[..^"Attribute".Length]);
        foreach (var member in type.GetMembers())
            if (member is IFieldSymbol { IsConst: true })
                names.Add(member.Name);
            else if (member is IMethodSymbol method && method.GetAttributes().Any(IsConditional))
            {
                names.Add(member.Name);
                conditionals.Add(member.Name);
            }
        foreach (var nested in type.GetTypeMembers())
            CollectErasableNames(nested, names, conditionals);
    }

    /// <summary>The framework types whose <c>[Conditional]</c> methods in-mesh code calls
    /// (<c>Debug.Assert</c> under a compile without <c>DEBUG</c>, …).</summary>
    private static readonly string[] FrameworkConditionalTypes =
    [
        "System.Diagnostics.Debug", "System.Diagnostics.Trace", "System.Diagnostics.Debugger",
        "System.Diagnostics.Contracts.Contract",
    ];

    /// <summary>The names of <c>[Conditional]</c> methods a call in this compilation can bind to
    /// beyond the pending modules': the ones its own source declares (by syntax — no binding) and
    /// the framework's. Names only nominate; the invocation is bound before its arguments count.</summary>
    private static void CollectConditionalCallees(CSharpCompilation compilation, HashSet<string> names, CancellationToken ct)
    {
        // The compilation's own erasing methods are decided by their DECLARED symbol, never the
        // attribute's spelling (an alias, `using C = System.Diagnostics.ConditionalAttribute;`, spells
        // anything): only declarations that carry an attribute or are a body-less partial are bound.
        foreach (var tree in compilation.SyntaxTrees)
        {
            SemanticModel? model = null;
            foreach (var method in tree.GetRoot(ct).DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.AttributeLists.Count == 0
                    && !(method.Modifiers.Any(SyntaxKind.PartialKeyword) && method.Body is null && method.ExpressionBody is null))
                    continue;
                model ??= compilation.GetSemanticModel(tree);
                if (model.GetDeclaredSymbol(method, ct) is { } declared && IsErasingCallee(declared))
                    names.Add(method.Identifier.ValueText);
            }
        }
        foreach (var metadataName in FrameworkConditionalTypes)
            if (compilation.GetTypeByMetadataName(metadataName) is { } type)
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                    if (method.GetAttributes().Any(IsConditional))
                        names.Add(method.Name);
    }

    /// <summary>A method whose CALL the compiler drops together with its arguments and receiver: a
    /// <c>[Conditional]</c> method, or a partial method declared without any implementation.</summary>
    private static bool IsErasingCallee(IMethodSymbol method) =>
        method.GetAttributes().Any(IsConditional)
        || method is { IsPartialDefinition: true, PartialImplementationPart: null };

    private static bool IsConditional(AttributeData attribute) =>
        attribute.AttributeClass is { Name: "ConditionalAttribute" } attributeClass
        && attributeClass.ContainingNamespace.ToDisplayString() == "System.Diagnostics";

    private static List<(AssemblyIdentity Identity, MetadataReference Reference)> ReferenceIdentities(
        CSharpCompilation compilation) =>
        compilation.References
            .Select(r => (Reference: r, Assembly: compilation.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol))
            .Where(x => x.Assembly is not null)
            .Select(x => (x.Assembly!.Identity, x.Reference))
            .ToList();

    private static EmittedAssemblyRef EmittedAssemblyRefOf(
        System.Reflection.Metadata.MetadataReader reader, System.Reflection.Metadata.AssemblyReferenceHandle handle)
    {
        var assemblyRef = reader.GetAssemblyReference(handle);
        return new EmittedAssemblyRef(
            reader.GetString(assemblyRef.Name), assemblyRef.Version, reader.GetString(assemblyRef.Culture),
            [.. reader.GetBlobBytes(assemblyRef.PublicKeyOrToken)],
            (assemblyRef.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0);
    }

    /// <summary>The MODULE edges when no emitted image is at hand: every reference the compilation
    /// USED (<see cref="Compilation.GetUsedAssemblyReferences"/>) that is an installed module, with
    /// the module's MVID. Ordinal by assembly name. Costs a full binding pass over every method body,
    /// which is why every caller that has the emitted image takes <see cref="EmittedModuleEdges"/>.</summary>
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

    /// <summary>
    /// The PLATFORM and PACKAGE edges of an EMITTED assembly, read off its metadata: every
    /// <c>AssemblyRef</c> the compiler wrote, classified by the compile's reference FILES, with the
    /// <c>TypeRef</c>s resolved through it as evidence. Ordered platform, then package; ordinal.
    ///
    /// <para>🚨 <b>Why metadata, not <see cref="Compilation.GetUsedAssemblyReferences"/> and not a
    /// semantic walk.</b> Roslyn's used-reference set counts an assembly whose namespace a
    /// <c>using</c> merely imports, and every NodeType compile carries the skeleton's global usings:
    /// measured on core's doc tree, every type reported the SAME 21 MeshWeaver assemblies — which
    /// answers "who binds MeshWeaver.Graph" with "everyone". A semantic walk that re-binds every
    /// name gives the right set at a price: measured on a 90-file module, 792 ms against 621 ms for
    /// the emit itself, and the runtime compile runs on every portal. The C# compiler writes an
    /// <c>AssemblyRef</c> only for an assembly the IL or metadata actually needs, so reading two
    /// metadata tables gives the bound set for ~50 ms. What metadata cannot show — a constant
    /// inlined from a platform type, a <c>nameof</c> — is not an edge.</para>
    ///
    /// <para><b>Known limit.</b> A NodeType's emitted assembly also carries the foreign owners' code
    /// compiled in through <c>shared=</c>/<c>@@</c>, so a platform assembly only that code binds is
    /// still this type's edge — an extra edge, never a missing one (the direction every limit of this
    /// extractor fails in).</para>
    /// </summary>
    /// <param name="compilation">The compilation that produced the image — its references classify
    /// each <c>AssemblyRef</c> by file.</param>
    /// <param name="image">The emitted assembly's bytes.</param>
    /// <param name="references">What each reference is.</param>
    public static IReadOnlyList<DependencyEdge> EmittedReferenceEdges(
        CSharpCompilation compilation, Stream image, DependencyReferenceClasses references)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(references);
        var identities = ReferenceIdentities(compilation);

        using var pe = new System.Reflection.PortableExecutable.PEReader(image, System.Reflection.PortableExecutable.PEStreamOptions.LeaveOpen);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var typesByScope = new Dictionary<System.Reflection.Metadata.AssemblyReferenceHandle, ImmutableSortedSet<string>.Builder>();
        foreach (var handle in reader.TypeReferences)
        {
            var typeRef = reader.GetTypeReference(handle);
            if (typeRef.ResolutionScope.Kind != System.Reflection.Metadata.HandleKind.AssemblyReference)
                continue;
            var scope = (System.Reflection.Metadata.AssemblyReferenceHandle)typeRef.ResolutionScope;
            if (!typesByScope.TryGetValue(scope, out var names))
                typesByScope[scope] = names = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
            var ns = reader.GetString(typeRef.Namespace);
            names.Add(ns.Length == 0 ? reader.GetString(typeRef.Name) : $"{ns}.{reader.GetString(typeRef.Name)}");
        }

        var platform = new SortedDictionary<string, DependencyEdge>(StringComparer.Ordinal);
        var packages = new SortedDictionary<string, ImmutableSortedSet<string>>(StringComparer.Ordinal);
        foreach (var handle in reader.AssemblyReferences)
        {
            var emitted = EmittedAssemblyRefOf(reader, handle);
            var name = emitted.Name;
            // By FULL identity first; ambiguous ⇒ every candidate (an extra edge, never a lost one).
            foreach (var reference in ReferencesFor(emitted, identities))
            {
                if (references.Modules.Contains(reference))
                    continue;
                if (references.Packages.TryGetValue(reference, out var packageId))
                    packages[packageId] = packages.TryGetValue(packageId, out var names)
                        ? names.Add(name)
                        : ImmutableSortedSet.Create(StringComparer.Ordinal, name);
                else if (DependencyReferenceClasses.IsPlatformName(name))
                    platform[name] = new DependencyEdge
                    {
                        Target = name,
                        Kind = DependencyEdgeKinds.Platform,
                        TargetBuildMvid = MvidOf(reference),
                        Evidence = typesByScope.TryGetValue(handle, out var types) ? [.. types.Take(MaxEvidence)] : [],
                    };
            }
        }
        return [
            .. platform.Values,
            .. packages.Select(p => new DependencyEdge
            {
                Target = DependencyEdgeTargets.ForPackage(p.Key),
                Kind = DependencyEdgeKinds.Package,
                Evidence = [.. p.Value.Take(MaxEvidence)],
            }),
        ];
    }

    /// <summary>An <c>AssemblyRef</c> as the emitted metadata spells it.</summary>
    /// <param name="Name">The simple name.</param>
    /// <param name="Version">The version.</param>
    /// <param name="Culture">The culture name, empty for neutral.</param>
    /// <param name="PublicKeyOrToken">The public key or its token, empty when unsigned.</param>
    /// <param name="IsFullKey">Whether <paramref name="PublicKeyOrToken"/> is the full key.</param>
    public sealed record EmittedAssemblyRef(
        string Name, Version Version, string Culture, ImmutableArray<byte> PublicKeyOrToken, bool IsFullKey);

    /// <summary>
    /// The compilation references an emitted <c>AssemblyRef</c> names, matched by FULL identity —
    /// name, version, culture and public key (or token) — because a host assembly and a package
    /// assembly may share a simple name with different publishers and BOTH stay referenced
    /// (<c>CompileReferences.WithPackageAssemblies</c>); a name-only lookup would classify the
    /// package reference as the host's (or as a module) and lose its edge. When no candidate matches
    /// the full identity, EVERY candidate of that name is returned — an extra edge, never a lost one.
    /// </summary>
    /// <param name="emitted">The emitted reference.</param>
    /// <param name="references">The compilation's references with their identities.</param>
    public static IReadOnlyList<MetadataReference> ReferencesFor(
        EmittedAssemblyRef emitted, IReadOnlyList<(AssemblyIdentity Identity, MetadataReference Reference)> references)
    {
        ArgumentNullException.ThrowIfNull(emitted);
        ArgumentNullException.ThrowIfNull(references);
        var candidates = references
            .Where(r => string.Equals(r.Identity.Name, emitted.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var exact = candidates.Where(r =>
                r.Identity.Version == emitted.Version
                && string.Equals(r.Identity.CultureName ?? string.Empty, emitted.Culture, StringComparison.OrdinalIgnoreCase)
                && (emitted.PublicKeyOrToken.IsDefaultOrEmpty
                    ? r.Identity.PublicKeyToken.IsDefaultOrEmpty
                    : (emitted.IsFullKey ? r.Identity.PublicKey : r.Identity.PublicKeyToken)
                        .SequenceEqual(emitted.PublicKeyOrToken)))
            .ToList();
        return [.. (exact.Count > 0 ? exact : candidates).Select(r => r.Reference).Distinct()];
    }

    /// <summary>The platform and package edges of an emitted assembly ON DISK — see
    /// <see cref="EmittedReferenceEdges(CSharpCompilation, Stream, DependencyReferenceClasses)"/>.</summary>
    /// <param name="compilation">The compilation that produced the assembly.</param>
    /// <param name="assemblyPath">The emitted assembly.</param>
    /// <param name="references">What each reference is.</param>
    public static IReadOnlyList<DependencyEdge> EmittedReferenceEdges(
        CSharpCompilation compilation, string assemblyPath, DependencyReferenceClasses references)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);
        using var stream = File.OpenRead(assemblyPath);
        return EmittedReferenceEdges(compilation, stream, references);
    }

    /// <summary>
    /// The set's edges, PROVEN to have unique targets — the invariant every target-keyed reader (and
    /// the Postgres projection's <c>(source, target)</c> key) relies on. Disjoint by construction
    /// (<see cref="DependencyEdgeTargets"/>); a collision here is an extractor defect and fails LOUDLY
    /// (the runtime compile logs it at Error and the build carries no edge set — "not extracted",
    /// never a set that silently lost a dependency).
    /// </summary>
    /// <param name="source">The source, for the message.</param>
    /// <param name="edges">The edges.</param>
    public static ImmutableList<DependencyEdge> UniqueTargets(string source, ImmutableList<DependencyEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var collision = edges.GroupBy(e => e.Target, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (collision is not null)
            throw new InvalidOperationException(
                $"dependency edges of '{source}': target '{collision.Key}' is claimed by "
                + string.Join(" and ", collision.Select(e => e.Kind)) + " — targets must be unique per set");
        return edges;
    }

    internal static string? MvidOf(MetadataReference reference)
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
    internal static bool ContainsSelf(ImmutableSortedSet<string> owners, string selfPath) =>
        owners.Any(o => IsSelf(o, selfPath));
}
