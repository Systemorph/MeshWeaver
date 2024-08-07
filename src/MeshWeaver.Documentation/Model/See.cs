using System.Xml.Serialization;

namespace MeshWeaver.Documentation.Model;

public record See
{
    [XmlAttribute("cref")]
    public string Cref { get; init; }
}
