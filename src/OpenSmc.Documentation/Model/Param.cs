using System.Xml.Serialization;

namespace OpenSmc.Documentation.Model;

public record Param
{
    [XmlAttribute("name")]
    public string Name { get; init; }

    [XmlText]
    public string Description { get; init; }
}
