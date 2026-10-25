using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// A parent-chained OVERLAY of type definitions (<c>Doc/Architecture/TypeRegistryLayers</c>).
///
/// <para>Every hub owns a registry chained to its parent's, up to the mesh registry. A registry's own
/// layer holds ONLY what it adds to the chain or overrides in it — a type the chain does not know, or a
/// different name or key for one it does. Everything else is resolved through the parent, recursively:
/// <list type="bullet">
/// <item><b>Lookups</b> — by type, by name and by key — walk own layer → parent → … → root.</item>
/// <item><b>Writes</b>: a <c>WithType</c> the chain already answers identically (the same type under the
/// same name) is a no-op returning the existing definition; nothing is stored. An override is stored in
/// the own layer and wins there, invisible to the parent and to siblings.</item>
/// <item><b>Enumeration</b> (<see cref="Types"/>) is the merged view: the parent's entries first, an own
/// entry in place of the parent's of the same name, then the own layer's additions.</item>
/// </list></para>
///
/// <para>The layer is an immutable snapshot replaced by compare-and-swap, so a reader never takes a
/// lock and a writer never blocks. A registry that adds nothing holds the shared empty layer.</para>
///
/// <para>Measured before the overlay (#7163, <c>Doc/Architecture/PortalHeapIsHubs</c>): 344 own entries
/// per Markdown node hub and 19 per <c>sync/</c> stream hub, almost all of them types the mesh
/// registry already held, and two definitions of each built-in message type per hub.</para>
/// </summary>
internal class TypeRegistry : ITypeRegistry
{
    private readonly ITypeRegistry? parent;
    // The parent when it is a TypeRegistry: the chain walk reads its layer directly — no synthesis and
    // no creation on the way up. A parent of another implementation is asked through ITypeRegistry.
    private readonly TypeRegistry? parentLayer;

    /// <summary>The registry's own layer: name → definition, type → name, full-name alias → definition.</summary>
    private sealed record Layer(
        ImmutableDictionary<string, TypeDefinition> ByName,
        ImmutableDictionary<Type, string> NameByType,
        ImmutableDictionary<string, TypeDefinition> Aliases)
    {
        /// <summary>The layer of a registry that adds nothing. Immutable, shared by every such registry.</summary>
        public static readonly Layer Empty = new(
            ImmutableDictionary<string, TypeDefinition>.Empty,
            ImmutableDictionary<Type, string>.Empty,
            ImmutableDictionary<string, TypeDefinition>.Empty);

        /// <summary>This layer with <paramref name="definition"/> bound to <paramref name="name"/>, its type and its full-name alias.</summary>
        public Layer Bind(Type type, string name, TypeDefinition definition, bool withAlias = true)
        {
            var aliases = Aliases;
            var fullName = FullNameOf(type);
            // Resolution alias: the full (namespace-qualified) name resolves too, so a full-name $type
            // discriminator — persisted data, or a payload written before the short-name default
            // (fb2ee677d) — still RESOLVES on the way IN. The canonical OUTPUT name stays `name`.
            //
            // An alias already naming ANOTHER type is never clobbered (an explicit registration owns it).
            // One naming the same type is replaced, so a re-bound name and its alias always resolve one
            // definition. 🚨 A COLLECTIBLE type is newest-wins outright: every recompile of a dynamic node
            // mints a new assembly whose types carry the SAME full name, and keeping the first build's
            // alias kept a superseded type resolving long after a newer build replaced it (content then
            // fails every `Content is X` — the foreign-assembly content class).
            if (withAlias && fullName != name
                && (type.Assembly.IsCollectible || !aliases.TryGetValue(fullName, out var existing) || existing.Type == type))
                aliases = aliases.SetItem(fullName, definition);
            return new Layer(ByName.SetItem(name, definition), NameByType.SetItem(type, name), aliases);
        }
    }

    private Layer layer;

    public TypeRegistry(ITypeRegistry? parent)
    {
        this.parent = parent;
        parentLayer = parent as TypeRegistry;
        descriptions = parentLayer?.descriptions ?? new TypeDisplayMetadataCache();
        keyFunctionBuilder = new KeyFunctionBuilder(parentLayer?.keyFunctionBuilder);
        // The basic types are seeded ONLY at the root of a chain: every other registry resolves them
        // through its parent.
        layer = parentLayer is not null
            ? Layer.Empty
            : BasicTypes.Aggregate(Layer.Empty, (l, t) =>
                l.Bind(t, t.Name, new TypeDefinition(t, t.Name, keyFunctionBuilder, descriptions.For(t)), withAlias: false));
    }

    private static readonly Type[] BasicTypes =
    [
        typeof(string),
        typeof(int),
        typeof(long),
        typeof(short),
        typeof(byte),
        typeof(sbyte),
        typeof(uint),
        typeof(ulong),
        typeof(ushort),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(char),
        typeof(bool),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(TimeSpan),
        typeof(Guid),
        typeof(Uri),
        typeof(byte[]),
        typeof(RawJson),
        typeof(Nullable<>),
        typeof(MessageDelivery<>),
        typeof(Address),
        typeof(HeartBeatEvent),
        typeof(DeliveryFailure),
        typeof(RequestProgress),
        typeof(DisposeRequest)
    ];

    private static string FullNameOf(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    // ── Sharing with the mesh ───────────────────────────────────────────────────────────────
    // What a CLR type DECLARES about itself (display attribute, wordified name, icon, XML description)
    // is the same for every registry of a mesh, so the description store is created ONCE by the root of
    // the chain and handed down (Doc/Architecture/PortalHeapIsHubs). An instance, never static.
    private readonly TypeDisplayMetadataCache descriptions;

    /// <summary>The mesh's XML-documentation reader, shared down the registry chain with the descriptions.</summary>
    internal XmlDocsReader Docs => descriptions.Docs;

    // Chains to the parent registry's builder: a definition the parent built is the one this registry
    // would build, unless key-function factories were registered here (see Localize).
    private readonly KeyFunctionBuilder keyFunctionBuilder;

    // ── The merged view ─────────────────────────────────────────────────────────────────────

    public IEnumerable<KeyValuePair<string, ITypeDefinition>> Types => MergedTypes();

    public IEnumerable<KeyValuePair<string, ITypeDefinition>> OwnTypes => OwnEntries();

    /// <summary>
    /// The own layer as name → definition, plus the still-alive entries of the weak shadow: a hub still
    /// running on a superseded assembly needs its $type discriminators (dropping them is the silent
    /// skin-drop class the polymorphic resolver warns about). A dead weak reference is skipped — its
    /// metadata is gone, which is the SIGSEGV this shadow exists to prevent.
    /// </summary>
    private ImmutableDictionary<string, ITypeDefinition> OwnEntries()
    {
        var own = Volatile.Read(ref layer).ByName
            .ToImmutableDictionary(x => x.Key, x => (ITypeDefinition)x.Value);
        foreach (var (name, definition) in LiveDemotedDefinitions())
            if (!own.ContainsKey(name))
                own = own.Add(name, definition);
        return own;
    }

    private IEnumerable<KeyValuePair<string, ITypeDefinition>> MergedTypes()
    {
        // One snapshot of the own layer for the whole enumeration; the weak shadow is read only when a
        // demotion ever happened (it is empty for almost every registry).
        var own = Volatile.Read(ref layer).ByName;
        var demoted = Volatile.Read(ref demotedTypeByName) is null
            ? ImmutableDictionary<string, ITypeDefinition>.Empty
            : LiveDemotedDefinitions().Where(x => !own.ContainsKey(x.Key)).ToImmutableDictionary(x => x.Key, x => x.Value);
        var emitted = ImmutableHashSet.CreateBuilder<string>();
        if (parent is not null)
            foreach (var (name, inherited) in parent.Types)
                if (emitted.Add(name))
                    yield return new(name,
                        own.TryGetValue(name, out var mine) ? mine
                        : demoted.TryGetValue(name, out var shadowed) ? shadowed
                        : inherited);
        foreach (var (name, definition) in own)
            if (emitted.Add(name))
                yield return new(name, definition);
        foreach (var entry in demoted)
            if (emitted.Add(entry.Key))
                yield return entry;
    }

    /// <summary>The still-alive entries of the weak shadow, as name→definition pairs.</summary>
    private IEnumerable<KeyValuePair<string, ITypeDefinition>> LiveDemotedDefinitions()
    {
        foreach (var (name, weak) in Volatile.Read(ref demotedTypeByName) ?? Enumerable.Empty<KeyValuePair<string, WeakReference<Type>>>())
            if (weak.TryGetTarget(out var type)
                && demotedDefinitions.TryGetValue(type, out var definition))
                yield return new KeyValuePair<string, ITypeDefinition>(name, definition);
    }

    // ── The chain walk ──────────────────────────────────────────────────────────────────────

    /// <summary>This layer's definition of <paramref name="type"/> (or the weak shadow's), with its registered name.</summary>
    private TypeDefinition? OwnByType(Type type, out string? name)
    {
        var current = Volatile.Read(ref layer);
        if (current.NameByType.TryGetValue(type, out var registered)
            && current.ByName.TryGetValue(registered, out var definition)
            && definition.Type == type)
        {
            name = registered;
            return definition;
        }
        // The weak shadow: a type demoted at unload INITIATION still resolves while its assembly is
        // alive (a live hub running on a superseded build — issue #1169). The caller holds the Type, so
        // the entry is alive by construction.
        if (GetDemoted(type) is { } demoted)
        {
            name = demoted.CollectionName;
            return demoted;
        }
        name = null;
        return null;
    }

    /// <summary>This layer's definition registered under <paramref name="name"/>: the canonical name first, then the full-name alias, then the weak shadow.</summary>
    private TypeDefinition? OwnByName(string name, bool canonicalOnly)
    {
        var current = Volatile.Read(ref layer);
        if (current.ByName.TryGetValue(name, out var definition))
            return definition;
        if (!canonicalOnly && current.Aliases.TryGetValue(name, out var aliased))
            return aliased;
        return GetDemoted(name) ?? (canonicalOnly ? null : GetDemoted(name, alias: true));
    }

    /// <summary>
    /// The chain's definition of <paramref name="type"/> and the name it is registered under: own layer,
    /// then each parent's, exactly as registered — no generic synthesis and no creation on the way.
    /// </summary>
    private ITypeDefinition? ChainByType(Type type, out string? name)
    {
        for (var registry = this; ; registry = registry.parentLayer)
        {
            if (registry.OwnByType(type, out name) is { } definition)
                return definition;
            if (registry.parentLayer is not null)
                continue;
            if (registry.parent is { } foreign && foreign.GetTypeDefinition(type, create: false) is { } inherited)
            {
                name = foreign.TryGetCollectionName(type, out var foreignName) ? foreignName : inherited.CollectionName;
                return inherited;
            }
            name = null;
            return null;
        }
    }

    /// <summary>The chain's definition registered under <paramref name="name"/>, own layer first.</summary>
    private ITypeDefinition? ChainByName(string name, bool canonicalOnly = false)
    {
        for (var registry = this; ; registry = registry.parentLayer)
        {
            if (registry.OwnByName(name, canonicalOnly) is { } definition)
                return definition;
            if (registry.parentLayer is not null)
                continue;
            return registry.parent?.GetTypeDefinition(name);
        }
    }

    /// <summary>
    /// A definition resolved through the parent, as THIS registry serves it. It is the parent's very
    /// object — unless a registry between this one and the layer that OWNS the definition registered
    /// key-function factories that key a type the owner's conventions and factories leave keyless. That
    /// is a genuine override (another key), and it belongs to the layer whose factory decides it: the
    /// nearest such layer to this registry, exactly the precedence <see cref="KeyFunctionBuilder"/>
    /// applies. Binding it THERE makes the answer independent of which registry is asked first — a leaf
    /// read sees the middle layer's key whether or not the middle was ever queried itself. A cold path:
    /// no registry in core registers factories, and the walk is skipped unless one in the chain did.
    /// </summary>
    private ITypeDefinition? Localize(ITypeDefinition? definition)
    {
        if (definition is not TypeDefinition inherited
            || !keyFunctionBuilder.HasFactoriesInChain
            || !inherited.KeyFromBuilder
            || inherited.Key is not null
            || inherited.DisplayMetadata.HasConventionalKey)
            return definition;
        for (var registry = this; registry is not null; registry = registry.parentLayer)
        {
            // The owning layer reached: no layer below it keys the type differently.
            if (ReferenceEquals(registry.OwnByName(inherited.CollectionName, canonicalOnly: true), inherited)
                || ReferenceEquals(registry.OwnByType(inherited.Type, out _), inherited))
                return definition;
            if (registry.keyFunctionBuilder.HasOwnFactories
                && registry.keyFunctionBuilder.GetOwnFactoryKey(inherited.Type) is { } ownKey)
                return registry.BindKeyedOverride(inherited, ownKey);
        }
        return definition;
    }

    /// <summary>
    /// Binds <paramref name="inherited"/> keyed by <paramref name="key"/> into THIS layer. A type whose
    /// load context has already begun unloading — seen by this registry or by any ancestor, which may
    /// have demoted it before this layer first resolved it — goes to the weak shadow instead: the
    /// context's <c>Unloading</c> will never fire again, so a strong entry would root the superseded
    /// assembly for the registry's lifetime.
    /// </summary>
    private TypeDefinition BindKeyedOverride(TypeDefinition inherited, KeyFunction key)
    {
        if (IsUnloadingContext(inherited.Type))
        {
            if (GetDemoted(inherited.Type) is { Key: not null } demoted)
                return demoted;
            var shadowed = inherited.WithKey(key);
            Demote(shadowed.CollectionName, shadowed, alias: false);
            var fullName = FullNameOf(shadowed.Type);
            if (fullName != shadowed.CollectionName)
                Demote(fullName, shadowed, alias: true);
            return shadowed;
        }
        var keyed = inherited.WithKey(key);
        Mutate(l => l.ByName.TryGetValue(keyed.CollectionName, out var held) && held.Type == keyed.Type && !held.KeyFromBuilder
            ? null
            : l.Bind(keyed.Type, keyed.CollectionName, keyed));
        TrackCollectible(keyed.Type);
        return OwnByName(keyed.CollectionName, canonicalOnly: true) ?? keyed;
    }

    /// <summary>Replaces the own layer by compare-and-swap. <paramref name="change"/> returns null for "no change".</summary>
    private void Mutate(Func<Layer, Layer?> change)
    {
        var current = Volatile.Read(ref layer);
        while (true)
        {
            var next = change(current);
            if (next is null)
                return;
            var seen = Interlocked.CompareExchange(ref layer, next, current);
            if (ReferenceEquals(seen, current))
                return;
            current = seen;
        }
    }

    /// <summary>Binds a definition the chain does not hold yet; the first writer of a racing pair wins.</summary>
    private TypeDefinition BindNew(Type type, string name, TypeDefinition definition)
    {
        Mutate(l => l.NameByType.TryGetValue(type, out var held) && held == name
                    && l.ByName.TryGetValue(name, out var existing) && existing.Type == type
            ? null
            : l.Bind(type, name, definition));
        TrackCollectible(type);
        return OwnByType(type, out _) ?? definition;
    }

    /// <summary>
    /// Copies the registrations this registry OWNS — its own layer, never the inherited ones reachable
    /// through its parent — into <paramref name="target"/>.
    ///
    /// <para>Serves <c>MessageHubConfiguration.WithTypeRegistry</c>: a configuration that adopts a
    /// SHARED registry must not silently drop the types it registered before the swap. Skips every
    /// type the target's own chain already resolves, so an existing registration there — including a
    /// key function attached via <c>WithKeyFunction</c> — is never clobbered.</para>
    /// </summary>
    internal void CopyOwnRegistrationsTo(ITypeRegistry target)
    {
        foreach (var (name, definition) in Volatile.Read(ref layer).ByName)
            if (!target.TryGetCollectionName(definition.Type, out _))
                target.WithType(definition.Type, name);
    }

    // ── Writes ──────────────────────────────────────────────────────────────────────────────

    public ITypeRegistry WithType(Type type) => WithType(type, FormatType(type));

    public ITypeRegistry WithType(Type type, string typeName)
    {
        typeName ??= type.FullName!;
        var existing = ChainByType(type, out var registeredName);
        // The type's FULL name, asked for where the chain already registers the type under another name
        // and that full name already resolves to the same type (every bound definition indexes its
        // full-name alias): the caller asks for a read alias that exists. Registering it again made the
        // full name this hub's canonical name and stored a second definition per hub — the "full-name
        // read alias first, short name last" pairs of the mesh and graph type lists.
        if (existing is not null && registeredName != typeName && IsFullNameOf(type, typeName)
            && ChainByName(typeName)?.Type == type)
            return this;
        // A closed generic under the name the chain synthesises for it from its parts: the chain
        // already answers it by type (the synthesis) and by name (the bracket parse, which every name
        // lookup falls back to — GetTypeDefinition, GetKeyFunction, WithKeyFunction), so there is
        // nothing to hold. 🚨 EXCEPT when a derived-type search can reach it: the polymorphic resolver
        // discovers subtypes of a base only by enumerating `Types`, so a closure with a supertype it
        // could be serialised through (`Box<int> : Shape`) is held by the first layer that registers it
        // — descendants re-registering it find it through the chain and store nothing.
        if (existing is null && SynthesizedName(type) == typeName && !IsReachableByDerivedTypeSearch(type))
            return this;
        if (existing is not null && registeredName == typeName)
        {
            // 🚨 Only overrides are re-registered. The chain already registers this type under this
            // name; if the name resolves to that very definition too, the registration is identical and
            // stores NOTHING — the hub resolves it through its parent. (Before, the hub constructor's
            // `WithType(typeof(PingRequest), …)` and `Register<PingRequest>` each built a definition:
            // two per built-in type per hub, 1,215 PingRequest definitions for 608 hubs.)
            if (ReferenceEquals(ChainByName(typeName), existing))
                return this;
            // The name resolves to ANOTHER definition on the way up — an intermediate registry rebound it
            // to another type. Bind the existing definition here so the name resolves to this hub's type;
            // the object is shared, no new definition is built.
            if (existing is TypeDefinition shared && !IsUnloadingContext(type))
            {
                Mutate(l => l.Bind(type, typeName, shared));
                TrackCollectible(type);
                return this;
            }
        }
        var typeDefinition = new TypeDefinition(type, typeName, keyFunctionBuilder, descriptions.For(type));
        // A type whose context has already BEGUN unloading registers into the weak shadow: its
        // Unloading event will never fire again, so a strong entry could never be evicted — a
        // permanent root on a superseded assembly.
        if (IsUnloadingContext(type))
        {
            Demote(typeName, typeDefinition, alias: false);
            var fullName = FullNameOf(type);
            if (fullName != typeName)
                Demote(fullName, typeDefinition, alias: true);
            return this;
        }
        // New to the chain, or another name for it: the hub's own. A second identical call finds it
        // through the chain above and returns it.
        Mutate(l => l.Bind(type, typeName, typeDefinition));
        TrackCollectible(type);
        return this;
    }

    private static bool IsFullNameOf(Type type, string name) =>
        name == type.FullName || name == FullNameOf(type);

    /// <summary>
    /// The name <see cref="TryGetCollectionName"/> synthesises for a closed generic from names the chain
    /// already holds — its generic definition's and each argument's — or null when a part is unknown.
    /// Side-effect free: unlike the synthesis itself, it registers nothing.
    /// </summary>
    private string? SynthesizedName(Type type)
    {
        if (!type.IsConstructedGenericType)
            return null;
        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(Nullable<>) || ChainByType(definition, out var definitionName) is null)
            return null;
        var arguments = type.GetGenericArguments();
        var names = new string[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            var nullable = argument.IsGenericType && argument.GetGenericTypeDefinition() == typeof(Nullable<>);
            var named = nullable ? argument.GetGenericArguments()[0] : argument;
            var name = ChainByType(named, out var registered) is not null ? registered : SynthesizedName(named);
            if (name is null)
                return null;
            names[i] = nullable ? name + "?" : name;
        }
        return $"{definitionName}[{string.Join(',', names)}]";
    }

    /// <summary>
    /// Whether a derived-type search of the polymorphic resolver can find <paramref name="type"/>: it has
    /// a supertype other than <see cref="object"/> / <see cref="ValueType"/> and other than the
    /// <c>IEquatable</c> of itself every record implements. The resolver configures polymorphism for a
    /// declared type that is never <see cref="object"/> (<c>ShouldConfigurePolymorphism</c>), so a type
    /// without such a supertype is never searched for — only written through the open <c>object</c>
    /// door, whose discriminator is synthesised from the chain.
    /// </summary>
    private static bool IsReachableByDerivedTypeSearch(Type type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            if (baseType != typeof(object) && baseType != typeof(ValueType))
                return true;
        var selfEquatable = typeof(IEquatable<>).MakeGenericType(type);
        return type.GetInterfaces().Any(i => i != selfEquatable);
    }

    public ITypeRegistry WithTypes(params IEnumerable<KeyValuePair<string, Type>> types)
        => types.Aggregate((ITypeRegistry)this, (i, kvp) => i.WithType(kvp.Value, kvp.Key));

    public ITypeRegistry WithTypesFromAssembly(Type type, Func<Type, bool> filter) =>
        WithTypes(type.Assembly.GetTypes().Where(filter));

    public ITypeRegistry WithTypes(IEnumerable<Type> types)
    {
        foreach (var t in types)
            WithType(t);
        return this;
    }

    public string GetOrAddType(Type type, string? defaultName = null)
    {
        // Resolved by the chain — or, for a closed generic, synthesised from the names of its parts —
        // so nothing is stored for a type the chain can already name.
        if (TryGetCollectionName(type, out var typeName) && typeName is not null)
            return typeName;

        typeName = defaultName ?? FormatType(type);
        var definition = new TypeDefinition(type, typeName, keyFunctionBuilder, descriptions.For(type));
        // A late registration from an already-unloading context goes to the weak shadow — a strong
        // entry could never be evicted again (see WithType).
        if (IsUnloadingContext(type))
        {
            Demote(typeName, definition, alias: false);
            return typeName;
        }
        BindNew(type, typeName, definition);
        return OwnByType(type, out var bound) is not null ? bound! : typeName;
    }

    public ITypeRegistry WithKeyFunctionProvider(Func<Type, KeyFunction?> key)
    {
        keyFunctionBuilder.WithKeyFunction(key);
        return this;
    }

    public ITypeDefinition WithKeyFunction(string collection, KeyFunction keyFunction)
    {
        if ((ChainByName(collection) ?? SynthesizedDefinition(collection)) is not TypeDefinition typeDefinition)
            throw new ArgumentException($"Type {collection} not found");
        // An explicit key is an override: the keyed definition is this registry's own, the parent's
        // definition is untouched.
        var keyed = typeDefinition.WithKey(keyFunction);
        Mutate(l =>
        {
            var next = l with { ByName = l.ByName.SetItem(collection, keyed) };
            // Index the type too, so a lookup BY TYPE finds the keyed definition — but only where that
            // does not flip the name the type serialises with: an existing own entry is this registry's
            // canonical (write) name, and an inherited name other than `collection` is the chain's.
            if (!l.NameByType.ContainsKey(keyed.Type) && InheritedNameOf(keyed.Type) is var inherited
                && (inherited is null || inherited == collection))
                next = next with { NameByType = next.NameByType.SetItem(keyed.Type, collection) };
            var fullName = FullNameOf(keyed.Type);
            if (next.Aliases.TryGetValue(fullName, out var aliased) && ReferenceEquals(aliased, typeDefinition))
                next = next with { Aliases = next.Aliases.SetItem(fullName, keyed) };
            return next;
        });
        // This registry now holds the type strongly, so it must hear its context unload — or the keyed
        // definition roots a superseded collectible assembly while only the parent demotes.
        TrackCollectible(keyed.Type);
        return keyed;
    }

    // ── Lookups ─────────────────────────────────────────────────────────────────────────────

    public KeyFunction? GetKeyFunction(string collection)
    {
        if (Localize(ChainByName(collection, canonicalOnly: true)) is { } definition)
            return definition is TypeDefinition own ? own.Key : ForeignRoot()?.GetKeyFunction(collection);
        // A closed generic no layer holds is keyed by its synthesised name too (see WithType).
        return SynthesizedDefinition(collection) is { } synthesized ? GetKeyFunction(synthesized.Type) : null;
    }

    /// <summary>
    /// The definition the bracket parse yields for a closed-generic <paramref name="name"/> no layer
    /// holds (<see cref="TryGetType"/>), or null for any other name. What lets every name lookup serve a
    /// closed generic the chain synthesises instead of storing.
    /// </summary>
    private ITypeDefinition? SynthesizedDefinition(string name) =>
        name.EndsWith(']') && name.Contains('[') && TryGetType(name, out var definition) ? definition : null;

    public KeyFunction? GetKeyFunction(Type type)
    {
        var definition = Localize(ChainByType(type, out _));
        if (definition is TypeDefinition own)
            return own.Key ?? keyFunctionBuilder.GetKeyFunction(type);
        return (definition is not null ? ForeignRoot()?.GetKeyFunction(type) : null)
               ?? keyFunctionBuilder.GetKeyFunction(type);
    }

    /// <summary>The name the PARENT chain registers <paramref name="type"/> under, or null.</summary>
    private string? InheritedNameOf(Type type)
    {
        if (parentLayer is not null)
            return parentLayer.ChainByType(type, out var name) is not null ? name : null;
        return parent is not null && parent.TryGetCollectionName(type, out var foreignName) ? foreignName : null;
    }

    /// <summary>The non-TypeRegistry registry at the top of the chain, if there is one.</summary>
    private ITypeRegistry? ForeignRoot()
    {
        var registry = this;
        while (registry.parentLayer is not null)
            registry = registry.parentLayer;
        return registry.parent;
    }

    public bool TryGetType(string name, out ITypeDefinition? typeDefinition)
    {
        typeDefinition = Localize(ChainByName(name));
        if (typeDefinition != null)
            return true;
        // Handle nullable syntax (e.g., "Int32?" -> Nullable<Int32>)
        if (name.EndsWith('?'))
        {
            var underlyingName = name[..^1];
            if (TryGetType(underlyingName, out var underlyingDef) && underlyingDef != null)
            {
                var nullableType = typeof(Nullable<>).MakeGenericType(underlyingDef.Type);
                typeDefinition = new TypeDefinition(nullableType, name, keyFunctionBuilder, descriptions.For(nullableType));
                return true;
            }
            return false;
        }
        if (name.Contains('[') && name.EndsWith(']'))
        {
            var typeName = name.Substring(0, name.IndexOf('['));
            var baseType = GetTypeDefinition(typeName)?.Type;

            // If not found with full name, try without namespace (e.g., "System.Nullable`1" -> "Nullable`1")
            if (baseType == null && typeName.Contains('.'))
            {
                var shortName = typeName.Substring(typeName.LastIndexOf('.') + 1);
                baseType = GetTypeDefinition(shortName)?.Type;
            }

            if (baseType == null)
                return false;

            var genericArgs = SplitTopLevelArguments(name.Substring(
                name.IndexOf('[') + 1,
                name.Length - name.IndexOf('[') - 2));
            var genericTypeArgs = new Type[genericArgs.Length];

            for (var i = 0; i < genericArgs.Length; i++)
            {
                var argName = genericArgs[i].Trim();

                // Handle nullable syntax (e.g., "Int32?" -> "System.Nullable`1[Int32]")
                if (argName.EndsWith('?'))
                {
                    var underlyingTypeName = argName.Substring(0, argName.Length - 1);
                    if (TryGetType(underlyingTypeName, out var underlyingType) && underlyingType != null)
                        genericTypeArgs[i] = typeof(Nullable<>).MakeGenericType(underlyingType.Type);
                    else
                        return false;
                }
                else if (TryGetType(argName, out var genericTypeArg) && genericTypeArg != null)
                    genericTypeArgs[i] = genericTypeArg.Type;
                else
                    return false;
            }
            var type = baseType.MakeGenericType(genericTypeArgs);
            typeDefinition = Localize(ChainByType(type, out _))
                ?? new TypeDefinition(type, FormatType(type), keyFunctionBuilder, descriptions.For(type));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Splits a generic argument list at the commas at bracket depth zero only, so a nested generic
    /// argument (<c>ValueTuple`2[Int32,ValueTuple`2[Int32,Int32]]</c>) stays one argument and the names
    /// <see cref="FormatType"/> synthesises round-trip.
    /// </summary>
    private static string[] SplitTopLevelArguments(string arguments)
    {
        var parts = ImmutableArray.CreateBuilder<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(arguments[start..i]);
                    start = i + 1;
                    break;
            }
        }
        parts.Add(arguments[start..]);
        return parts.ToArray();
    }

    public Type? GetType(string name) => TryGetType(name, out var td) && td != null ? td.Type : null;

    public bool TryGetCollectionName(Type type, out string? typeName)
    {
        if (ChainByType(type, out typeName) is not null)
            return true;

        if (type.IsGenericType)
        {
            var genericTypeDefinition = type.GetGenericTypeDefinition();
            var genericArguments = type.GetGenericArguments();
            var genericTypeArguments = new string[genericArguments.Length];
            for (var i = 0; i < genericArguments.Length; i++)
            {
                // For nullable types, use the special formatting (e.g., "Int32?" instead of "System.Nullable`1[Int32]")
                if (genericArguments[i].IsGenericType && genericArguments[i].GetGenericTypeDefinition() == typeof(Nullable<>))
                    genericTypeArguments[i] = FormatType(genericArguments[i]);
                else if (!TryGetCollectionName(genericArguments[i], out var genericTypeArgument) || genericTypeArgument == null)
                    return false;
                else
                    genericTypeArguments[i] = genericTypeArgument;
            }
            typeName = $"{GetOrAddType(genericTypeDefinition)}[{string.Join(',', genericTypeArguments)}]";
            return true;
        }

        return false;
    }

    public ITypeDefinition? GetTypeDefinition(Type type, bool create = true, string? typeName = null)
    {
        if (Localize(ChainByType(type, out _)) is { } found)
            return found;
        if (!create)
            return null;

        typeName ??= FormatType(type);
        var created = new TypeDefinition(type, typeName, keyFunctionBuilder, descriptions.For(type));
        // A late registration from an already-unloading context goes to the weak shadow — a strong
        // entry could never be evicted again (see WithType).
        if (IsUnloadingContext(type))
        {
            Demote(created.CollectionName, created, alias: false);
            return created;
        }
        return BindNew(type, created.CollectionName, created);
    }

    public ITypeDefinition? GetTypeDefinition(string typeName) =>
        Localize(ChainByName(typeName)) ?? SynthesizedDefinition(typeName);

    public string FormatType(Type mainType)
    {
        // Already registered somewhere in the chain under a name (e.g., basic types like "Int32")?
        if (ChainByType(mainType, out var registeredName) is not null && registeredName is not null)
            return registeredName;

        // 🎯 The $type discriminator defaults to the SHORT type name (type.Name), NOT the
        // namespace-qualified full name. An unregistered type then serialises as e.g. "ThreadViewModel"
        // — which a reading hub that registered the type under its short name (the standard
        // WithType(typeof(T), nameof(T)) shape) RESOLVES — instead of "MeshWeaver.AI.ThreadViewModel",
        // which mismatches the short-name registration → the value comes back as an untyped JsonElement
        // (renders empty / reactive waits time out — the chat-vanish / prod storm wedge class).
        var mainTypeName = (mainType.Name ?? mainType.FullName!).Replace('+', '.');
        if (!mainType.IsGenericType || mainType.IsGenericTypeDefinition)
            return mainTypeName;

        var typeDefinition = mainType.GetGenericTypeDefinition();
        if (typeDefinition == typeof(Nullable<>))
            return FormatType(mainType.GetGenericArguments()[0]) + "?";

        return $"{GetOrAddType(typeDefinition)}[{string.Join(',', mainType.GetGenericArguments().Select(valueType => GetOrAddType(valueType)))}]";
    }

    // ── The weak shadow ─────────────────────────────────────────────────────────────────────
    // Entries DEMOTED from the own layer when their collectible AssemblyLoadContext BEGAN unloading.
    // AssemblyLoadContext.Unload() is COOPERATIVE: the Unloading event fires at INITIATION, while every
    // hub whose configuration lives in that assembly is still running on it (a NodeType recompile
    // evicts the superseded context the moment the new build loads — see
    // CompilationCacheService.EvictSupersededContexts). Removing the entries outright (issue #1169)
    // stripped those live hubs of their own registrations mid-flight ("Type StorePackage is unknown").
    //
    // The shadow keeps serving a demoted type for EXACTLY as long as anything else keeps its assembly
    // alive, without rooting it:
    //  - demotedDefinitions is a ConditionalWeakTable keyed by the Type — the definition (which strongly
    //    references the Type) lives precisely as long as the Type does, and the table itself holds no
    //    strong root (DependentHandle semantics).
    //  - demotedTypeByName / demotedAliasByName map names to WeakReference<Type>. A dead target is
    //    skipped and pruned.
    private readonly ConditionalWeakTable<Type, TypeDefinition> demotedDefinitions = new();
    // Created on the first demotion only: most registries never see an unload.
    private ConcurrentDictionary<string, WeakReference<Type>>? demotedTypeByName;
    private ConcurrentDictionary<string, WeakReference<Type>>? demotedAliasByName;

    private ConcurrentDictionary<string, WeakReference<Type>> DemotedNames(bool alias) => alias
        ? LazyInitializer.EnsureInitialized(ref demotedAliasByName, () => new())
        : LazyInitializer.EnsureInitialized(ref demotedTypeByName, () => new());

    // Contexts whose Unloading has already fired. A LATE registration from such a context must go
    // straight to the weak shadow: the Unloading event will never fire again, so a strong entry would
    // be an un-evictable permanent root — the exact leak the eviction exists to close.
    private readonly ConditionalWeakTable<AssemblyLoadContext, object> unloadingContexts = new();

    /// <summary>Whether the type's collectible load context has already begun unloading.</summary>
    /// <remarks>
    /// Read along the WHOLE chain: only a registry that held a type from the context heard its
    /// <c>Unloading</c>, and a descendant first binding a type from it afterwards (a keyed override, a
    /// rename) must see that the event is already gone, or it binds a strong entry nothing will evict.
    /// </remarks>
    private bool IsUnloadingContext(Type type)
    {
        if (!type.Assembly.IsCollectible || AssemblyLoadContext.GetLoadContext(type.Assembly) is not { } context)
            return false;
        for (var registry = this; registry is not null; registry = registry.parentLayer)
            if (registry.unloadingContexts.TryGetValue(context, out _))
                return true;
        return false;
    }

    /// <summary>Places one entry into the weak shadow (see the field docs).</summary>
    private void Demote(string name, TypeDefinition definition, bool alias)
    {
        demotedDefinitions.AddOrUpdate(definition.Type, definition);
        DemotedNames(alias)[name] = new WeakReference<Type>(definition.Type);
    }

    /// <summary>
    /// Weak-shadow lookup by name. Skips (and prunes) dead entries — a dead weak reference means the
    /// assembly's metadata is gone, and serving it would be the freed-metadata dereference the shadow
    /// exists to prevent.
    /// </summary>
    private TypeDefinition? GetDemoted(string name, bool alias = false)
    {
        var map = alias ? Volatile.Read(ref demotedAliasByName) : Volatile.Read(ref demotedTypeByName);
        if (map is null || !map.TryGetValue(name, out var weak))
            return null;
        if (weak.TryGetTarget(out var type)
            && demotedDefinitions.TryGetValue(type, out var definition))
            return definition;
        map.TryRemove(new KeyValuePair<string, WeakReference<Type>>(name, weak));
        return null;
    }

    /// <summary>Weak-shadow lookup by type (alive by construction — the caller holds the Type).</summary>
    private TypeDefinition? GetDemoted(Type type)
        => demotedDefinitions.TryGetValue(type, out var definition) ? definition : null;

    // Collectible load contexts this registry currently holds types from, so it subscribes to each
    // one's Unloading exactly once. ConditionalWeakTable and NOT a dictionary: an ALC used as a
    // dictionary KEY is a strong root, which is the very leak this eviction exists to close.
    private readonly ConditionalWeakTable<AssemblyLoadContext, object> trackedContexts = new();

    /// <summary>
    /// A registry entry for a type from a COLLECTIBLE assembly (a dynamically compiled node — every
    /// recompile mints a new assembly with a new CLR identity) strongly roots that assembly's
    /// <see cref="AssemblyLoadContext"/>: the context can never be collected (a leak), and after it IS
    /// unloaded anything walking this registry dereferences <b>freed metadata</b> → SIGSEGV
    /// (<c>PolymorphicTypeInfoResolver.ComputeDerivedTypes</c> enumerates <see cref="Types"/>; a CI core
    /// dump caught it faulting there, exit=139). So the registry that HOLDS a collectible type
    /// subscribes to its context's <c>Unloading</c> and DEMOTES that context's entries into the weak
    /// shadow (<see cref="EvictLoadContext"/>). Only the registry holding the type subscribes — a hub
    /// that resolves it through its parent holds nothing — and the event holds a delegate to THIS
    /// registry, never the reverse.
    /// </summary>
    private void TrackCollectible(Type type)
    {
        if (!type.Assembly.IsCollectible)
            return;
        var context = AssemblyLoadContext.GetLoadContext(type.Assembly);
        if (context is null || !context.IsCollectible)
            return;
        // ONE atomic claim per context, and only its winner subscribes (issue #6891: a check-then-act
        // let two concurrent first registrations both subscribe, and the second Add threw).
        if (trackedContexts.TryAdd(context, Sentinel))
            context.Unloading += EvictLoadContext;
    }

    private static readonly object Sentinel = new();

    /// <summary>
    /// DEMOTES every own entry whose type came from <paramref name="context"/> into the weak shadow.
    /// Runs from the context's <c>Unloading</c> event — i.e. at unload INITIATION, before the metadata
    /// is freed, while <c>type.Assembly</c> is still safe to read. Demote, never remove (issue #1169).
    /// </summary>
    public void EvictLoadContext(AssemblyLoadContext context)
    {
        // 🚨 NOTHING MAY THROW OUT OF HERE — this runs on the FINALIZER THREAD.
        //
        // `Unloading` fires from an explicit `Unload()` AND from `AssemblyLoadContext.Finalize()`, so at
        // process teardown this body executes during finalization, where an escaping exception
        // terminates the process (observed under the plugin-tester suite: a NullReferenceException out
        // of ConditionalWeakTable.TryAdd, whose own Container was finalized first, threw away a whole
        // green suite). There is nothing to salvage once the process is going away, so every failure
        // of the sweep degrades to "not swept". A blanket catch on purpose: the value is that the
        // finalizer thread survives.
        try
        {
            EvictLoadContextCore(context);
        }
        catch (Exception ex)
        {
            // Trace, not a logger: this registry has none, and a logger's own state is exactly as
            // likely to be torn down as the table that just failed.
            System.Diagnostics.Debug.WriteLine(
                $"TypeRegistry.EvictLoadContext: sweep abandoned ({ex.GetType().Name}: {ex.Message})"
                + " — the registry is being torn down with the process.");
        }
    }

    private void EvictLoadContextCore(AssemblyLoadContext context)
    {
        // Mark FIRST so a registration racing this sweep routes to the weak shadow instead of
        // re-adding a strong, never-again-evictable entry.
        unloadingContexts.TryAdd(context, Sentinel);
        while (true)
        {
            var current = Volatile.Read(ref layer);
            var names = current.ByName.Where(x => BelongsTo(x.Value.Type, context)).ToArray();
            var aliases = current.Aliases.Where(x => BelongsTo(x.Value.Type, context)).ToArray();
            var types = current.NameByType.Keys.Where(t => BelongsTo(t, context)).ToArray();
            if (names.Length == 0 && aliases.Length == 0 && types.Length == 0)
                break;
            var next = new Layer(
                current.ByName.RemoveRange(names.Select(x => x.Key)),
                current.NameByType.RemoveRange(types),
                current.Aliases.RemoveRange(aliases.Select(x => x.Key)));
            if (!ReferenceEquals(Interlocked.CompareExchange(ref layer, next, current), current))
                continue;
            foreach (var (name, definition) in names)
                Demote(name, definition, alias: false);
            foreach (var (name, definition) in aliases)
                Demote(name, definition, alias: true);
            break;
        }
        // Opportunistically prune weak entries whose assemblies have since been collected — keeps the
        // shadow's name maps bounded by LIVE demoted types, not by history.
        foreach (var map in new[] { Volatile.Read(ref demotedTypeByName), Volatile.Read(ref demotedAliasByName) })
            if (map is not null)
                foreach (var (name, weak) in map)
                    if (!weak.TryGetTarget(out _))
                        map.TryRemove(new KeyValuePair<string, WeakReference<Type>>(name, weak));
        trackedContexts.Remove(context);
        context.Unloading -= EvictLoadContext;
    }

    private static bool BelongsTo(Type type, AssemblyLoadContext context) =>
        type.Assembly.IsCollectible && AssemblyLoadContext.GetLoadContext(type.Assembly) == context;
}
