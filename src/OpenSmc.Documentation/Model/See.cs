using System.Xml.Serialization;

namespace OpenSmc.Documentation.Model;

public record See
{
    [XmlAttribute("cref")]
    public string Cref { get; init; }
}
