using System.Xml.Serialization;

namespace MeshWeaver.Data.Documentation.Model;

public record See
{
    [XmlAttribute("cref")]
    public string? Cref { get; init; }
}
