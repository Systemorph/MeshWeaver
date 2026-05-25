using System.Text.Json.Serialization;
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
    /// EmbeddedResource collections default to false. FileSystem and other writable sources default to true.
    /// <para>🚨 <see cref="JsonIgnoreCondition.Never"/> overrides the hub serializer's
    /// global <c>DefaultIgnoreCondition = WhenWritingDefault</c>. Without it, a
    /// sender-side <c>IsEditable = false</c> matches <c>bool</c>'s type-default and is
    /// dropped from the wire payload; the receiver deserializes against the C# property
    /// initializer <c>= true</c> and a read-only collection silently becomes writable.
    /// Same applies to any other <c>bool</c> property whose meaningful value is false but
    /// whose C# default is true — be explicit.</para>
    /// </summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool IsEditable { get; set; } = true;

    /// <summary>
    /// Whether this collection's files should be served under the /static route.
    /// </summary>
    public bool IsStatic { get; set; }

    /// <summary>
    /// Whether this collection should be visible to child nodes in the hierarchy.
    /// When false, only the node that owns the collection can see it.
    /// Storage collections and embedded resource collections typically set this to false.
    /// <para>Same wire-default trap as <see cref="IsEditable"/>: C# property default
    /// is true; <c>WhenWritingDefault</c> would drop <c>false</c> from the wire.</para>
    /// </summary>
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool ExposeInChildren { get; set; } = true;

    /// <summary>
    /// Additional provider-specific settings
    /// </summary>
    public Dictionary<string, string>? Settings { get; set; }
}
