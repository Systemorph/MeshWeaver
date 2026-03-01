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
    /// </summary>
    public bool IsEditable { get; set; } = true;

    /// <summary>
    /// Whether this collection's files should be served under the /static route.
    /// </summary>
    public bool IsStatic { get; set; }

    /// <summary>
    /// Whether this collection should be visible to child nodes in the hierarchy.
    /// When false, only the node that owns the collection can see it.
    /// Storage collections and embedded resource collections typically set this to false.
    /// </summary>
    public bool ExposeInChildren { get; set; } = true;

    /// <summary>
    /// Additional provider-specific settings
    /// </summary>
    public Dictionary<string, string>? Settings { get; set; }
}
