using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Reads XML-documentation summaries for types and members — the ONLY sanctioned way to read doc
/// summaries anywhere in the platform. See <c>Doc/Architecture/XmlDocumentationReader</c>.
///
/// <para><b>Owned by the mesh.</b> One instance per mesh, created by the root
/// <see cref="TypeRegistry"/> of the mesh's registry chain alongside its
/// <see cref="TypeDisplayMetadataCache"/> and handed down to every hub's registry. Reach it with
/// <c>hub.XmlDocs()</c> or <c>typeRegistry.XmlDocs()</c>; never hold one in a <c>static</c>
/// (<c>Doc/Architecture/NoStaticState</c>): a snapshot — or a cached MISS — would then outlive the
/// mesh and be seen by the next one.</para>
///
/// <para><b>Where the docs come from.</b> The compiler's <c>{AssemblyName}.xml</c> beside
/// <see cref="Assembly.Location"/>; for an assembly with no location (loaded from a stream, or a
/// single-file host) the application base directory and the current directory, each also under
/// <c>bin/</c>. Compiled NodeTypes are emitted as <c>{node}.dll</c> with assembly name
/// <c>DynamicNode_{node}</c>, and the pipeline writes <c>DynamicNode_{node}.xml</c> beside the dll.</para>
///
/// <para><b>Weakly keyed.</b> The per-assembly snapshots live in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>, which does not keep its key alive: a
/// runtime-compiled NodeType's assembly lives in a COLLECTIBLE load context, and a strong key would
/// root that generation for the lifetime of the mesh. Keying by the <see cref="Assembly"/> instance
/// (not its name) also means a recompiled NodeType reads ITS docs, not the first build's.</para>
///
/// <para><b>No lock.</b> Each file is parsed once into an immutable snapshot;
/// <see cref="ConditionalWeakTable{TKey,TValue}.GetValue"/> is thread-safe (a racing parse is
/// discarded) and nothing shared is mutated afterwards.</para>
///
/// <para><b>Synchronous on first read.</b> A cold lookup reads and parses the file on the caller's
/// thread, exactly as the previous third-party reader did. It cannot be warmed through the
/// platform's IO pool from here: <c>IIoPool</c> lives in <c>MeshWeaver.Mesh.Contract</c>, which
/// depends on this assembly, and a warm-up that the synchronous reader then waits for would be a
/// blocking bridge.</para>
/// </summary>
public sealed class XmlDocsReader
{
    // Bound on <inheritdoc/> chains, so a cycle in hand-written crefs cannot recurse forever.
    private const int MaxInheritDepth = 16;

    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static;

    private readonly ConditionalWeakTable<Assembly, XmlDocFile> files = new();

    /// <summary>The <c>&lt;summary&gt;</c> text of a type, or the empty string when it has none.</summary>
    /// <param name="type">The type; a constructed generic type reads its definition's docs.</param>
    /// <returns>The rendered summary text.</returns>
    public string Summary(Type type)
        => FindSummary(type, 0) ?? string.Empty;

    /// <summary>
    /// The <c>&lt;summary&gt;</c> text of a member (property, field, method, constructor, event or
    /// nested type), or the empty string. A property with no summary of its own falls back to the
    /// <c>&lt;param&gt;</c> of the same name on its declaring type — the shape a positional record's
    /// properties are documented in.
    /// </summary>
    /// <param name="member">The member.</param>
    /// <returns>The rendered summary text.</returns>
    public string Summary(MemberInfo member)
        => FindSummary(member, 0) ?? string.Empty;

    private XmlDocFile File(Assembly assembly)
        => files.GetValue(assembly, static a => XmlDocFile.Load(a));

    private static Assembly AssemblyOf(MemberInfo member) => member is Type type ? type.Assembly : member.Module.Assembly;

    private XmlMemberDoc? Lookup(MemberInfo member)
        => DocCommentId.For(member) is { } id ? File(AssemblyOf(member)).Members.GetValueOrDefault(id) : null;

    /// <summary>
    /// The summary of <paramref name="member"/>: its own, else what its <c>&lt;inheritdoc/&gt;</c>
    /// resolves to, else (a property) its declaring type's <c>&lt;param&gt;</c> of the same name.
    /// Every path, crefs and inheritance included, re-enters here, so the depth bound and the
    /// record fallback apply at every hop.
    /// </summary>
    private string? FindSummary(MemberInfo member, int depth)
    {
        if (depth > MaxInheritDepth)
            return null;
        // A constructed generic method is documented, implements and overrides as its generic
        // definition: interface maps and base signatures name the definition.
        if (member is MethodInfo { IsConstructedGenericMethod: true } constructed)
            member = constructed.GetGenericMethodDefinition();

        var summary = Lookup(member) switch
        {
            null => null,
            { Summary: { } own } => own,
            { InheritDoc: false } => null,
            { InheritDocCref: { } cref } => FindByCref(member, cref, depth + 1),
            _ => FindInherited(member, depth + 1),
        };
        if (string.IsNullOrEmpty(summary)
            && member is PropertyInfo { DeclaringType: { } declaringType } property
            && Lookup(declaringType) is { } typeDoc
            && typeDoc.Params.TryGetValue(property.Name, out var recordParam))
            return recordParam;
        return summary;
    }

    /// <summary>An <c>&lt;inheritdoc/&gt;</c> with no <c>cref</c>: the base class first, then interfaces.</summary>
    private string? FindInherited(MemberInfo member, int depth)
    {
        foreach (var candidate in InheritanceCandidates(member))
            if (FindSummary(candidate, depth) is { } summary)
                return summary;
        return null;
    }

    private static IEnumerable<MemberInfo> InheritanceCandidates(MemberInfo member)
    {
        if (member is Type type)
        {
            for (var b = type.BaseType; b is not null; b = b.BaseType)
                yield return b;
            foreach (var i in type.GetInterfaces())
                yield return i;
            yield break;
        }

        if (member.DeclaringType is not { } declaring)
            yield break;

        for (var b = declaring.BaseType; b is not null; b = b.BaseType)
            foreach (var m in b.GetMember(member.Name, member.MemberType, Declared))
                if (SameSignature(m, member))
                    yield return m;

        foreach (var i in declaring.GetInterfaces())
        {
            // Through the interface map when there is one (open generic definitions included): it
            // names the member actually IMPLEMENTED, which a name match cannot when two interfaces
            // declare the same name.
            if (!declaring.IsInterface && InterfaceMapOrNull(declaring, i) is { } map)
            {
                foreach (var implemented in MappedInterfaceMembers(member, map))
                    yield return implemented;
                continue;
            }
            // No map (a runtime-special type): match by name and signature, restricted to the
            // interface an explicit implementation names exactly.
            if (!ExplicitQualifierMatches(member.Name, i))
                continue;
            foreach (var m in i.GetMember(InterfaceMemberName(member.Name), member.MemberType, Declared))
                if (SameSignature(m, member))
                    yield return m;
        }
    }

    /// <summary>The members of <c>map.InterfaceType</c> that <paramref name="member"/> implements.</summary>
    private static IEnumerable<MemberInfo> MappedInterfaceMembers(MemberInfo member, InterfaceMapping map)
    {
        var accessors = member switch
        {
            MethodInfo method => [method],
            PropertyInfo property => property.GetAccessors(nonPublic: true),
            EventInfo @event => new[] { @event.AddMethod, @event.RemoveMethod }.OfType<MethodInfo>().ToArray(),
            _ => Array.Empty<MethodInfo>(),
        };
        var interfaceMethods = ImmutableHashSet<MethodInfo>.Empty;
        for (var k = 0; k < map.TargetMethods.Length; k++)
            if (accessors.Contains(map.TargetMethods[k]))
                interfaceMethods = interfaceMethods.Add(map.InterfaceMethods[k]);
        if (interfaceMethods.IsEmpty)
            yield break;

        switch (member)
        {
            case MethodInfo:
                foreach (var m in interfaceMethods)
                    yield return m;
                break;
            case PropertyInfo:
                foreach (var p in map.InterfaceType.GetProperties(Declared))
                    if (p.GetAccessors(nonPublic: true).Any(interfaceMethods.Contains))
                        yield return p;
                break;
            case EventInfo:
                foreach (var e in map.InterfaceType.GetEvents(Declared))
                    if ((e.AddMethod is { } add && interfaceMethods.Contains(add))
                        || (e.RemoveMethod is { } remove && interfaceMethods.Contains(remove)))
                        yield return e;
                break;
        }
    }

    private static InterfaceMapping? InterfaceMapOrNull(Type type, Type iface)
    {
        // GetInterfaceMap answers for open generic definitions too; it refuses a few runtime-special
        // types, and for those the exact qualified name match is used instead.
        try
        {
            return type.GetInterfaceMap(iface);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // An explicit implementation is named "Ns.IFoo.Bar" (or "Ns.IFoo<T>.Bar"); the interface declares "Bar".
    private static string InterfaceMemberName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>
    /// For an explicit implementation, whether <paramref name="iface"/> is EXACTLY the interface its
    /// name qualifies. The compiler names the member <c>{interface}.{member}</c> with the interface
    /// in C# form (<c>Ns.IFoo&lt;System.Int32&gt;</c>, <c>Ns.Outer&lt;T&gt;.IA</c>,
    /// <c>Ns.IPair&lt;T,System.String&gt;</c>), so the full identity (nesting, arity, arguments)
    /// is compared. An implicit implementation (no qualifier) matches any interface.
    /// </summary>
    private static bool ExplicitQualifierMatches(string memberName, Type iface)
    {
        var dot = memberName.LastIndexOf('.');
        return dot < 0 || string.Equals(memberName[..dot], CSharpName(iface), StringComparison.Ordinal);
    }

    /// <summary>A type as the C# compiler spells it in an explicit implementation's metadata name.</summary>
    internal static string CSharpName(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name;
        if (type.IsArray)
            return CSharpName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsPointer)
            return CSharpName(type.GetElementType()!) + "*";
        var arguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        var chain = ImmutableStack<Type>.Empty;
        for (var t = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type; t is not null; t = t.DeclaringType)
            chain = chain.Push(t);
        var name = new System.Text.StringBuilder();
        var consumed = 0;
        var first = true;
        while (!chain.IsEmpty)
        {
            chain = chain.Pop(out var level);
            if (first)
            {
                if (!string.IsNullOrEmpty(level.Namespace))
                    name.Append(level.Namespace).Append('.');
                first = false;
            }
            else
                name.Append('.');
            var tick = level.Name.IndexOf('`');
            name.Append(tick < 0 ? level.Name : level.Name[..tick]);
            var total = level.IsGenericType ? level.GetGenericArguments().Length : 0;
            if (total > consumed)
            {
                name.Append('<').Append(string.Join(",", arguments[consumed..total].Select(CSharpName))).Append('>');
                consumed = total;
            }
        }
        return name.ToString();
    }

    private static bool SameSignature(MemberInfo a, MemberInfo b)
    {
        if (a is MethodBase ma && b is MethodBase mb
            && (ma.IsGenericMethod ? ma.GetGenericArguments().Length : 0) != (mb.IsGenericMethod ? mb.GetGenericArguments().Length : 0))
            return false;
        var pa = a switch { MethodBase m => m.GetParameters(), PropertyInfo p => p.GetIndexParameters(), _ => null };
        var pb = b switch { MethodBase m => m.GetParameters(), PropertyInfo p => p.GetIndexParameters(), _ => null };
        if (pa is null || pb is null)
            return pa is null && pb is null;
        if (pa.Length != pb.Length)
            return false;
        for (var k = 0; k < pa.Length; k++)
            if (DocCommentId.TypeName(pa[k].ParameterType) != DocCommentId.TypeName(pb[k].ParameterType))
                return false;
        return true;
    }

    /// <summary>
    /// An <c>&lt;inheritdoc cref="..."/&gt;</c>. The referenced member is resolved and read like any
    /// other member — so its own <c>&lt;inheritdoc/&gt;</c>, with or without a cref, continues in ITS
    /// declaring type's context, under the same depth bound. A cref whose member cannot be resolved
    /// by reflection falls back to the documented text under that ID.
    /// </summary>
    private string? FindByCref(MemberInfo member, string cref, int depth)
    {
        if (depth > MaxInheritDepth)
            return null;
        var assemblies = CrefAssemblies(member, cref).ToImmutableArray();
        if (ResolveCref(cref, assemblies) is { } target)
            return FindSummary(target, depth);
        foreach (var assembly in assemblies)
            if (File(assembly).Members.GetValueOrDefault(cref) is { } doc)
                return doc.Summary;
        return null;
    }

    private static MemberInfo? ResolveCref(string cref, ImmutableArray<Assembly> assemblies)
    {
        if (DocCommentId.TypeNameOfCref(cref) is not { } typeName)
            return null;
        foreach (var assembly in assemblies)
        {
            if (DocCommentId.FindType(assembly, typeName) is not { } type)
                continue;
            if (cref[0] == 'T')
                return type;
            foreach (var m in type.GetMembers(Declared))
                if (DocCommentId.For(m) == cref)
                    return m;
        }
        return null;
    }

    private static IEnumerable<Assembly> CrefAssemblies(MemberInfo member, string cref)
    {
        var owner = member as Type ?? member.DeclaringType;
        var seen = ImmutableHashSet<Assembly>.Empty;
        IEnumerable<Assembly> Hierarchy()
        {
            yield return AssemblyOf(member);
            if (owner is null)
                yield break;
            for (var b = owner.BaseType; b is not null; b = b.BaseType)
                yield return b.Assembly;
            foreach (var i in owner.GetInterfaces())
                yield return i.Assembly;
        }

        foreach (var a in Hierarchy())
            if (!seen.Contains(a))
            {
                seen = seen.Add(a);
                yield return a;
            }

        // A cref to a type outside the hierarchy: find the type among the assemblies loaded in the
        // member's own load context and the default one.
        if (DocCommentId.TypeNameOfCref(cref) is not { } typeName)
            yield break;
        foreach (var context in new[] { AssemblyLoadContext.GetLoadContext(AssemblyOf(member)), AssemblyLoadContext.Default })
        {
            if (context is null)
                continue;
            foreach (var a in context.Assemblies)
                if (!seen.Contains(a) && !a.IsDynamic && DocCommentId.FindType(a, typeName) is not null)
                {
                    seen = seen.Add(a);
                    yield return a;
                }
        }
    }
}
