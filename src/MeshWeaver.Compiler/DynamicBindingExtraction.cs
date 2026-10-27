using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace MeshWeaver.Compiler;

/// <summary>
/// The DYNAMIC half of the forward dependency edges (<c>Doc/Architecture/DependencyGraph</c> →
/// <i>Dynamic edges</i>): every reflection call that binds a type or an assembly BY NAME at run time,
/// which no <see cref="Compilation.GetUsedAssemblyReferences"/> and no symbol walk can see —
/// <c>Type.GetType("MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph")</c> references nothing
/// the compiler binds, and still breaks the day <c>HomeAppSource</c> leaves <c>MeshWeaver.Graph</c>.
///
/// <para><b>What is scanned</b> — the APIs core and its content resolve types and assemblies by name
/// through (<see cref="Apis"/>): <c>Type.GetType</c>, <c>Assembly.GetType</c>,
/// <c>Module.GetType</c>, <c>Assembly.CreateInstance</c>, <c>Activator.CreateInstance(string, string…)</c>
/// and <c>CreateInstanceFrom</c>, <c>AppDomain.CreateInstance*</c>, <c>Assembly.Load</c> /
/// <c>LoadFrom</c> / <c>LoadFile</c> / <c>LoadWithPartialName</c> / <c>UnsafeLoadFrom</c>,
/// <c>AssemblyLoadContext.LoadFromAssemblyName</c> / <c>LoadFromAssemblyPath</c> /
/// <c>LoadFromNativeImagePath</c> / <c>LoadFromStream</c>, and the mesh's own name-to-type surface,
/// <c>ITypeRegistry.GetType</c> / <c>TryGetType</c> (any implementation). A call is matched by its
/// BOUND method, so an alias or a <c>using static</c> cannot hide it, and a method group passed
/// along (<c>names.Select(Type.GetType)</c>) is recorded as unresolved.</para>
///
/// <para><b>Resolved or not.</b> A constant argument (a literal, a <c>const</c>, a constant
/// interpolation, a <c>new AssemblyName("…")</c> over one) is a
/// <see cref="DependencyEdgeKinds.Dynamic"/> edge to an assembly-qualified pattern
/// <c>{type}, {assembly}</c>, with what the assembly IS in <see cref="DependencyEdge.TargetKind"/>.
/// Anything else is a <see cref="DependencyEdgeKinds.DynamicUnresolved"/> edge — one per API, the
/// call sites as evidence. Unknown is recorded as unknown; nothing is dropped.</para>
///
/// <para><b>Cost.</b> A syntactic prefilter on the invoked NAME (<see cref="CandidateNames"/>)
/// means only candidate invocations are ever bound; a compile with no reflection pays one syntax
/// walk per tree and no semantic model at all.</para>
/// </summary>
public static class DynamicBindingExtraction
{
    /// <summary>The most call sites one dynamic edge keeps as evidence.</summary>
    public const int MaxEvidence = DependencyEdgeExtraction.MaxEvidence;

    /// <summary>How one API's arguments name its target.</summary>
    private enum Shape
    {
        /// <summary>Parameter <c>typeName</c> (or the first string) is a type name, possibly assembly-qualified.</summary>
        TypeName,
        /// <summary>A type name, looked up in the RECEIVER assembly (<c>Assembly.GetType</c>).</summary>
        TypeNameInReceiver,
        /// <summary><c>assemblyName</c> + <c>typeName</c> (<c>Activator.CreateInstance(string, string)</c>).</summary>
        AssemblyAndType,
        /// <summary><c>assemblyFile</c> + <c>typeName</c>.</summary>
        FileAndType,
        /// <summary>An assembly name (a string or an <c>AssemblyName</c>).</summary>
        AssemblyName,
        /// <summary>An assembly file path.</summary>
        AssemblyFile,
        /// <summary>Bytes or a stream: never resolvable.</summary>
        Opaque,
        /// <summary>A type-registry name.</summary>
        RegistryName,
    }

    /// <summary>The API table, keyed by <c>{containing type}.{method}</c> — read-only, never written
    /// at run time.</summary>
    private static readonly ImmutableDictionary<string, Shape> Apis = new Dictionary<string, Shape>
    {
        ["System.Type.GetType"] = Shape.TypeName,
        ["System.Reflection.Assembly.GetType"] = Shape.TypeNameInReceiver,
        ["System.Reflection.Module.GetType"] = Shape.TypeNameInReceiver,
        ["System.Reflection.Assembly.CreateInstance"] = Shape.TypeNameInReceiver,
        ["System.Activator.CreateInstance"] = Shape.AssemblyAndType,
        ["System.Activator.CreateInstanceFrom"] = Shape.FileAndType,
        ["System.AppDomain.CreateInstance"] = Shape.AssemblyAndType,
        ["System.AppDomain.CreateInstanceAndUnwrap"] = Shape.AssemblyAndType,
        ["System.AppDomain.CreateInstanceFrom"] = Shape.FileAndType,
        ["System.AppDomain.CreateInstanceFromAndUnwrap"] = Shape.FileAndType,
        ["System.Reflection.Assembly.Load"] = Shape.AssemblyName,
        ["System.Reflection.Assembly.LoadWithPartialName"] = Shape.AssemblyName,
        ["System.Reflection.Assembly.LoadFrom"] = Shape.AssemblyFile,
        ["System.Reflection.Assembly.LoadFile"] = Shape.AssemblyFile,
        ["System.Reflection.Assembly.UnsafeLoadFrom"] = Shape.AssemblyFile,
        ["System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyName"] = Shape.AssemblyName,
        ["System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath"] = Shape.AssemblyFile,
        ["System.Runtime.Loader.AssemblyLoadContext.LoadFromNativeImagePath"] = Shape.AssemblyFile,
        ["System.Runtime.Loader.AssemblyLoadContext.LoadFromStream"] = Shape.Opaque,
        [RegistryInterface + ".GetType"] = Shape.RegistryName,
        [RegistryInterface + ".TryGetType"] = Shape.RegistryName,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>The mesh's name-to-type surface; an implementation's method is normalised to it.</summary>
    private const string RegistryInterface = "MeshWeaver.Domain.ITypeRegistry";

    /// <summary>The invoked simple names worth binding — the syntactic prefilter.</summary>
    public static readonly ImmutableHashSet<string> CandidateNames = Apis.Keys
        .Select(k => k[(k.LastIndexOf('.') + 1)..])
        .ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The dynamic edges of a whole compilation (a module workspace build: every tree is the
    /// compiling project's own code).
    /// </summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="trees">The trees to scan.</param>
    /// <param name="references">What each reference is, to say what a resolved target IS.</param>
    /// <param name="ct">Cancellation.</param>
    public static IReadOnlyList<DependencyEdge> Extract(
        CSharpCompilation compilation, IEnumerable<SyntaxTree> trees, DependencyReferenceClasses references,
        CancellationToken ct = default) =>
        Extract(compilation, trees, references, _ => true, ct);

    /// <summary>
    /// The dynamic edges of the call sites <paramref name="isOwnCode"/> accepts, ordered by kind then
    /// target (ordinal); targets are unique.
    /// </summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="trees">The trees to scan.</param>
    /// <param name="references">What each reference is.</param>
    /// <param name="isOwnCode">Whether a call site is the compiling source's own code.</param>
    /// <param name="ct">Cancellation.</param>
    internal static IReadOnlyList<DependencyEdge> Extract(
        CSharpCompilation compilation, IEnumerable<SyntaxTree> trees, DependencyReferenceClasses references,
        Func<SyntaxNode, bool> isOwnCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(references);
        var resolved = new SortedDictionary<string, (string? TargetKind, ImmutableSortedSet<string> Evidence)>(StringComparer.Ordinal);
        var unresolved = new SortedDictionary<string, ImmutableSortedSet<string>>(StringComparer.Ordinal);
        AssemblyLookup? assemblies = null;

        foreach (var tree in trees)
        {
            SemanticModel? model = null;
            foreach (var name in tree.GetRoot(ct).DescendantNodes().OfType<SimpleNameSyntax>())
            {
                ct.ThrowIfCancellationRequested();
                if (!CandidateNames.Contains(name.Identifier.ValueText) || !IsInvokedOrReferencedName(name))
                    continue;
                // `x.GetType()` — the ubiquitous parameterless object.GetType — is never a candidate.
                var invocation = InvocationOf(name);
                if (invocation is { ArgumentList.Arguments.Count: 0 })
                    continue;
                if (!isOwnCode(name))
                    continue;
                model ??= compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                var symbolInfo = model.GetSymbolInfo(name, ct);
                if ((symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol method
                    || ApiOf(method) is not { } api)
                    continue;
                // Only the overloads that bind BY NAME: CreateInstance(Type) and Load(byte[]) name
                // their target statically (or not at all) and are not this edge.
                if (!IsByNameOverload(api.Shape, method))
                    continue;
                var site = SiteOf(name);
                if (invocation is null)
                {
                    // A method group handed along: its argument is whatever the receiver passes.
                    if (!IsInsideNameOf(name))
                        Add(unresolved, "?" + api.Key, $"{api.Display} (method group) in {site}");
                    continue;
                }
                if (model.GetOperation(invocation, ct) is not IInvocationOperation operation)
                    continue;
                assemblies ??= new AssemblyLookup(compilation, references);
                var target = Resolve(api.Shape, operation, compilation, assemblies);
                if (target is { } t)
                {
                    var evidence = $"{api.Display}(\"{t.Literal}\") in {site}";
                    resolved[t.Target] = resolved.TryGetValue(t.Target, out var existing)
                        ? (existing.TargetKind, existing.Evidence.Add(evidence))
                        : (t.TargetKind, ImmutableSortedSet.Create(StringComparer.Ordinal, evidence));
                }
                else
                    Add(unresolved, "?" + api.Key, $"{api.Display}({Shorten(invocation.ArgumentList.Arguments.ToString())}) in {site}");
            }
        }

        return [
            .. resolved.Select(r => new DependencyEdge
            {
                Target = r.Key,
                Kind = DependencyEdgeKinds.Dynamic,
                EvidenceClass = DependencyEvidenceClasses.Dynamic,
                TargetKind = r.Value.TargetKind,
                Evidence = [.. r.Value.Evidence.Take(MaxEvidence)],
            }),
            .. unresolved.Select(u => new DependencyEdge
            {
                Target = u.Key,
                Kind = DependencyEdgeKinds.DynamicUnresolved,
                EvidenceClass = DependencyEvidenceClasses.Dynamic,
                Evidence = [.. u.Value.Take(MaxEvidence)],
            }),
        ];
    }

    private static void Add(SortedDictionary<string, ImmutableSortedSet<string>> into, string key, string evidence) =>
        into[key] = into.TryGetValue(key, out var existing)
            ? existing.Add(evidence)
            : ImmutableSortedSet.Create(StringComparer.Ordinal, evidence);

    /// <summary>The name is the invoked member of an invocation, or a method group (a member
    /// access or a bare name not being declared).</summary>
    private static bool IsInvokedOrReferencedName(SimpleNameSyntax name) =>
        name.Parent is MemberAccessExpressionSyntax access ? access.Name == name : name.Parent is not NameColonSyntax;

    /// <summary>The invocation whose callee IS this name, or null for a method group.</summary>
    private static InvocationExpressionSyntax? InvocationOf(SimpleNameSyntax name)
    {
        SyntaxNode callee = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
        if (callee.Parent is MemberBindingExpressionSyntax binding)
            callee = binding;
        return callee.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee
            ? invocation
            : null;
    }

    private sealed record Api(string Key, string Display, Shape Shape);

    private static Api? ApiOf(IMethodSymbol method)
    {
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        var key = $"{FullName(original.ContainingType)}.{original.Name}";
        if (Apis.TryGetValue(key, out var shape))
            return new Api(key, $"{original.ContainingType.Name}.{original.Name}", shape);
        // An ITypeRegistry implementation (or a derived interface) is the registry surface. Matched
        // by NAME and POSITION, never by parameter name: an implementation may legally rename the
        // parameter (`GetType(string typeName)`), and a rename must not take a call off the ratchet.
        if (original.Name is "GetType" or "TryGetType"
            && original.Parameters.Length > 0
            && original.Parameters[0].Type.SpecialType == SpecialType.System_String
            && original.ContainingType.AllInterfaces.Append(original.ContainingType)
                .Any(i => FullName(i) == RegistryInterface)
            && Apis.TryGetValue(RegistryInterface + "." + original.Name, out var registryShape))
            return new Api(RegistryInterface + "." + original.Name, $"ITypeRegistry.{original.Name}", registryShape);
        return null;
    }

    private static string FullName(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? $"{ns.ToDisplayString()}.{type.Name}"
            : type.Name;

    /// <summary>Whether this overload names its target by a string (or an <c>AssemblyName</c>, bytes
    /// or a stream) — as opposed to an overload of the same name that takes a <see cref="System.Type"/>.</summary>
    private static bool IsByNameOverload(Shape shape, IMethodSymbol method)
    {
        bool Has(params string[] names) => method.Parameters.Any(p => names.Contains(p.Name, StringComparer.Ordinal));
        return shape switch
        {
            Shape.TypeName => Has("typeName"),
            Shape.TypeNameInReceiver => Has("name", "typeName", "className"),
            Shape.AssemblyAndType => Has("assemblyName") && Has("typeName"),
            Shape.FileAndType => Has("assemblyFile") && Has("typeName"),
            // Position, not name — ApiOf already required a leading string parameter.
            Shape.RegistryName => method.Parameters.Length > 0 && method.Parameters[0].Type.SpecialType == SpecialType.System_String,
            _ => true,
        };
    }

    private static bool IsInsideNameOf(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

    private readonly record struct Resolution(string Target, string? TargetKind, string Literal);


    private static Resolution? Resolve(
        Shape shape, IInvocationOperation operation, CSharpCompilation compilation,
        AssemblyLookup assemblies)
    {
        string? Arg(params string[] names)
        {
            foreach (var argument in operation.Arguments)
                if (argument.Parameter is { } p && names.Contains(p.Name, StringComparer.Ordinal))
                    return ConstantString(argument.Value);
            return null;
        }
        string? ArgAt(int ordinal)
        {
            foreach (var argument in operation.Arguments)
                if (argument.Parameter is { } p && p.Ordinal == ordinal)
                    return ConstantString(argument.Value);
            return null;
        }

        switch (shape)
        {
            case Shape.TypeName:
                return Arg("typeName") is { } typeName
                    ? ForTypeName(typeName, Receiver.None, compilation, assemblies)
                    : null;
            case Shape.TypeNameInReceiver:
                return Arg("name", "typeName", "className") is { } inReceiver
                    ? ForTypeName(inReceiver, ReceiverOf(operation.Instance), compilation, assemblies)
                    : null;
            case Shape.AssemblyAndType:
            {
                if (Arg("assemblyName") is not { } assembly || Arg("typeName") is not { } type)
                    return null;
                return ForTypeName(type, Receiver.Of(SimpleAssemblyName(assembly)), compilation, assemblies)
                    with { Literal = $"{assembly}\", \"{type}" };
            }
            case Shape.FileAndType:
            {
                if (Arg("assemblyFile") is not { } file || Arg("typeName") is not { } type)
                    return null;
                return ForTypeName(type, Receiver.Of(Path.GetFileNameWithoutExtension(file.Replace('\\', '/'))),
                        compilation, assemblies)
                    with { Literal = $"{file}\", \"{type}" };
            }
            case Shape.AssemblyName:
            {
                if (Arg("assemblyString", "assemblyRef", "partialName", "assemblyName") is not { } assembly)
                    return null;
                var name = SimpleAssemblyName(assembly);
                return new Resolution($"*, {name}", assemblies.KindOf(name), assembly);
            }
            case Shape.AssemblyFile:
            {
                if (Arg("assemblyFile", "path", "assemblyPath", "nativeImagePath") is not { } file)
                    return null;
                var name = Path.GetFileNameWithoutExtension(file.Replace('\\', '/'));
                return new Resolution($"*, {name}", assemblies.KindOf(name), file);
            }
            case Shape.RegistryName:
            {
                // By POSITION (the interface's first parameter), whatever the implementation calls it.
                if (ArgAt(0) is not { } registered)
                    return null;
                // A registry name is whatever some hub registered it under: the assembly is not
                // knowable at compile time, so the target kind stays unknown.
                return new Resolution($"{registered}, ?", null, registered);
            }
            default:
                return null;
        }
    }

    /// <summary>Where a type name is looked up when it carries no assembly of its own: nowhere
    /// special (<c>Type.GetType</c> — the calling assembly and CoreLib), a STATICALLY known assembly,
    /// or an assembly the compile cannot know (an arbitrary <c>Assembly</c> instance, <c>typeof(T)</c>
    /// of a type parameter).</summary>
    private readonly record struct Receiver(bool Known, string? Assembly)
    {
        public static Receiver None => new(true, null);
        public static Receiver Unknown => new(false, null);
        public static Receiver Of(string assembly) => new(true, assembly);
    }

    /// <summary>
    /// Resolves a CONSTANT type name — conservatively. 🚨 <b>Only a FULLY understood name may leave
    /// the ratchet.</b> The name is parsed with a strict grammar (<see cref="TryParseTypeName"/>); a
    /// name that does not parse, a part whose assembly the compile cannot classify, or a lookup in an
    /// assembly the compile cannot know makes the whole target UNKNOWN (<c>TargetKind</c> null), which
    /// counts as into core exactly like a <see cref="DependencyEdgeKinds.DynamicUnresolved"/> edge.
    /// Reflection's name grammar and the shapes a lookup can take are open-ended; classifying them
    /// completely would never converge, so the scanner is complete in the other direction — whatever
    /// it does not fully resolve, it counts.
    /// </summary>
    private static Resolution ForTypeName(
        string typeName, Receiver receiver, CSharpCompilation compilation, AssemblyLookup assemblies)
    {
        if (!TryParseTypeName(typeName, out var parsed, out var ownAssembly))
        {
            var (raw, rawAssembly) = SplitAssemblyQualifiedName(typeName);
            return new Resolution($"{raw}, {rawAssembly ?? receiver.Assembly ?? "?"}", null, typeName);
        }
        string? outerAssembly;
        string? outerKind;
        if (ownAssembly is not null)
            (outerAssembly, outerKind) = (ownAssembly, assemblies.KindOf(ownAssembly));
        else if (!receiver.Known)
            (outerAssembly, outerKind) = (null, null);
        else if (receiver.Assembly is { } known)
            (outerAssembly, outerKind) = (known, assemblies.KindOf(known));
        else if (Resolved(parsed!.Name, compilation, assemblies) is { } outer)
            (outerAssembly, outerKind) = (outer.Assembly, outer.Kind);
        else
            (outerAssembly, outerKind) = (null, null);
        var typePart = SplitAssemblyQualifiedName(typeName).Type;
        return new Resolution($"{typePart}, {outerAssembly ?? "?"}",
            Combine(outerKind, parsed!, compilation, assemblies), typeName);
    }

    /// <summary>The assembly and kind the compilation resolves an UNQUALIFIED metadata name to, or
    /// null when it resolves none.</summary>
    private static (string Assembly, string? Kind)? Resolved(string metadataName, CSharpCompilation compilation, AssemblyLookup assemblies)
    {
        if (compilation.GetTypeByMetadataName(metadataName) is not { } symbol)
            return null;
        if (SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
            return (compilation.AssemblyName ?? "?", DependencyEdgeKinds.NodeType);
        var name = symbol.ContainingAssembly.Identity.Name;
        return (name, assemblies.KindOf(name));
    }

    /// <summary>
    /// The target kind of a parsed name INCLUDING every generic argument at every depth: a closed
    /// generic needs each argument's assembly to resolve, so
    /// <c>List`1[[MeshWeaver.Mesh.MeshNode, MeshWeaver.Mesh.Contract]], System.Private.CoreLib</c>
    /// reaches into the platform although its outer type is the framework's. Unknown anywhere ⇒
    /// unknown (null); otherwise the platform anywhere ⇒ <see cref="DependencyEdgeKinds.Platform"/>;
    /// otherwise the outer kind.
    /// </summary>
    private static string? Combine(
        string? outerKind, ParsedTypeName parsed, CSharpCompilation compilation, AssemblyLookup assemblies)
    {
        var kinds = new List<string?> { outerKind };
        foreach (var argument in parsed.Arguments)
        {
            var argumentKind = argument.Assembly is { } qualified
                ? assemblies.KindOf(qualified)
                // An unqualified argument resolves like Type.GetType would: the calling assembly or CoreLib.
                : Resolved(argument.Type.Name, compilation, assemblies)?.Kind;
            kinds.Add(Combine(argumentKind, argument.Type, compilation, assemblies));
        }
        return kinds.Any(k => k is null) ? null
            : kinds.Contains(DependencyEdgeKinds.Platform) ? DependencyEdgeKinds.Platform
            : outerKind;
    }

    /// <summary>A parsed type name: its metadata name (with arity, without arguments or array
    /// suffixes) and its generic arguments.</summary>
    /// <param name="Name">The metadata name of the type definition.</param>
    /// <param name="Arguments">The generic arguments, in order.</param>
    public sealed record ParsedTypeName(string Name, ImmutableArray<ParsedTypeArgument> Arguments);

    /// <summary>One generic argument: its type, and the assembly simple name when it is
    /// assembly-qualified (<c>[T, Asm]</c>), null when it is bare (<c>T</c>).</summary>
    /// <param name="Type">The argument's type.</param>
    /// <param name="Assembly">The assembly's simple name, or null.</param>
    public sealed record ParsedTypeArgument(ParsedTypeName Type, string? Assembly);

    /// <summary>
    /// Parses a reflection type name with a STRICT grammar — false for anything it does not fully
    /// understand, which the caller turns into "unknown", never into a classification:
    /// <code>
    /// AQN      := TypeName [ ',' AssemblyDisplayName ]
    /// TypeName := Name [ '[' Arg { ',' Arg } ']' ] { '[' { ',' | '*' } ']' | '&amp;' | '*' }
    /// Arg      := '[' TypeName [ ',' AssemblyDisplayName ] ']'  |  TypeName
    /// </code>
    /// Mixed qualified and bare arguments (<c>Dictionary`2[System.String,[T, Asm]]</c>) parse; an
    /// escape character, an unbalanced bracket or a stray token does not.
    /// </summary>
    /// <param name="name">The name as written.</param>
    /// <param name="parsed">The parsed type name.</param>
    /// <param name="assembly">The outer assembly's simple name, or null when unqualified.</param>
    public static bool TryParseTypeName(string name, out ParsedTypeName? parsed, out string? assembly)
    {
        ArgumentNullException.ThrowIfNull(name);
        parsed = null;
        assembly = null;
        if (name.Contains('\\'))
            return false;
        var i = 0;
        if (!TryParseType(name, ref i, out parsed))
            return false;
        SkipSpaces(name, ref i);
        if (i == name.Length)
            return true;
        if (name[i] != ',')
            return false;
        var display = name[(i + 1)..];
        if (display.IndexOfAny(['[', ']']) >= 0)
            return false;
        assembly = SimpleAssemblyName(display);
        return assembly.Length > 0;
    }

    private static bool TryParseType(string s, ref int i, out ParsedTypeName? parsed)
    {
        parsed = null;
        SkipSpaces(s, ref i);
        var start = i;
        while (i < s.Length && s[i] is not ('[' or ']' or ',' or '&' or '*'))
            i++;
        var name = s[start..i].Trim();
        if (name.Length == 0)
            return false;
        var arguments = ImmutableArray.CreateBuilder<ParsedTypeArgument>();
        // A generic argument list: '[' followed by something that is not an array rank.
        if (i < s.Length && s[i] == '[' && i + 1 < s.Length && s[i + 1] is not (']' or ',' or '*'))
        {
            i++;
            while (true)
            {
                SkipSpaces(s, ref i);
                if (i >= s.Length)
                    return false;
                if (s[i] == '[')
                {
                    i++;
                    if (!TryParseType(s, ref i, out var qualified))
                        return false;
                    SkipSpaces(s, ref i);
                    string? argumentAssembly = null;
                    if (i < s.Length && s[i] == ',')
                    {
                        var close = s.IndexOf(']', i);
                        var open = s.IndexOf('[', i);
                        if (close < 0 || (open >= 0 && open < close))
                            return false;
                        argumentAssembly = SimpleAssemblyName(s[(i + 1)..close]);
                        if (argumentAssembly.Length == 0)
                            return false;
                        i = close;
                    }
                    if (i >= s.Length || s[i] != ']')
                        return false;
                    i++;
                    arguments.Add(new ParsedTypeArgument(qualified!, argumentAssembly));
                }
                else
                {
                    if (!TryParseType(s, ref i, out var bare))
                        return false;
                    arguments.Add(new ParsedTypeArgument(bare!, null));
                }
                SkipSpaces(s, ref i);
                if (i >= s.Length)
                    return false;
                if (s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (s[i] == ']')
                {
                    i++;
                    break;
                }
                return false;
            }
        }
        // Array ranks, by-ref and pointer suffixes.
        while (i < s.Length)
        {
            if (s[i] is '&' or '*')
            {
                i++;
                continue;
            }
            if (s[i] != '[')
                break;
            var j = i + 1;
            while (j < s.Length && s[j] is ',' or '*' or ' ')
                j++;
            if (j >= s.Length || s[j] != ']')
                return false;
            i = j + 1;
        }
        parsed = new ParsedTypeName(name, arguments.ToImmutable());
        return true;
    }

    private static void SkipSpaces(string s, ref int i)
    {
        while (i < s.Length && s[i] == ' ')
            i++;
    }

    /// <summary>The assembly a lookup on <paramref name="instance"/> searches. Known only for
    /// <c>typeof(X).Assembly</c> where <c>X</c> is a STATICALLY known type; <c>typeof(T)</c> of a type
    /// parameter (its runtime argument decides), any other <c>Assembly</c> value, and anything this
    /// does not recognise are UNKNOWN — never a fall-back to the compilation's own lookup.</summary>
    private static Receiver ReceiverOf(IOperation? instance)
    {
        if (Unwrap(instance) is not IPropertyReferenceOperation { Property.Name: "Assembly" } property
            || Unwrap(property.Instance) is not ITypeOfOperation typeOf)
            return Receiver.Unknown;
        var operand = typeOf.TypeOperand;
        while (operand is IArrayTypeSymbol or IPointerTypeSymbol)
            operand = operand is IArrayTypeSymbol array ? array.ElementType : ((IPointerTypeSymbol)operand).PointedAtType;
        return operand is INamedTypeSymbol { TypeKind: not TypeKind.Error } named
               && named.ContainingAssembly is { } containing
            ? Receiver.Of(containing.Identity.Name)
            : Receiver.Unknown;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;
        return operation;
    }

    /// <summary>A constant string — directly, through a conversion, or as the one constant argument
    /// of <c>new AssemblyName("…")</c>.</summary>
    private static string? ConstantString(IOperation value)
    {
        var unwrapped = Unwrap(value);
        if (unwrapped?.ConstantValue is { HasValue: true, Value: string s } && s.Length > 0)
            return s;
        if (unwrapped is IObjectCreationOperation { Constructor: { } ctor } creation
            && ctor.ContainingType.ToDisplayString() == "System.Reflection.AssemblyName"
            && creation.Arguments.Length == 1)
            return ConstantString(creation.Arguments[0].Value);
        return null;
    }

    /// <summary>Splits an assembly-qualified type name at its first top-level comma:
    /// <c>Ns.T`1[[A, B]], Asm, Version=…</c> ⇒ (<c>Ns.T`1[[A, B]]</c>, <c>Asm</c>).</summary>
    /// <param name="name">The type name as written.</param>
    public static (string Type, string? Assembly) SplitAssemblyQualifiedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '[': depth++; break;
                case ']': depth--; break;
                case ',' when depth == 0:
                    var assembly = SimpleAssemblyName(name[(i + 1)..]);
                    return (name[..i].Trim(), assembly.Length == 0 ? null : assembly);
            }
        }
        return (name.Trim(), null);
    }

    /// <summary>The simple name of an assembly display name (<c>Asm, Version=1.0</c> ⇒ <c>Asm</c>).</summary>
    private static string SimpleAssemblyName(string displayName)
    {
        var comma = displayName.IndexOf(',');
        return (comma < 0 ? displayName : displayName[..comma]).Trim();
    }

    private static string SiteOf(SyntaxNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (parent is BaseTypeDeclarationSyntax && DependencyEdgeExtraction.TypeDeclarationName(parent) is { } type)
                return type;
        return "<top level>";
    }

    private static string Shorten(string text)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()));
        return flat.Length <= 80 ? flat : flat[..77] + "...";
    }

    /// <summary>What an assembly simple name IS in this compile's reference set — module, package,
    /// MeshWeaver platform, the compilation itself, or the framework. Null when the compile does not
    /// reference it at all and its name is a platform name: it may well be the platform (a
    /// carve-out moved it out of the reference set), so a reader must not read it as harmless.</summary>
    private sealed class AssemblyLookup(CSharpCompilation compilation, DependencyReferenceClasses classes)
    {
        private readonly ImmutableDictionary<string, string> kinds = Build(compilation, classes);

        private static ImmutableDictionary<string, string> Build(
            CSharpCompilation compilation, DependencyReferenceClasses classes)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                    continue;
                var name = assembly.Identity.Name;
                builder[name] = classes.Modules.Contains(reference) ? DependencyEdgeKinds.Module
                    : classes.Packages.ContainsKey(reference) ? DependencyEdgeKinds.Package
                    : DependencyReferenceClasses.IsPlatformName(name) ? DependencyEdgeKinds.Platform
                    : DependencyEdgeKinds.Framework;
            }
            builder[compilation.AssemblyName ?? string.Empty] = DependencyEdgeKinds.NodeType;
            return builder.ToImmutable();
        }

        // Framework only for an assembly the compile REFERENCES, or one of the runtime's core names
        // a reference set (built from reference assemblies) never carries. Anything else the compile
        // does not reference is unknown — a name that merely looks like the framework is not proof.
        public string? KindOf(string assemblyName) =>
            kinds.TryGetValue(assemblyName, out var kind) ? kind
            : assemblyName is "mscorlib" or "netstandard" or "System.Private.CoreLib" or "System.Runtime"
                ? DependencyEdgeKinds.Framework
                : null;
    }
}
