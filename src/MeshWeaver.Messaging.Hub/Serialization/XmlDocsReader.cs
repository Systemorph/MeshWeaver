using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
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
/// <para><b>Two ways to read, and only one of them may touch the disk.</b>
/// <see cref="Summary(MemberInfo)"/> answers synchronously and, on a cold assembly, reads and parses
/// its documentation file on the CALLER's thread. That is acceptable where the caller is already at
/// an I/O edge, and wrong anywhere a view is being built or a hub is being configured. Those paths
/// use the reactive half instead: <see cref="WhenLoaded"/> (and <see cref="ObserveSummary"/> on top of
/// it) answers at once when every snapshot a lookup needs is already in memory, and otherwise runs
/// the cold reads through an <see cref="IBlockingReadScheduler"/> — the mesh's I/O pool, which this
/// assembly cannot reference directly — and emits once they are done. After that emission the
/// synchronous lookups for the same members are dictionary hits. <see cref="TrySummary(MemberInfo, out string)"/>
/// never reads at all; it says whether the answer is already in memory. Reach the reactive half from
/// a hub with <c>hub.WhenXmlDocsLoaded(...)</c> / <c>hub.ObserveXmlDocsSummary(...)</c>.</para>
/// </summary>
public sealed class XmlDocsReader
{
    // Stack-safety bound on how many distinct members one lookup may hold open at once. Cycles are
    // cut by the in-progress set below, so this only limits pathologically deep legitimate chains.
    private const int MaxInheritDepth = 64;

    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.Static;

    private readonly ConditionalWeakTable<Assembly, XmlDocFile> files = new();

    /// <summary>The <c>&lt;summary&gt;</c> text of a type, or the empty string when it has none.</summary>
    /// <param name="type">The type; a constructed generic type reads its definition's docs.</param>
    /// <returns>The rendered summary text.</returns>
    public string Summary(Type type)
        => new Resolution(this, loadedOnly: false).Find(type) ?? string.Empty;

    /// <summary>
    /// The <c>&lt;summary&gt;</c> text of a member (property, field, method, constructor, event or
    /// nested type), or the empty string. A property with no summary of its own falls back to the
    /// <c>&lt;param&gt;</c> of the same name on its declaring type — the shape a positional record's
    /// properties are documented in.
    /// </summary>
    /// <param name="member">The member.</param>
    /// <returns>The rendered summary text.</returns>
    public string Summary(MemberInfo member)
        => new Resolution(this, loadedOnly: false).Find(member) ?? string.Empty;

    /// <summary>
    /// The summary of <paramref name="member"/> (a type or a member) if it can be answered from
    /// snapshots already in memory — WITHOUT reading any file. Returns <c>false</c> when some
    /// documentation file the lookup needs (the member's own, or one an <c>&lt;inheritdoc/&gt;</c>
    /// leads to) has not been loaded yet; <see cref="WhenLoaded"/> loads it at the I/O edge.
    /// </summary>
    /// <param name="member">The type or member.</param>
    /// <param name="summary">The rendered summary text when the answer is in memory; otherwise empty.</param>
    /// <returns>Whether the answer was available without a read.</returns>
    public bool TrySummary(MemberInfo member, out string summary)
    {
        var resolution = new Resolution(this, loadedOnly: true);
        var found = resolution.Find(member);
        summary = resolution.Missed ? string.Empty : found ?? string.Empty;
        return !resolution.Missed;
    }

    /// <summary>
    /// Emits once every documentation file the summaries of <paramref name="members"/> need is in
    /// memory, so that <see cref="Summary(MemberInfo)"/> for those members afterwards reads nothing.
    /// COLD. When everything is already loaded it emits synchronously on subscribe; otherwise the
    /// lookups — and with them the cold file reads — run through <paramref name="io"/> and the
    /// emission follows on the pool's thread. With no scheduler (a hub without I/O pools) the work is
    /// hopped onto the task pool, so the subscribing thread never reads either way.
    /// </summary>
    /// <param name="members">The types and members whose summaries are about to be read.</param>
    /// <param name="io">The mesh's blocking-read edge, or <c>null</c> when the hub has none.</param>
    /// <returns>A single <see cref="Unit"/>.</returns>
    public IObservable<Unit> WhenLoaded(IEnumerable<MemberInfo> members, IBlockingReadScheduler? io)
        => Observable.Defer(() =>
        {
            var all = members.ToImmutableArray();
            var cold = all.Where(m => !TrySummary(m, out _)).ToImmutableArray();
            if (cold.IsEmpty)
                return Observable.Return(Unit.Default);
            Unit Load(CancellationToken ct)
            {
                foreach (var member in cold)
                {
                    ct.ThrowIfCancellationRequested();
                    _ = Summary(member);
                }
                return Unit.Default;
            }
            return io is not null
                ? io.InvokeBlocking(Load)
                : Observable.Defer(() => Observable.Return(Load(CancellationToken.None))).SubscribeOn(TaskPoolScheduler.Default);
        });

    /// <summary>
    /// The summary of <paramref name="member"/>, read through <see cref="WhenLoaded"/>: emitted at once
    /// when it is in memory, otherwise after the cold read has run at the I/O edge. COLD, one value.
    /// </summary>
    /// <param name="member">The type or member.</param>
    /// <param name="io">The mesh's blocking-read edge, or <c>null</c> when the hub has none.</param>
    /// <returns>The rendered summary text, empty when there is none.</returns>
    public IObservable<string> ObserveSummary(MemberInfo member, IBlockingReadScheduler? io)
        => WhenLoaded([member], io).Select(_ => Summary(member));

    private XmlDocFile Load(Assembly assembly)
        => files.GetValue(assembly, static a => XmlDocFile.Load(a));

    private XmlDocFile? Loaded(Assembly assembly)
        => files.TryGetValue(assembly, out var file) ? file : null;

    private static Assembly AssemblyOf(MemberInfo member) => member is Type type ? type.Assembly : member.Module.Assembly;

    /// <summary>
    /// A member's METADATA identity: its module and token, a constructed generic method taken as its
    /// definition. Two <see cref="MemberInfo"/>s for the same metadata member compare unequal by
    /// reference or <c>Equals</c> when they were reached differently — through a subclass (another
    /// <see cref="MemberInfo.ReflectedType"/>), a constructed type, or an interface map — so EVERY member
    /// comparison in this reader goes through this key.
    /// </summary>
    private static (Module Module, int Token) Identity(MemberInfo member)
    {
        if (member is MethodInfo { IsConstructedGenericMethod: true } constructed)
            member = constructed.GetGenericMethodDefinition();
        return (member.Module, member.MetadataToken);
    }

    /// <summary>
    /// One summary lookup. Memoizes every member it resolves (keyed by assembly and documentation ID)
    /// and every base-chain walk, so <c>&lt;inheritdoc/&gt;</c> resolution costs one visit per distinct
    /// member however the chain branches; a member already open on the stack (a cycle) resolves to
    /// nothing. Instance-local and discarded with the lookup — the shared state stays the immutable
    /// per-assembly snapshots.
    /// </summary>
    private sealed class Resolution(XmlDocsReader reader, bool loadedOnly)
    {
        private ImmutableDictionary<(Assembly, string), string?> resolved = ImmutableDictionary<(Assembly, string), string?>.Empty;
        private ImmutableHashSet<(Assembly, string)> open = ImmutableHashSet<(Assembly, string)>.Empty;

        /// <summary>
        /// In memory-only mode: whether the lookup needed a documentation file that is not loaded.
        /// The answer it produced is then incomplete and must not be used.
        /// </summary>
        public bool Missed { get; private set; }

        /// <summary>
        /// The snapshot of <paramref name="assembly"/>: loaded on demand, or — in memory-only mode —
        /// taken only if already loaded, recording the miss otherwise.
        /// </summary>
        private XmlDocFile File(Assembly assembly)
        {
            if (!loadedOnly)
                return reader.Load(assembly);
            if (reader.Loaded(assembly) is { } file)
                return file;
            Missed = true;
            return XmlDocFile.Empty;
        }

        /// <summary>
        /// The summary of <paramref name="member"/>: its own, else what its <c>&lt;inheritdoc/&gt;</c>
        /// resolves to, else (a property) its declaring type's <c>&lt;param&gt;</c> of the same name.
        /// Every path — crefs, base classes, interfaces — re-enters here.
        /// </summary>
        public string? Find(MemberInfo member)
        {
            // A constructed generic method is documented, implements and overrides as its definition.
            if (member is MethodInfo { IsConstructedGenericMethod: true } constructed)
                member = constructed.GetGenericMethodDefinition();
            if (DocCommentId.For(member) is not { } id)
                return null;
            return Memoized((AssemblyOf(member), id), () => Compute(member));
        }

        private string? Memoized((Assembly, string) key, Func<string?> compute)
        {
            if (resolved.TryGetValue(key, out var known))
                return known;
            if (open.Contains(key) || open.Count >= MaxInheritDepth)
                return null;
            open = open.Add(key);
            var result = compute();
            open = open.Remove(key);
            resolved = resolved.SetItem(key, result);
            return result;
        }

        private string? Compute(MemberInfo member)
        {
            var doc = DocCommentId.For(member) is { } id ? File(AssemblyOf(member)).Members.GetValueOrDefault(id) : null;
            var summary = doc switch
            {
                null => null,
                { Summary: { } own } => own,
                { InheritDoc: false } => null,
                { InheritDocCref: { } cref } => FindByCref(member, cref),
                _ => FindInherited(member),
            };
            if (string.IsNullOrEmpty(summary)
                && member is PropertyInfo { DeclaringType: { } declaringType } property
                && DocCommentId.For(declaringType) is { } typeId
                && File(AssemblyOf(declaringType)).Members.GetValueOrDefault(typeId) is { } typeDoc
                && typeDoc.Params.TryGetValue(property.Name, out var recordParam))
                return recordParam;
            return summary;
        }

        /// <summary>An <c>&lt;inheritdoc/&gt;</c> with no <c>cref</c>: the base class chain first, then interfaces.</summary>
        private string? FindInherited(MemberInfo member)
        {
            if (FromBase(member) is { } fromBase)
                return fromBase;
            foreach (var candidate in InterfaceCandidates(member))
                if (Find(candidate) is { } summary)
                    return summary;
            return null;
        }

        /// <summary>
        /// The nearest base member's summary, or — when it has none — ITS nearest base's, and so on.
        /// Memoized per member, so a chain of N undocumented overrides is walked once, not N² times
        /// (or exponentially, as resolving every ancestor of every ancestor was).
        /// </summary>
        private string? FromBase(MemberInfo member)
        {
            if (NearestBase(member) is not { } nearest || DocCommentId.For(nearest) is not { } id)
                return null;
            return Memoized((AssemblyOf(nearest), "base:" + id), () => Find(nearest) ?? FromBase(nearest));
        }

        /// <summary>
        /// An <c>&lt;inheritdoc cref="..."/&gt;</c>: the referenced member is resolved and read like any
        /// other, so its own <c>&lt;inheritdoc/&gt;</c> continues in ITS declaring type's context. A cref
        /// reflection cannot resolve falls back to the documented text under that ID.
        /// </summary>
        private string? FindByCref(MemberInfo member, string cref)
        {
            var assemblies = CrefAssemblies(member, cref).ToImmutableArray();
            if (ResolveCref(cref, assemblies) is { } target)
                return Find(target);
            foreach (var assembly in assemblies)
                if (File(assembly).Members.GetValueOrDefault(cref) is { } doc)
                    return doc.Summary;
            return null;
        }
    }

    /// <summary>
    /// The nearest base type, or the nearest base-class member this member overrides or hides.
    /// An OVERRIDE (a method, or a property's / event's accessor) is matched by the identity of its
    /// root virtual slot (<see cref="MethodInfo.GetBaseDefinition"/>): constructing a generic base can
    /// give two distinct overloads identical parameter types (<c>Base&lt;int&gt;.M(T)</c> and
    /// <c>M(int)</c>), and only the slot says which one is overridden. A member that overrides nothing
    /// (a field, a non-virtual or <c>new</c> member) falls back to name and signature.
    /// </summary>
    private static MemberInfo? NearestBase(MemberInfo member)
    {
        if (member is Type type)
            return type.BaseType;
        if (member.DeclaringType is not { } declaring)
            return null;

        if (OverriddenSlot(member) is { } slot)
        {
            for (var b = declaring.BaseType; b is not null; b = b.BaseType)
                foreach (var m in b.GetMember(member.Name, member.MemberType, Declared))
                    if (RootSlot(m) == slot)
                        return m;
            return null;
        }

        for (var b = declaring.BaseType; b is not null; b = b.BaseType)
            foreach (var m in b.GetMember(member.Name, member.MemberType, Declared))
                if (SameSignature(m, member))
                    return m;
        return null;
    }

    /// <summary>The accessor (or method) that carries a member's virtual slot.</summary>
    private static MethodInfo? SlotMethod(MemberInfo member) => member switch
    {
        MethodInfo method => method,
        PropertyInfo property => property.GetMethod ?? property.SetMethod,
        EventInfo @event => @event.AddMethod,
        _ => null,
    };

    /// <summary>The identity of the root virtual slot a member's method or accessor occupies.</summary>
    private static (Module Module, int Token)? RootSlot(MemberInfo member)
        => SlotMethod(member) is { IsVirtual: true } method ? Identity(method.GetBaseDefinition()) : null;

    /// <summary>
    /// The root slot of a member that OVERRIDES an inherited virtual (its slot's root lies in a base
    /// type), or <c>null</c> for a member that introduces its own slot or is not virtual.
    /// </summary>
    private static (Module Module, int Token)? OverriddenSlot(MemberInfo member)
        => SlotMethod(member) is { IsVirtual: true } method && RootSlot(member) is { } root && root != Identity(method)
            ? root
            : null;

    private static IEnumerable<MemberInfo> InterfaceCandidates(MemberInfo member)
    {
        if (member is Type type)
        {
            foreach (var i in type.GetInterfaces())
                yield return i;
            yield break;
        }

        if (member.DeclaringType is not { } declaring)
            yield break;

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

    /// <summary>
    /// The members of <c>map.InterfaceType</c> that <paramref name="member"/> implements, matched by
    /// <see cref="Identity"/> — never by reference — so a member read through a subclass or a
    /// constructed type still finds its map entry.
    /// </summary>
    private static IEnumerable<MemberInfo> MappedInterfaceMembers(MemberInfo member, InterfaceMapping map)
    {
        var accessors = member switch
        {
            MethodInfo method => [method],
            PropertyInfo property => property.GetAccessors(nonPublic: true),
            EventInfo @event => new[] { @event.AddMethod, @event.RemoveMethod }.OfType<MethodInfo>().ToArray(),
            _ => Array.Empty<MethodInfo>(),
        };
        var accessorIds = accessors.Select(Identity).ToImmutableHashSet();
        var interfaceMethods = ImmutableList<MethodInfo>.Empty;
        for (var k = 0; k < map.TargetMethods.Length; k++)
            if (accessorIds.Contains(Identity(map.TargetMethods[k])))
                interfaceMethods = interfaceMethods.Add(map.InterfaceMethods[k]);
        if (interfaceMethods.IsEmpty)
            yield break;
        var interfaceIds = interfaceMethods.Select(Identity).ToImmutableHashSet();

        switch (member)
        {
            case MethodInfo:
                foreach (var m in interfaceMethods)
                    yield return m;
                break;
            case PropertyInfo:
                foreach (var p in map.InterfaceType.GetProperties(Declared))
                    if (p.GetAccessors(nonPublic: true).Any(a => interfaceIds.Contains(Identity(a))))
                        yield return p;
                break;
            case EventInfo:
                foreach (var e in map.InterfaceType.GetEvents(Declared))
                    if (new[] { e.AddMethod, e.RemoveMethod }.OfType<MethodInfo>().Any(a => interfaceIds.Contains(Identity(a))))
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
