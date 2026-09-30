using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Domain;
using MeshWeaver.Utils;
using Namotion.Reflection;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// The immutable, hub-independent part of a <see cref="TypeDefinition"/>: what the CLR type itself
/// declares about how it is displayed — its <see cref="DisplayAttribute"/>, the wordified fallback
/// name, its icon, and its (deferred) XML-doc description.
///
/// <para>One instance per CLR type per MESH. Every hub owns its own <see cref="TypeRegistry"/>
/// (collection names, key functions and owning addresses are per-hub registration state), but what
/// the type declares is the same for all of them, so a hub-level registry reads its description from
/// the <see cref="TypeDisplayMetadataCache"/> of the mesh-level registry it chains to rather than
/// rebuilding it — a heap census of a retained-hub fixture counted 3,662,876 definitions for 378 CLR
/// types, each with its own description graph (see <c>Doc/Architecture/PortalHeapIsHubs</c>).</para>
/// </summary>
public sealed class TypeDisplayMetadata
{
    internal TypeDisplayMetadata(Type type)
    {
        Display = type.GetCustomAttribute<DisplayAttribute>();
        FallbackName = type.Name.Wordify();
        var iconAttribute = type.GetCustomAttribute<IconAttribute>();
        if (iconAttribute != null)
            Icon = new Icon(iconAttribute.Provider, iconAttribute.Id);
        // 🚨 LAZY, and it must stay lazy — see the note in TypeDefinition's constructor
        // (FutuRe.Test teardown SIGSEGV): the XML-docs read must never run on the hub-construction path.
        Description = new(() => XmlDocs.Summary(type));
    }

    /// <summary>
    /// The type's <see cref="DisplayAttribute"/>, if declared. The DECLARATION is shared, never the
    /// values it resolves: <c>GetName</c>/<c>GetGroupName</c> read <c>ResourceType</c> properties at
    /// use time, so each definition resolves them itself.
    /// </summary>
    internal DisplayAttribute? Display { get; }

    /// <summary>The wordified type name, used when no <see cref="DisplayAttribute"/> name is declared.</summary>
    internal string FallbackName { get; }

    /// <summary>The icon declared by the type's icon attribute, or <c>null</c>.</summary>
    internal Icon? Icon { get; }

    /// <summary>The type's XML-doc summary, resolved on FIRST READ only.</summary>
    internal Lazy<string> Description { get; }
}

/// <summary>
/// The per-mesh store of <see cref="TypeDisplayMetadata"/>s, keyed by CLR type.
///
/// <para>🚨 An INSTANCE owned by the mesh-level <see cref="TypeRegistry"/> (itself a mesh-scoped
/// singleton registered by <c>MeshBuilder</c>) and handed down the registry parent chain to every
/// hub-level registry — never a <c>static</c> table, which would outlive the mesh and bleed across
/// meshes and tests (<c>Doc/Architecture/NoStaticState</c>).</para>
///
/// <para>A <see cref="ConditionalWeakTable{TKey,TValue}"/>, not a dictionary: a runtime-compiled
/// NodeType's types live in a COLLECTIBLE load context, and a strong key would root that context
/// for the lifetime of the mesh. The weak key lets the description leave with its assembly.</para>
/// </summary>
internal sealed class TypeDisplayMetadataCache
{
    private readonly ConditionalWeakTable<Type, TypeDisplayMetadata> descriptions = new();

    /// <summary>Returns the one description of <paramref name="type"/> in this mesh, creating it on first use.</summary>
    /// <param name="type">The CLR type being described.</param>
    /// <returns>The shared description.</returns>
    public TypeDisplayMetadata For(Type type)
        => descriptions.GetValue(type, static t => new TypeDisplayMetadata(t));
}
