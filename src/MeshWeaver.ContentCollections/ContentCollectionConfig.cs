using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;


public class ContentCollectionConfig
{
    public string SourceType { get; set; } = FileSystemContentCollectionFactory.SourceType;
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? BasePath { get; set; }
    public int Order { get; set; }
    public Address? Address { get; set; }
    /// <summary>
    /// Additional provider-specific settings
    /// </summary>
    public Dictionary<string, string>? Settings { get; set; }
}
