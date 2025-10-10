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
    /// Additional provider-specific settings
    /// </summary>
    public Dictionary<string, string>? Settings { get; set; }
}
