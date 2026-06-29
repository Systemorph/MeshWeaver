using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;


/// <summary>
/// Declarative configuration for a content collection: which backing store supplies its files,
/// how it is named and ordered, and which capabilities (editing, static serving, child exposure)
/// it offers.
/// </summary>
public record ContentCollectionConfig
{
    /// <summary>The backing store kind (e.g. <c>"FileSystem"</c>, <c>"EmbeddedResource"</c>, <c>"Hub"</c>, <c>"Mapped"</c>) used to select the stream-provider factory.</summary>
    public required string SourceType { get; set; }
    /// <summary>The collection's unique key within its hub.</summary>
    public required string Name { get; set; }
    /// <summary>Optional human-friendly name; when <c>null</c> a word-split of <see cref="Name"/> is used.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Optional base path/prefix into the backing store that scopes this collection's files.</summary>
    public string? BasePath { get; set; }
    /// <summary>Sort order used when listing collections; lower values come first.</summary>
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
