using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;


public record ContentCollectionConfig
{
    public required string SourceType { get; set; }
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? BasePath { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// If specified, this collection should be loaded from the specified address.
    /// This allows referencing collections from other hubs (e.g., Documentation, Northwind).
    /// </summary>
    public Address? Address { get; set; }

    /// <summary>
    /// Whether this collection supports editing (file upload, delete, etc.).
    /// <para>🚨 Default is <c>false</c> — must be set <c>true</c> explicitly at every
    /// writable callsite. Reason: the hub serializer uses
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault"/>,
    /// which drops any value that matches the type-default (<c>false</c> for <c>bool</c>).
    /// If the C# initializer were <c>= true</c>, a sender-side <c>IsEditable = false</c>
    /// would be dropped from the wire and the receiver would deserialize back to the C#
    /// initializer <c>= true</c> — silently flipping a read-only collection to writable.
    /// Aligning the C# default with the type-default keeps the wire safe; the cost is
    /// explicit <c>IsEditable = true</c> at every editable callsite.</para>
    /// </summary>
    public bool IsEditable { get; set; }

    /// <summary>
    /// Whether this collection's files should be served under the /static route.
    /// </summary>
    public bool IsStatic { get; set; }

    /// <summary>
    /// Whether this collection should be visible to child nodes in the hierarchy.
    /// When false, only the node that owns the collection can see it.
    /// <para>Default <c>false</c> for the same wire-default reason as <see cref="IsEditable"/>:
    /// keep the C# initializer matching <c>bool</c>'s type-default so <c>false</c> isn't
    /// silently flipped to <c>true</c> across the hub boundary. Set <c>= true</c>
    /// explicitly at callsites that want the collection inherited by children.</para>
    /// </summary>
    public bool ExposeInChildren { get; set; }

    /// <summary>
    /// Additional provider-specific settings. <see cref="IReadOnlyDictionary{TKey,TValue}"/>
    /// so callers can pass either a <c>Dictionary&lt;,&gt;</c> literal or an
    /// <c>ImmutableDictionary&lt;,&gt;</c> without converting.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Settings { get; set; }
}
