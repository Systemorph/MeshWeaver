using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Describes a CLR type known to the mesh: its serialization collection name, display metadata
/// (name, group, order, icon, description) derived from attributes/XML docs, optional owning address,
/// and the key function used to identify instances of the type.
/// </summary>
public record TypeDefinition : ITypeDefinition
{
    /// <summary>
    /// Initializes a type definition, deriving display name, group, order, icon and description from the
    /// type's <see cref="DisplayAttribute"/>, icon attribute and XML doc summary.
    /// </summary>
    /// <param name="elementType">The CLR type being described.</param>
    /// <param name="typeName">The collection / serialization name for the type.</param>
    /// <param name="keyFunctionBuilder">Builder that resolves the key function for instances of the type.</param>
    /// <remarks>
    /// A definition built through this constructor owns a PRIVATE <see cref="TypeDisplayMetadata"/>.
    /// Registries use the internal overload instead, which takes the mesh's shared description.
    /// </remarks>
    public TypeDefinition(Type elementType, string typeName, KeyFunctionBuilder keyFunctionBuilder)
        : this(elementType, typeName, keyFunctionBuilder, new TypeDisplayMetadata(elementType, new XmlDocsReader()))
    {
    }

    /// <summary>
    /// Initializes a type definition over an already-built — normally mesh-shared —
    /// <see cref="TypeDisplayMetadata"/> (see <see cref="TypeDisplayMetadataCache"/>).
    /// </summary>
    /// <param name="elementType">The CLR type being described.</param>
    /// <param name="typeName">The collection / serialization name for the type.</param>
    /// <param name="keyFunctionBuilder">Builder that resolves the key function for instances of the type.</param>
    /// <param name="displayMetadata">The description of <paramref name="elementType"/>.</param>
    internal TypeDefinition(Type elementType, string typeName, KeyFunctionBuilder keyFunctionBuilder, TypeDisplayMetadata displayMetadata)
    {
        Type = elementType;
        CollectionName = typeName;
        DisplayMetadata = displayMetadata;

        // GetName/GetGroupName resolve ResourceType properties at use time. The description shares
        // the declaration, never its translated values, so two viewers can still receive different text.
        DisplayName = displayMetadata.Display?.GetName() ?? displayMetadata.FallbackName;

        GroupName = displayMetadata.Display?.GetGroupName();
        Order = displayMetadata.Display?.GetOrder();
        Icon = displayMetadata.Icon;

        // NULLABLE on purpose: a type with no [Key], no Id and no SystemName property has no key
        // function, and that is a legitimate registration as long as nothing asks it for a key
        // (most TypeRegistry entries are never a data-source collection). A `!` here used to hide
        // the null until GetKey dereferenced it — a bare NullReferenceException at every
        // activation of a hub whose data source registered such a type, naming nothing
        // (Systemorph/MeshWeaver#6643). GetKey/GetKeyType now refuse it BY NAME.
        // Resolved on first use from the builder (see Key), exactly once.
        keyCell = new(() => keyFunctionBuilder.GetKeyFunction(elementType));
        keyFromBuilder = true;

        // 🚨 LAZY, and it must stay lazy — this is the FutuRe.Test teardown SIGSEGV (exit=139).
        // XmlDocs.Summary reflects into the type's assembly XML documentation. Constructing a
        // TypeDefinition happens for EVERY type registered on EVERY hub, i.e. squarely on the hub
        // CONSTRUCTION path (MessageHub.ctor → Register → WithTypeAndRelatedTypesFor →
        // TypeRegistry.WithType → here). Compiled scope NodeTypes live in COLLECTIBLE ALCs, so when
        // one is unloading while another hub is being built, that read walks metadata for an
        // assembly whose image is going away and the process dies with SIGSEGV inside
        // Namotion.Reflection (dump: ToXmlDocsContent → StringBuilder.ToString on the GC thread). The
        // third-party reader has since been replaced by XmlDocs' own (Doc/Architecture/XmlDocumentationReader);
        // the read still reflects over the type, so it stays off the construction path.
        //
        // HostedHubsCollection.CloseCreation() does NOT cover this: it is a TEARDOWN guard, flipped
        // when the owning hub's disposal begins. The crash reproduces on a BUILD path
        // (MeshBuilder.BuildHub → … → HostedHubsCollection.CreateHub), where creation is legitimate
        // and refusing it would be wrong. Keeping the XML-docs read off the construction path is
        // what actually closes the window.
        //
        // Description is pure display metadata with a single consumer, so deferring costs nothing.
        description = displayMetadata.Description;
    }

    /// <summary>
    /// Initializes a type definition as above and additionally associates it with an owning address.
    /// </summary>
    /// <param name="elementType">The CLR type being described.</param>
    /// <param name="typeName">The collection / serialization name for the type.</param>
    /// <param name="keyFunctionBuilder">Builder that resolves the key function for instances of the type.</param>
    /// <param name="address">The address that owns instances of this type.</param>
    public TypeDefinition(Type elementType, string typeName, KeyFunctionBuilder keyFunctionBuilder, Address address)
        : this(elementType, typeName, keyFunctionBuilder)
    {
        Address = address;
    }


    /// <summary>The CLR type being described.</summary>
    public Type Type { get; }
    /// <summary>
    /// The hub-independent description of <see cref="Type"/> this definition reads its display
    /// metadata from. Every registry of one mesh hands out the SAME instance per CLR type, while the
    /// definition itself — collection name, key function, owning address — stays per registry.
    /// </summary>
    public TypeDisplayMetadata DisplayMetadata { get; }
    /// <summary>The human-readable display name, from <see cref="DisplayAttribute"/> or the wordified type name.</summary>
    public string DisplayName { get; }
    /// <summary>The collection / serialization name used to identify this type in the mesh.</summary>
    public string CollectionName { get; }
    /// <summary>The icon associated with the type, if an icon attribute is present; otherwise <c>null</c>.</summary>
    public object? Icon { get; init; }
    /// <summary>The address that owns instances of this type, if any.</summary>
    public Address? Address { get; init; }

    /// <summary>The display ordering hint, from <see cref="DisplayAttribute"/>, if specified.</summary>
    public int? Order { get; }
    /// <summary>The display group name, from <see cref="DisplayAttribute"/>, if specified.</summary>
    public string? GroupName { get; }
    /// <summary>
    /// The description, taken from the type's XML documentation summary. Resolved on FIRST READ,
    /// never during construction — see the note in the constructor (FutuRe.Test teardown SIGSEGV).
    /// </summary>
    public string Description
    {
        get => description.Value;
        init => description = new(value);
    }

    // Backing store. `init` accepts an already-materialised value (record `with` / deserialization),
    // so an explicitly-supplied Description still wins and is never recomputed from XML docs.
    private readonly Lazy<string> description;

    /// <summary>
    /// Returns the key identifying the given instance using the type's configured key function.
    /// </summary>
    /// <param name="instance">The instance to extract the key from.</param>
    /// <returns>The key value for the instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no key mapping is defined for the type, or when the instance's key is null.
    /// Both messages name the CLR type and the collection.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="instance"/> is null.</exception>
    public virtual object GetKey(object instance)
    {
        var keyFunction = Key ?? throw NoKeyMapping();
        if (instance is null)
            throw new ArgumentNullException(nameof(instance),
                $"Cannot compute the key of a null instance of type {Type.FullName} (collection '{CollectionName}'): "
                + "the instance sequence handed to this collection contains a null element.");
        return keyFunction.Function(instance)
               ?? throw new InvalidOperationException(
                   $"An instance of type {Type.FullName} (collection '{CollectionName}') has a null key. "
                   + "Every instance of a data-source collection needs a non-null key value.");
    }

    /// <summary>
    /// Returns the key identifying the given instance, or <c>null</c> when the instance carries no key value.
    /// </summary>
    /// <remarks>
    /// <see cref="GetKey"/> was the only key hook before this member existed, so a subclass may
    /// override it alone. Such a subclass keeps its key: this base implementation then DISPATCHES to
    /// that override (and inherits its behaviour for a missing key — return <c>null</c> or throw)
    /// instead of reading the configured key mapping behind its back. The base <see cref="GetKey"/>
    /// never calls this member, so overriding both, or either, cannot recurse.
    /// </remarks>
    /// <param name="instance">The instance to extract the key from.</param>
    /// <returns>The key value, or <c>null</c> when the instance has none.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no key mapping is defined for the type.</exception>
    public virtual object? GetKeyOrNull(object instance) =>
        GetKeyIsOverridden() ? GetKey(instance) : (Key ?? throw NoKeyMapping()).Function(instance);

    // Whether the runtime type overrides GetKey(object). The exact type cannot, so only a subclass
    // pays the reflection read — on use, never on the hub-construction path (see the description
    // note above).
    private bool GetKeyIsOverridden() =>
        GetType() != typeof(TypeDefinition)
        && GetType().GetMethod(nameof(GetKey), [typeof(object)])!.DeclaringType != typeof(TypeDefinition);

    /// <summary>
    /// Returns the CLR type of the key produced by the type's configured key function.
    /// </summary>
    /// <returns>The key's CLR type.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no key mapping is defined for the type.</exception>
    public Type GetKeyType() => (Key ?? throw NoKeyMapping()).KeyType;

    private InvalidOperationException NoKeyMapping() =>
        new($"No key mapping is defined for type {Type.FullName} (collection '{CollectionName}'). "
            + "A type served as a data-source collection needs a key: mark one property [Key], "
            + "name it Id or SystemName, or configure WithKey(...) on its type source.");

    // The key function, resolved from the registry's builder on first use. A Lazy, and the field
    // never changes after construction: (1) the record's synthesized Equals/GetHashCode read every
    // instance field, so cache state that flipped on the first read would change a definition's
    // hash while it sits in a hash set — the reference to this cell is stable instead; (2) the
    // builder runs arbitrary key-function providers, so concurrent first readers must not run it
    // twice and publish two different key functions — Lazy's ExecutionAndPublication runs it once.
    private Lazy<KeyFunction?> keyCell;
    // Whether keyCell resolves from a builder's conventions (false once an explicit key is fixed).
    private bool keyFromBuilder;

    /// <summary>The key function of this type, resolved from the registry's builder on first use.</summary>
    internal KeyFunction? Key => keyCell.Value;

    /// <summary>
    /// True while this definition's key comes from the key-function CONVENTIONS of a registry's
    /// builder rather than an explicit key function — i.e. it is the plain definition any registry
    /// would build for the type. What lets a hub-level registry reuse its parent's definition of a
    /// type whose key the conventions decide instead of holding a copy (TypeRegistry.WithType).
    /// </summary>
    internal bool KeyFromBuilder => keyFromBuilder;

    /// <summary>
    /// True when the description and icon are the ones the type itself declares — neither was
    /// overridden on this definition (<c>with { Description = … }</c> / <c>with { Icon = … }</c>).
    /// </summary>
    internal bool DescribedByType =>
        ReferenceEquals(description, DisplayMetadata.Description) && Equals(Icon, DisplayMetadata.Icon);

    /// <summary>A copy of this definition whose key is <paramref name="keyFunction"/>.</summary>
    /// <param name="keyFunction">The explicit key function.</param>
    /// <returns>The copy.</returns>
    internal TypeDefinition WithKey(KeyFunction keyFunction)
    {
        var copy = this with { };
        copy.keyCell = new(keyFunction);
        copy.keyFromBuilder = false;
        return copy;
    }
}
