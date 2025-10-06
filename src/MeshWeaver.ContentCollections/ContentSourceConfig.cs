namespace MeshWeaver.ContentCollections;


public class ContentSourceConfig
{
    public string SourceType { get; set; } = FileSystemContentCollectionFactory.SourceType;
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? BasePath { get; set; }
    public string[] HiddenFrom { get; set; } = [];

    /// <summary>
    /// Maps address types or IDs to this collection.
    /// If null or empty, the collection is not automatically mapped to any address.
    /// </summary>
    public string[]? AddressMappings { get; set; }

    /// <summary>
    /// Function to determine if an address should use this collection.
    /// If null, uses AddressMappings array.
    /// </summary>
    public Func<Messaging.Address, bool>? AddressFilter { get; set; }
}
