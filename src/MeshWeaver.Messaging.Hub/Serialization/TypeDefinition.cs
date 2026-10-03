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
        : this(elementType, typeName, keyFunctionBuilder, new TypeDisplayMetadata(elementType))
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

        Key = new(() => keyFunctionBuilder.GetKeyFunction(Type)!);

        // 🚨 LAZY, and it must stay lazy — this is the FutuRe.Test teardown SIGSEGV (exit=139).
        // XmlDocs.Summary reflects into the type's assembly XML documentation. Constructing a
        // TypeDefinition happens for EVERY type registered on EVERY hub, i.e. squarely on the hub
        // CONSTRUCTION path (MessageHub.ctor → Register → WithTypeAndRelatedTypesFor →
        // TypeRegistry.WithType → here). Compiled scope NodeTypes live in COLLECTIBLE ALCs, so when
        // one is unloading while another hub is being built, that read walks metadata for an
        // assembly whose image is going away and the process dies with SIGSEGV inside
        // Namotion.Reflection (dump: ToXmlDocsContent → StringBuilder.ToString on the GC thread).
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
    /// <exception cref="InvalidOperationException">Thrown when no key mapping is defined for the type.</exception>
    public virtual object GetKey(object instance) =>
        Key.Value.Function(instance)
        ?? throw new InvalidOperationException(
            $"No key mapping is defined for type {CollectionName}. Please specify in the configuration of the data sources source.");

    /// <summary>
    /// Returns the CLR type of the key produced by the type's configured key function.
    /// </summary>
    /// <returns>The key's CLR type.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no key mapping is defined for the type.</exception>
    public Type GetKeyType() =>
        Key.Value.KeyType
        ?? throw new InvalidOperationException(
            $"No key mapping is defined for type {CollectionName}. Please specify in the configuration of the data sources source.");
    internal Lazy<KeyFunction> Key { get; init; }
}
