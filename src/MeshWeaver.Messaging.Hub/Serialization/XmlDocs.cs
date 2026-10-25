using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Reads XML-documentation summaries for types and members — the ONLY sanctioned way to read doc
/// summaries anywhere in the platform. See <c>Doc/Architecture/XmlDocumentationReader</c>.
///
/// <para><b>Where the docs come from.</b> The compiler's <c>{AssemblyName}.xml</c> beside
/// <see cref="Assembly.Location"/>; for an assembly with no location (loaded from a stream, or a
/// single-file host) the application base directory and the current directory, each also under
/// <c>bin/</c>. Compiled NodeTypes are emitted as <c>{node}.dll</c> with assembly name
/// <c>DynamicNode_{node}</c>, and the pipeline writes <c>DynamicNode_{node}.xml</c> beside the dll
/// so the first probe finds it.</para>
///
/// <para><b>Why a <see cref="ConditionalWeakTable{TKey,TValue}"/> and not a mesh-owned
/// singleton.</b> The callers are display-metadata reads (<c>TypeDisplayMetadata</c>, layout areas,
/// JSON-schema generation) that hold a <see cref="Type"/> and no hub or service provider. A static
/// <c>Dictionary</c> keyed by assembly would root every collectible NodeType
/// <c>AssemblyLoadContext</c> it ever saw, for the process lifetime — the defect the
/// no-static-collections rule exists to prevent. A <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// does not keep its key alive: an entry lives exactly as long as its <see cref="Assembly"/>, so an
/// unloaded generation is collected with its docs (pinned by <c>XmlDocsCollectibleContextTest</c>).
/// The values are immutable maps of plain strings that reference no reflection object, and the table
/// holds no state that could bleed between meshes: the same assembly yields the same docs in every
/// mesh. Keying by the <see cref="Assembly"/> instance (not its name) also means a recompiled
/// NodeType — same assembly name, new generation — reads ITS docs, not the first build's.</para>
///
/// <para><b>No lock.</b> Each file is parsed once into an immutable snapshot;
/// <see cref="ConditionalWeakTable{TKey,TValue}.GetValue"/> is thread-safe (a racing parse is
/// discarded), and nothing shared is ever mutated afterwards. The lock this class used to hold
/// guarded a third-party, process-wide cache of live <c>XDocument</c>s that concurrent readers tore
/// (SIGSEGV in <c>XContainer.GetElements</c>); that cache no longer exists.</para>
/// </summary>
public static class XmlDocs
{
    // Bound on <inheritdoc/> chains, so a cycle in hand-written crefs cannot recurse forever.
    private const int MaxInheritDepth = 16;

    private static readonly ConditionalWeakTable<Assembly, XmlDocFile> Files = new();

    /// <summary>The <c>&lt;summary&gt;</c> text of a type, or the empty string when it has none.</summary>
    /// <param name="type">The type; a constructed generic type reads its definition's docs.</param>
    /// <returns>The rendered summary text.</returns>
    public static string Summary(Type type)
        => FindSummary(type, 0) ?? string.Empty;

    /// <summary>
    /// The <c>&lt;summary&gt;</c> text of a member (property, field, method, constructor, event or
    /// nested type), or the empty string. A property with no summary of its own falls back to the
    /// <c>&lt;param&gt;</c> of the same name on its declaring type — the shape a positional record's
    /// properties are documented in.
    /// </summary>
    /// <param name="member">The member.</param>
    /// <returns>The rendered summary text.</returns>
    public static string Summary(MemberInfo member)
    {
        var summary = FindSummary(member, 0);
        if (string.IsNullOrEmpty(summary)
            && member is PropertyInfo { DeclaringType: { } declaringType } property
            && Lookup(declaringType) is { } typeDoc
            && typeDoc.Params.TryGetValue(property.Name, out var recordParam))
            return recordParam;
        return summary ?? string.Empty;
    }

    /// <summary>
    /// The documentation comment ID the C# compiler writes for <paramref name="member"/>
    /// (<c>T:</c>, <c>P:</c>, <c>M:</c>, <c>F:</c>, <c>E:</c>), or <c>null</c> when it has none
    /// (a generic parameter, a global method).
    /// </summary>
    /// <param name="member">The type or member.</param>
    /// <returns>The ID, e.g. <c>M:Ns.C.M``1(``0,System.Int32@)</c>.</returns>
    public static string? DocumentationId(MemberInfo member) => DocCommentId.For(member);

    private static XmlDocFile File(Assembly assembly)
        => Files.GetValue(assembly, static a => XmlDocFile.Load(a));

    private static XmlMemberDoc? Lookup(MemberInfo member)
    {
        if (DocCommentId.For(member) is not { } id)
            return null;
        var assembly = member is Type type ? type.Assembly : member.Module.Assembly;
        return File(assembly).Members.GetValueOrDefault(id);
    }

    private static string? FindSummary(MemberInfo member, int depth)
    {
        if (depth > MaxInheritDepth || Lookup(member) is not { } doc)
            return null;
        if (doc.Summary is not null || !doc.InheritDoc)
            return doc.Summary;
        return doc.InheritDocCref is { } cref
            ? FindByCref(member, cref, depth + 1)
            : FindInherited(member, depth + 1);
    }

    /// <summary>An <c>&lt;inheritdoc/&gt;</c> with no <c>cref</c>: the base class first, then interfaces.</summary>
    private static string? FindInherited(MemberInfo member, int depth)
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

        const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                      | BindingFlags.Instance | BindingFlags.Static;
        for (var b = declaring.BaseType; b is not null; b = b.BaseType)
            foreach (var m in b.GetMember(member.Name, member.MemberType, declared))
                if (SameSignature(m, member))
                    yield return m;

        foreach (var i in declaring.GetInterfaces())
        {
            if (member is MethodInfo method && !declaring.IsInterface)
            {
                var map = InterfaceMapOrNull(declaring, i);
                if (map is { } m)
                    for (var k = 0; k < m.TargetMethods.Length; k++)
                        if (m.TargetMethods[k] == method)
                            yield return m.InterfaceMethods[k];
                continue;
            }
            foreach (var m in i.GetMember(InterfaceMemberName(member.Name), member.MemberType, declared))
                if (SameSignature(m, member))
                    yield return m;
        }
    }

    private static InterfaceMapping? InterfaceMapOrNull(Type type, Type iface)
    {
        // GetInterfaceMap refuses an open generic type and a few runtime-special types; for those the
        // name match below is all <inheritdoc/> can do, exactly as before.
        if (type.ContainsGenericParameters)
            return null;
        try
        {
            return type.GetInterfaceMap(iface);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // An explicit implementation is named "Ns.IFoo.Bar"; the interface declares "Bar".
    private static string InterfaceMemberName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    private static bool SameSignature(MemberInfo a, MemberInfo b)
    {
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

    /// <summary>An <c>&lt;inheritdoc cref="..."/&gt;</c>: the referenced member's own docs.</summary>
    private static string? FindByCref(MemberInfo member, string cref, int depth)
    {
        var owner = member as Type ?? member.DeclaringType;
        foreach (var assembly in CrefAssemblies(member, owner, cref))
        {
            if (File(assembly).Members.GetValueOrDefault(cref) is not { } doc)
                continue;
            if (doc.Summary is not null || !doc.InheritDoc)
                return doc.Summary;
            return doc.InheritDocCref is { } next && next != cref ? FindByCref(member, next, depth + 1) : null;
        }
        return null;
    }

    private static IEnumerable<Assembly> CrefAssemblies(MemberInfo member, Type? owner, string cref)
    {
        var seen = ImmutableHashSet<Assembly>.Empty;
        IEnumerable<Assembly> Hierarchy()
        {
            yield return member is Type t ? t.Assembly : member.Module.Assembly;
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
        var typeName = DocCommentId.TypeNameOfCref(cref);
        if (typeName is null)
            yield break;
        var origin = member is Type ot ? ot.Assembly : member.Module.Assembly;
        var contexts = new[] { System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(origin), System.Runtime.Loader.AssemblyLoadContext.Default };
        foreach (var context in contexts)
        {
            if (context is null)
                continue;
            foreach (var a in context.Assemblies)
                if (!seen.Contains(a) && !a.IsDynamic && DocCommentId.DefinesType(a, typeName))
                {
                    seen = seen.Add(a);
                    yield return a;
                }
        }
    }
}
