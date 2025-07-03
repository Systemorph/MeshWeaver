using System.Xml.Serialization;

namespace MeshWeaver.Data.Documentation.Model;

public record Param
{
    [XmlAttribute("name")]
    public string? Name { get; init; }

    [XmlText]
    public string? Description { get; init; }
}
